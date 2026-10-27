# net/protocol as upstream has it (Src/StdLib/ruby/4.0/net/protocol.rb, untouched),
# plus the timeouts over TLS that IronRuby's blocking SSLSocket needs
# (ironruby/net_tls.rb).  See Docs/net-http-over-tls.md.

require File.expand_path("../../ruby/4.0/net/protocol", File.dirname(__FILE__))
require "ironruby/net_tls"

IronRuby::NetTLS.install(Net)
