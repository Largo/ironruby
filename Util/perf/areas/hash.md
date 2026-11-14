# Hash/Set perf area (2026-11-14)

Shapes: `Util/perf/areas/hash/shapes.rb` (31 in-process shapes, least-of-3 CPU ms, checksums printed;
all checksums equal CRuby 4.0.9). Machine load was ~40 on 8 cores during the round: shapes this change
does not touch moved by up to +-50% between runs, so only multi-x differences below are results.

## Ratios IronRuby/CRuby on the baseline (b6cd208a), worst first (two runs where they differed)

| shape | ratio | where the time goes |
|---|---|---|
| dig (3 keys) | 10-17x | prelude Ruby `dig(key, *rest)` -> `__dig_step__` -> respond_to? -> `dig(*rest)`: splat arrays + 3 dispatches per level |
| merge (1000 + 5) | 10-17x | Duplicate through initialize_copy/allocate sites, then re-insert; per-call fixed cost on a 1.7 ms CRuby op |
| promote (3-literal then 4th key) | 6-12x | 4th insert into an unlinked 3-entry hash: LinkPendingBuckets, then Resize to 7 (3 arrays) + GC |
| transform_values | 6-7x | prelude Ruby: `each` + block.call + result[k]= per entry |
| literal_small / merge_small | 3-6x | allocation/GC bound (Hash object + Entry[] + int[]); measured 45-300 ms for the same 200k literals in different processes: very GC/heap-state sensitive |
| to_a, uniq, set_add, dup, each/each_pair | 3-5x | allocation of 2-element RubyArrays / block yield per entry |
| aref_arr, aref_str, group_by, tally | 2-4x | #hash/#eql? of Array keys through call sites; String keys hash the bytes each time |
| aref_sym/int/custom, key?, fetch, compare_by_id | 0.4-1.3x | fine |

## Kept

1. `Hash#dig` in C# (HashOps.Dig, overloads for 1/2/3 keys and a params tail): no argument arrays for up
   to 3 keys, Hash/Array values skip the respond_to? call. dig 3 keys: 321-587 ms -> 34-67 ms (CRuby 35);
   1 key: ~90-190 -> ~40-90 (CRuby 21). 2 keys showed ~180-360 in both trees (not improved; unexplained,
   the top-level 2-arg call is slow while the same call made from the 3-key overload is fast).
2. `Hash#transform_values` in C#: 132-216 ms -> 34-95 ms (CRuby 18). Walks self without marking it
   iterated, like MRI's rb_hash_stlike_foreach (the prelude version raised on a key added in the block, MRI does not).
   The no-block enumerator answers #size.

Verified: mspec core/hash 562 examples, identical (0 F, 1 E) on baseline and branch; a probe of dig/transform_values
edge cases (defaults, default procs, nil, TypeError messages, custom #dig, subclass #dig, arity -1/0,
compare_by_identity, break, frozen, adding in the block) matches CRuby line for line.

## Left, ranked

1. Hash literal / small-hash allocation (literal_small, promote, merge_small): Hash has ~16 fields plus
   Entry[] and int[]; a CRuby-ar_table-like inline small storage (no bucket array up to 8 entries, linear
   probe by hash code) would cut two allocations. Biggest breadth, a large contained change in Hash.Storage.cs.
2. promote: when the unlinked hash is full, Resize directly instead of linking buckets first (saves one int[]).
3. merge: Duplicate via dynamic initialize_copy; a plain-Hash fast path (TryCopyEntriesFrom) would skip the sites.
4. to_a/each_pair/map: 2-element RubyArray allocation per pair (arrays area).
5. Array keys (aref_arr 2-3x): Array#hash/#eql? through call sites; a built-in fast path in EqualityComparer for
   RubyArray of built-ins.
