#!/bin/bash
# Windows experiments; runs in the ironruby checkout.
export RUBY_EXE="$(cygpath -w "$PWD/ir.cmd")"

cat > udp.rb <<'RUBY'
require "socket"
require "timeout"
def step(what)
  t = Time.now
  r = Timeout.timeout(5) { yield }
  puts "#{what}: #{r.inspect} (#{(Time.now - t).round(3)}s)"
rescue Timeout::Error
  puts "#{what}: TIMED OUT"
rescue Exception => e
  puts "#{what}: #{e.class}: #{e.message}"
end

server = UDPSocket.new(Socket::AF_INET)
client = UDPSocket.new(Socket::AF_INET)
server.bind("127.0.0.1", 0)
client.connect("127.0.0.1", server.connect_address.ip_port)
client.write("hello")
step("peek recvfrom(2, MSG_PEEK)") { server.recvfrom(2, Socket::MSG_PEEK) }
step("select after peek") { IO.select([server], nil, nil, 1) }
step("recvfrom(2)") { server.recvfrom(2) }
client.write("world")
step("recv(2, MSG_PEEK)") { server.recv(2, Socket::MSG_PEEK) }
step("recv(2)") { server.recv(2) }
client.write("again")
step("recvfrom(10, MSG_PEEK)") { server.recvfrom(10, Socket::MSG_PEEK) }
step("recvfrom(10)") { server.recvfrom(10) }

r, w = IO.pipe
step("read_nonblock empty pipe") { r.read_nonblock(5) }
w.write "abc"
step("read_nonblock with data") { r.read_nonblock(5) }
RUBY

echo "--- CRuby"
ruby udp.rb
echo "--- IronRuby"
timeout 120 cmd //c ir.cmd udp.rb
