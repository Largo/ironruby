# Perf area: web-framework workloads (rails)

Round of 2026-11-14, branch `perf/rails`, baseline PR #216's head b6cd208a.
ruby-bench macro benchmarks run from a copy at `/root/workspace/rails-scratch/ruby-bench`.

**The machine was loaded 15-20 (8 cores, ten agents) the whole time**: every wall-clock
number below is several times what it is on a quiet box (CRuby railsbench took 18 s an
iteration instead of ~2.8 s). Ratios therefore come from IronRuby and CRuby run *at the same
time* (`vs.sh` below), and A/B comparisons from the two variants run at the same time on one
build (`ab.sh`, the old code put back by a `-r` overlay), never from sequential runs.

## Ratio table (IronRuby / CRuby, run concurrently)

| benchmark | IronRuby (perf/rails) | CRuby 4.0.9 | IR/CRuby | earlier (web-gems.md, quiet box) |
|---|---|---|---|---|
| rack | 473 ms | 110 ms | **4.3x** | 7.7x |
| railsbench | 12095 ms | 4125 ms | **2.9x** | 23x |
| activerecord | 1374 ms | 468 ms | **2.9x** | 5.5x |

Averages of the harness's last 6 iterations (WARMUP_ITRS=3) with both engines running at
once; railsbench IronRuby iterations 13.0 -> 11.5 s were still falling at the end. All three
assert their own status codes (railsbench every one of its 2000 requests is a 200).
Sequential, unpaired baseline runs gave rack 1591 vs 288 ms (5.5x).

railsbench did **not run at all** on the baseline: every `/posts.json` request was a 500 (see
fix 1), so the ~20x in `Util/rubybench/web-gems.md` predates a regression. lobsters, sequel,
grape and shipit were not run this round (time: one railsbench iteration pair takes ~20 min
here with start-up).

## Where the time goes

* **rack** (10k requests through 7 middlewares): a flat profile. 13% `CallSite.Target` (the
  DLR rules of ordinary method calls), ~8% GC, `HashOps::GetElement`/`IDictionaryOps::SetElement`
  on the env hash, then ~1% each in every middleware's `call`. Nothing workload-specific: it is
  dispatch (another area's).
* **railsbench** (steady state, after the fix below; 26.5k samples): 15% `CallSite.Target`,
  16% libcoreclr (GC), then a long flat tail of Rails methods. The workload-specific items that
  stood out were all in *crypto/entropy prelude code*, ~11% together:
  - `Random.urandom` 4.1% - the prelude opened, read and closed `/dev/urandom` per call
    (`File#initialize`, `IO#read` in the profile); SecureRandom calls it for the session id,
    CSRF mask, request id and uuid of every request.
  - `OpenSSL::HMAC.digest` 2.3%, nearly all in `HMAC#initialize` building its pads with a block
    and a one-char String per key byte (signed/encrypted cookies, MessageVerifier).
  - `OpenSSL::Cipher#final`/`#initialize` 2% (`IronRubyOpenSSL__.bytes`: a block per byte of
    a CLR byte[]).
  - `Concurrent::Map` 4% (SynchronizedMapBackend: Monitor + `super` inside a block per `[]`).
* No steady-state rebinding or recompilation: `IR_BIND_TRACE=1` on rack shows 6.5k binds in the
  first iteration, 37 in the second, ~0-7 afterwards. (A first profile that seemed to show 30%
  libclrjit at "steady state" was a leftover process from an earlier run being sampled.)

## Kept changes

1. **a10f5c0b - BasicObject subclass + Enumerable/Comparable kept two sets of ivars** (correctness;
   it is what makes railsbench run). 41dda165 made the generated CLR type of a class that gains
   CLR interfaces derive from Object when its base is RubyObject, but a direct subclass of
   BasicObject has `BasicObject` (a RubyObject) as base, so its type implemented IRubyObject a
   second time over its own fields: the first `@x = v` (slow path) went to one, the cached read
   to the other. `class D < BasicObject; include Enumerable; end` lost the first object's first
   ivar - Jbuilder's ScopedIterator (a SimpleDelegator with Enumerable) lost its collection, and
   every railsbench JSON request raised NoMethodError `size`. Test `ClrRubyImpliedInterfaces4`.
2. **8560ad25 - `Random.urandom` via `RandomNumberGenerator.Fill`** (Kernel.__ir_urandom__):
   `Random.urandom(16)` 47 us -> 3.3 us CPU (CRuby 0.26-0.39 us on the same loaded box). The
   remaining cost is .NET's RNG (OpenSSL RAND_bytes plus a `getpid` syscall per call).
3. **a679c80b - OpenSSL HMAC pads by 64-bit words; byte[] -> String via Base64**:
   `HMAC.digest("SHA256", ...)` 134-144 us -> 64 us (SHA1 247 -> 93 us); `bytes` of a 16-byte
   array 3.3 -> 1.0-1.3 us, 512 bytes 71 -> 14-16 us.

railsbench A/B of 2+3 (same build, old code restored by `ab/old.rb`, both variants at once):
run 1 21726 -> 21435 ms (-1.3%), run 2 (10 iterations) 26402 -> 25404 ms (-3.8%); B faster in
16 of 21 paired iterations. The profile share predicts ~5%; the per-iteration spread under this
load is +-10%, so read it as "a few percent, direction consistent", not a precise number.

Verification: `Util/ivar-matrix.rb` identical to CRuby; mspec core/basicobject (3 errors on
both trees), library/delegate, core/comparable, core/random, library/random,
library/securerandom, library/openssl: no failures beyond the baseline's; HMAC hexdigests
(SHA1/SHA256/SHA512 long key/MD5/SHA3-256) and AES-GCM/CBC/CTR ciphertexts identical to CRuby's;
`Random.urandom` answers (0, -1, 3.7, nil, "2", encoding, 1 MiB) identical. IronRuby.Tests:
one failure, `Exception_CauseRaisedInClause1`, which fails identically with the baseline's DLLs
(pre-existing).

## Tried / measured and not changed

* `Concurrent::Map` (4% of railsbench): `[]` is 674 ns here vs 140 ns CRuby (cmap.rb). Monitor
  itself is at par with CRuby (~290 vs ~220 ns); the gap is the layering: Map#[] ->
  SynchronizedMapBackend#[] -> `synchronize { super }` -> NonConcurrentMapBackend#[]. `super`
  inside a block is ~2.4x CRuby's (sup.rb: 511 vs 220 ns) and plain `super` ~1.7x. A
  lock-free read backend is not safe on IronRuby's Hash without a GVL; the real fix is faster
  `super`-in-block (dispatch area) or a native thread-safe map backend.

## What is left, ranked

1. **`CallSite.Target` 13-15% everywhere** - ordinary call dispatch through DLR rules; the
   dispatch area's.
2. **GC ~16% of railsbench** - allocation volume (scopes, arrays for splats, strings in
   tag helpers); profile allocations by site next.
3. **`super` from inside a block** (Concurrent::Map, ActiveSupport callbacks, delegation):
   2.4x CRuby in isolation.
4. **OpenSSL in Ruby**: `HMAC.new` is still ~3x CRuby (three `Digest.new` per HMAC); an
   `IncrementalHash.CreateHMAC` fast path for SHA-1/2 and MD5 would remove most of it.
5. **.NET RNG cost**: `Random.urandom(16)` still ~10x CRuby; a getrandom(2) P/Invoke would match.

## Tools (in this directory)

* `rails/cmap.rb` - Concurrent::Map ops per CPU-ns. `rails/hmac.rb` - HMAC answers and cost.
* `rails/ab.sh`, `rails/vs.sh`, `rails/old.rb` - the A/B and ratio runners (paths inside point at
  /root/workspace/rails-scratch);
  `rails/prof.sh` (perf on a ruby-bench macro
  benchmark at steady state; `-p` the benchmark's own pid, not the subshell's).
