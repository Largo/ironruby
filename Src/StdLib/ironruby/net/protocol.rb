# net/protocol as upstream has it (Src/StdLib/ruby/4.0/net/protocol.rb, untouched),
# plus the timeouts over TLS.
#
# Upstream applies open_timeout, read_timeout and write_timeout while it waits after
# connect_nonblock, read_nonblock or write_nonblock answered :wait_readable or
# :wait_writable.  IronRuby's OpenSSL::SSL::SSLSocket is a blocking .NET SslStream
# (openssl/ssl.rb) and never answers those, so without this a server that stops
# answering in the middle of a TLS connection leaves the caller blocked for ever.
# Here the timeout goes to the socket before the call, and the IO::TimeoutError it
# raises when the time runs out becomes the Net:: error upstream would raise.
# Every other kind of socket takes upstream's path.  See Docs/net-http-over-tls.md.

require File.expand_path("../../ruby/4.0/net/protocol", File.dirname(__FILE__))

module Net
  module IronRubyTLS__ # :nodoc: all
    # The method comes first: naming ::OpenSSL::SSL would run net/http's
    # `autoload :OpenSSL`, and a plain TCP connection has no business loading openssl.
    def self.applies?(io)
      io.respond_to?(:__connect) && io.is_a?(::OpenSSL::SSL::SSLSocket)
    end

    module Protocol
      private

      def ssl_socket_connect(s, timeout)
        return super unless IronRubyTLS__.applies?(s)
        raise Net::OpenTimeout if timeout && timeout <= 0
        begin
          s.__connect(timeout)
        rescue IO::TimeoutError
          raise Net::OpenTimeout
        end
      end
    end

    module BufferedIO
      private

      def rbuf_fill
        return super unless IronRubyTLS__.applies?(@io)
        @io.__read_timeout = @read_timeout
        begin
          super
        rescue IO::TimeoutError
          raise Net::ReadTimeout.new(@io.to_io)
        end
      end

      def write0(*strs)
        return super unless IronRubyTLS__.applies?(@io)
        @io.__write_timeout = @write_timeout
        begin
          super
        rescue IO::TimeoutError
          raise Net::WriteTimeout.new(@io.to_io)
        end
      end
    end
  end

  class Protocol
    prepend IronRubyTLS__::Protocol
  end

  class BufferedIO
    prepend IronRubyTLS__::BufferedIO
  end
end
