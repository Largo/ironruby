# net/http over TLS: diagnosis and proposed fix

Status: diagnosed, not fixed. Measured on IronRuby 4.0.1, Windows 11, .NET 10.0.12,
2026-10-23.

## The suspicion

`net/http` has a reputation for being undependable on Windows, put down to `IO.select`
on a socket being `poll(2)` there. Applications have worked around it by calling .NET's
`HttpClient` instead.

## What is actually the case

`Util/net-http-tls-check.rb` against `Util/net-http-tls-server.rb` (CRuby) runs keep-alive
GETs and POSTs from 10 bytes to 300 KB, chunked bodies, a fresh connection per request,
and one request that has to end in `Net::ReadTimeout`:

| | plain HTTP | HTTPS, `VERIFY_NONE` | HTTPS, `ca_file:` |
|---|---|---|---|
| CRuby 4.0 | no failures | no failures | no failures |
| IronRuby | no failures | **`read_timeout` never fires** | **every connection refused** |

- **Plain sockets are fine.** `IO.select`, `IO#wait_readable` and
  `read_nonblock` on a socket go through `Socket.Select` on Windows
  (`Src/Libraries/Builtins/IoReadiness.cs`, `BlockWindows`), not `poll(2)`, and
  `net/http`'s read timeout works. What *is* `poll(2)` on Windows is a pipe
  (README, "What Windows does not have yet"); the suspicion mixes the two up.
- **Over TLS nothing times out.** A server that stops answering leaves the caller
  blocked for ever: no `read_timeout`, no `write_timeout`, and no `open_timeout`
  for the TLS handshake. For a service that calls an API in a loop, that is a hang.
- **`ca_file`, `ca_path`, `cert_store` and `verify_callback` are ignored.** Only the
  system trust store counts, so a private CA or a self-signed test server cannot be
  verified, only switched off with `VERIFY_NONE`.

The data itself arrives intact over TLS: no corruption and no short reads were seen.

## Why

`OpenSSL::SSL::SSLSocket` (`Src/StdLib/ironruby/openssl/ssl.rb`) runs on
`OpenSSL::SSL::Transport__` (`Src/Libraries/OpenSSL/OpenSSL.cs`), an `SslStream` over
the TCP socket, which `Transport__.Create` switches to blocking mode. The header of
`ssl.rb` says what follows from that: `#read_nonblock`, `#write_nonblock` and
`#connect_nonblock` block, and never return `:wait_readable`/`:wait_writable`.

`Net::BufferedIO#rbuf_fill` (`net/protocol.rb`) applies `read_timeout` only on the
`:wait_readable` path:

```ruby
case rv = @io.read_nonblock(BUFSIZE, tmp, exception: false)
when String then ...
when :wait_readable
  (io = @io.to_io).wait_readable(@read_timeout) or raise Net::ReadTimeout.new(io)
```

An `SSLSocket` never takes that path, so `read_nonblock` sits in `SslStream.Read` until
data comes. `ssl_socket_connect` (handshake under `open_timeout`) and `write_nonblock`
(under `write_timeout`) have the same shape. `Transport__.Validate` returns
`errors == SslPolicyErrors.None` and never looks at the context's CA settings.

## Proposed fix

There are two stages. The first is small and covers what a client needs; the second
gives real non-blocking I/O.

### Stage 1: timeouts on the blocking calls, and the context's CAs (recommended next)

1. **Read and write timeouts.** `Transport__` gets `read_timeout=` and `write_timeout=`
   (milliseconds, -1 for none), which set `SslStream.ReadTimeout`/`WriteTimeout`.
   They delegate to the inner `NetworkStream`, which means `Socket.ReceiveTimeout`/
   `SendTimeout`. A read that times out throws an `IOException` whose inner
   `SocketException` is `TimedOut`. `Transport__.read`/`write` turn that into
   `IO::TimeoutError`. An `SslStream` is unusable after a timed-out read, so the
   transport marks itself broken and every later call raises.
2. **Where the timeout comes from.** IronRuby keeps a small override of
   `Net::BufferedIO` in its own stdlib layer (`Src/StdLib/ironruby/`, loaded after
   `net/protocol`). When `@io` is an IronRuby `SSLSocket`, `rbuf_fill` sets the
   transport's read timeout from `@read_timeout` before the read and maps
   `IO::TimeoutError` to `Net::ReadTimeout`. `write0` does the same with
   `@write_timeout` and `Net::WriteTimeout`. The upstream file stays untouched, as
   with the other stdlib shims.
3. **Handshake timeout.** `Transport__.connect` takes an optional timeout and calls
   `AuthenticateAsClientAsync(options, cancellationToken)` with a
   `CancellationTokenSource(timeout)`, waiting on the task. `ssl_socket_connect`'s
   timeout reaches it through an override of `SSLSocket#connect_nonblock`, or through
   `net/http` passing `open_timeout` (the override above can do that in `connect`).
4. **CA settings.** `Validate` builds the chain itself when the context has
   `ca_file`, `ca_path` or `cert_store`: `X509ChainPolicy.TrustMode =
   X509ChainTrustMode.CustomRootTrust` with those certificates in `CustomTrustStore`,
   then `chain.Build`, plus the name check SslStream reported (`RemoteCertificateNameMismatch`).
   `verify_callback`, when set, is called with the result and an `X509StoreContext`
   holding the chain, and it has the last word, as in MRI.

Risk: small. Blocking I/O stays as it is, only bounded. What it does not give is real
non-blocking `read_nonblock`, so `IO.select([ssl_socket])` still says "readable" only
when the TCP socket is. A reader that selects first and then reads can block in the
middle of a TLS record. `net/http` doesn't work that way.

### Stage 2: non-blocking reads over SslStream

- `Transport__.read_nonblock(length)` keeps at most one `ReadAsync` in flight. If none is
  in flight, it starts one. If that completes at once (the plaintext was already
  decrypted), it returns the data. Otherwise it returns `:wait_readable` and keeps the
  task. The next call returns the task's result once it has completed.
- Readiness has to see that task: the in-flight read may already have drained the TCP
  socket, so `Socket.Select` would wait although the data is there. `IoReadiness`
  gets a per-`RubyIO` hook ("a pending read has completed") that `Block` checks
  before it selects. It then waits on the task's handle together with the sockets,
  the way `ReadinessWaiter` already stands in for pipes. `SSLSocket#to_io` keeps
  returning the TCP socket, which carries the hook.
- `write_nonblock` and `connect_nonblock` follow the same pattern with `WriteAsync` and
  `AuthenticateAsClientAsync`.

Risk: medium. It touches `IO.select` for every caller, and the in-flight task has to be
cancelled cleanly on `close`. It is worth doing for servers and event loops (nio4r,
puma over TLS), not for a client.

## How to test it

- `Util/net-http-tls-check.rb` (needs CRuby for the server): after stage 1 the
  `no_timeout` entry and, with `CA=1`, the refused connections are gone.
- For CI without CRuby: `IronRuby.Tests` can host the TLS server itself. Create a
  certificate with `CertificateRequest` + `CreateSelfSigned`, accept on a loopback
  `TcpListener`, `SslStream.AuthenticateAsServer`, and stall after the headers for the
  timeout case. The Ruby side then runs `Net::HTTP` with `ca_file:` pointing at the
  exported certificate and `read_timeout: 1` (the test needs `[Options(Prism = true)]`
  to load `net/http`).

## Until then

An application that needs timeouts or its own CA over HTTPS is better served by .NET's
`HttpClient` until stage 1 lands. The reason is not `IO.select` being `poll(2)`; it is
that `net/http` over TLS has no timeouts and ignores `ca_file`.
