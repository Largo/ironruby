# net/protocol's timeouts over TLS, for every copy of net/protocol: the standard
# library's Net, and RubyGems' vendored Gem::Net.  See Docs/net-http-over-tls.md.
#
# Upstream applies open_timeout, read_timeout and write_timeout while it waits after
# connect_nonblock, read_nonblock or write_nonblock answered :wait_readable or
# :wait_writable.  IronRuby's OpenSSL::SSL::SSLSocket is a blocking .NET SslStream
# (openssl/ssl.rb) and never answers those, so without this a server that stops
# answering in the middle of a TLS connection leaves the caller blocked for ever.
# Here the timeout goes to the socket before the call, and the IO::TimeoutError it
# raises when the time runs out becomes the error upstream would raise, from the
# copy of net/protocol the caller uses.  Every other kind of socket takes upstream's
# path.
module IronRuby
  module NetTLS # :nodoc: all
    # Protocol and BufferedIO classes => the namespace they belong to.
    @namespaces = {}

    class << self
      # The method comes first: naming ::OpenSSL::SSL would run net/http's
      # `autoload :OpenSSL`, and a plain TCP connection has no business loading openssl.
      def applies?(io)
        io.respond_to?(:__connect) && io.is_a?(::OpenSSL::SSL::SSLSocket)
      end

      # +net+ is a namespace with net/protocol's Protocol, BufferedIO and timeout errors.
      def install(net)
        return if @namespaces.value?(net)
        @namespaces[net::Protocol] = net
        @namespaces[net::BufferedIO] = net
        net::Protocol.prepend(Protocol)
        net::BufferedIO.prepend(BufferedIO)
      end

      # RubyGems loads its copy with require_relative, which nothing can hook.  An
      # SSLSocket being made is the last moment before that copy's first handshake:
      # Gem::Net::HTTP#connect makes the socket and then calls ssl_socket_connect.
      def install_vendored
        return unless defined?(::Gem) && ::Gem.const_defined?(:Net, false)
        return if ::Gem.respond_to?(:autoload?) && ::Gem.autoload?(:Net)
        net = ::Gem::Net
        install(net) if net.const_defined?(:Protocol, false) && net.const_defined?(:BufferedIO, false)
      end

      def namespace(object)
        @namespaces.each { |klass, net| return net if object.is_a?(klass) }
        ::Net
      end
    end

    module Protocol
      private

      def ssl_socket_connect(s, timeout)
        return super unless NetTLS.applies?(s)
        net = NetTLS.namespace(self)
        raise net::OpenTimeout if timeout && timeout <= 0
        begin
          s.__connect(timeout)
        rescue IO::TimeoutError
          raise net::OpenTimeout
        end
      end
    end

    module BufferedIO
      private

      def rbuf_fill
        return super unless NetTLS.applies?(@io)
        @io.__read_timeout = @read_timeout
        begin
          super
        rescue IO::TimeoutError
          raise NetTLS.namespace(self)::ReadTimeout.new(@io.to_io)
        end
      end

      def write0(*strs)
        return super unless NetTLS.applies?(@io)
        @io.__write_timeout = @write_timeout
        begin
          super
        rescue IO::TimeoutError
          raise NetTLS.namespace(self)::WriteTimeout.new(@io.to_io)
        end
      end
    end
  end
end
