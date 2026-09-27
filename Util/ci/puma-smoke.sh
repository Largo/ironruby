#!/bin/bash
# Boots puma under IronRuby on 127.0.0.1 with a small rack app, talks HTTP to it with
# curl - GET, POST (small and one big enough for puma to spool it to a Tempfile),
# a keep-alive connection, concurrent clients - then stops it through puma's control
# server and checks that it shut down cleanly.  Runs from the repository root on Linux
# and, under Git Bash, on Windows, where it is the only thing that drives nio4r's
# Socket.Select backend and IO.select on real sockets.
#
#   Util/ci/puma-smoke.sh [port]
set -u
PORT=${1:-9292}
CTL=$((PORT + 1))
IR="./ir.sh"
[ "${RUNNER_OS:-}" = Windows ] || [ "${OS:-}" = Windows_NT ] && IR="cmd //c ir.cmd"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/config.ru" <<'RUBY'
count = 0
lock = Mutex.new
run lambda { |env|
  n = lock.synchronize { count += 1 }
  input = env["rack.input"].read
  body = "#{env["REQUEST_METHOD"]} #{env["PATH_INFO"]} #{env["QUERY_STRING"]} #{input.bytesize} #{input[0, 16]}\n"
  [200, {"content-type" => "text/plain", "x-request-count" => n.to_s}, [body]]
}
RUBY

fail() {
  echo "FAILED: $*"
  echo "::group::puma log"; cat "$WORK/puma.log"; echo "::endgroup::"
  kill "$PID" 2>/dev/null
  exit 1
}

check() { # expected actual what
  if [ "$1" = "$2" ]; then echo "ok   $3: $2"; else fail "$3: expected [$1], got [$2]"; fi
}

$IR -S puma --bind "tcp://127.0.0.1:$PORT" --threads 1:4 \
  --control-url "tcp://127.0.0.1:$CTL" --control-token ci \
  "$WORK/config.ru" > "$WORK/puma.log" 2>&1 &
PID=$!

up=
for _ in $(seq 1 120); do
  if curl -s -o /dev/null "http://127.0.0.1:$PORT/"; then up=1; break; fi
  kill -0 "$PID" 2>/dev/null || fail "puma exited before it answered"
  sleep 0.5
done
[ -n "$up" ] || fail "puma did not answer within 60s"
echo "::group::puma boot log"; cat "$WORK/puma.log"; echo "::endgroup::"

check "GET /hello a=1&b=2 0 " "$(curl -sS 'http://127.0.0.1:'$PORT'/hello?a=1&b=2' | tr -d '\r')" "GET"
check "POST /form  13 name=IronRuby" "$(curl -sS -d 'name=IronRuby' "http://127.0.0.1:$PORT/form" | tr -d '\r')" "POST"

# Bigger than puma's 112 KiB in-memory limit: the body goes through a Tempfile.
head -c 2000000 /dev/zero | tr '\0' 'x' > "$WORK/big.txt"
check "PUT /big  2000000 xxxxxxxxxxxxxxxx" \
  "$(curl -sS -X PUT --data-binary "@$WORK/big.txt" "http://127.0.0.1:$PORT/big" | tr -d '\r')" "big PUT"

# Keep-alive: three requests on one curl invocation. num_connects is 1 for the request
# that opened the connection and 0 for each one that reused it.
check "1 0 0" "$(curl -sS -w '%{num_connects} ' -o /dev/null "http://127.0.0.1:$PORT/a" \
  -o /dev/null "http://127.0.0.1:$PORT/b" -o /dev/null "http://127.0.0.1:$PORT/c" | xargs)" "keep-alive connects"
check "HTTP/1.1 200 OK" "$(curl -sS -i "http://127.0.0.1:$PORT/h" | head -1 | tr -d '\r')" "status line"

# Concurrent clients, more of them than puma has threads.
seq 1 24 | xargs -P 8 -I{} curl -sS -o "$WORK/c{}.out" "http://127.0.0.1:$PORT/c{}"
ok=0
for i in $(seq 1 24); do
  grep -q "^GET /c$i " "$WORK/c$i.out" 2>/dev/null && ok=$((ok + 1))
done
check 24 "$ok" "concurrent requests answered"

check '{ "status": "ok" }' \
  "$(curl -sS "http://127.0.0.1:$CTL/stop?token=ci")" "control stop"
stopped=
for _ in $(seq 1 60); do
  if ! kill -0 "$PID" 2>/dev/null; then stopped=1; break; fi
  sleep 0.5
done
[ -n "$stopped" ] || fail "puma still running 30s after stop"
wait "$PID"
status=$?
echo "::group::puma log"; cat "$WORK/puma.log"; echo "::endgroup::"
check 0 "$status" "puma exit status"
grep -q "Goodbye!" "$WORK/puma.log" || fail "puma did not say Goodbye!"
echo "puma smoke test passed"
