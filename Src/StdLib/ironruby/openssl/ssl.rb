# OpenSSL::SSL::SSLSocket over OpenSSL::SSL::Transport__, which is a .NET
# SslStream on the socket inside the Ruby TCPSocket it is handed.
#
# What that costs, compared with MRI:
#
#   * there is no non-blocking handshake or non-blocking IO.  SslStream drives
#     the socket itself and has no "would block" answer, so #connect_nonblock
#     does the whole handshake and #read_nonblock / #write_nonblock block.
#     They never return :wait_readable, which the callers (net/protocol's
#     BufferedIO, net/http) treat as "the data is here", so they work.
#   * the trust decisions are .NET's: the system trust store, and the
#     certificate's own name check against the SNI hostname.  An
#     SSLContext#cert_store, #ca_file or #verify_callback is not consulted --
#     only #verify_mode, and only for VERIFY_NONE against everything else.
#   * server sockets (SSLServer, #accept) are not implemented.

module OpenSSL
  module SSL
    class SSLError < OpenSSLError; end unless const_defined?(:SSLError, false)

    OP_ALL = 0x80000054
    OP_NO_SSLv2 = 0x01000000
    OP_NO_SSLv3 = 0x02000000
    OP_NO_TLSv1 = 0x04000000
    OP_NO_TLSv1_1 = 0x10000000
    OP_NO_TLSv1_2 = 0x08000000
    OP_NO_TLSv1_3 = 0x20000000
    OP_NO_COMPRESSION = 0x00020000

    TLS1_VERSION = 0x0301
    TLS1_1_VERSION = 0x0302
    TLS1_2_VERSION = 0x0303
    TLS1_3_VERSION = 0x0304

    # MRI's SSLSocket includes this and forwards the socket-level calls that an
    # SslStream does not answer itself to the underlying IO.  net/http sets
    # TCP_NODELAY through it on every connection, so without it no HTTPS request
    # gets off the ground.
    module SocketForwarder
      def addr
        to_io.addr
      end

      def peeraddr
        to_io.peeraddr
      end

      def setsockopt(level, optname, optval)
        to_io.setsockopt(level, optname, optval)
      end

      def getsockopt(level, optname)
        to_io.getsockopt(level, optname)
      end

      def fcntl(*args)
        to_io.fcntl(*args)
      end

      def closed?
        to_io.closed?
      end

      def do_not_reverse_lookup=(flag)
        to_io.do_not_reverse_lookup = flag
      end
    end

    class SSLSocket
      include SocketForwarder

      attr_reader :io, :context
      attr_accessor :hostname, :sync_close, :session

      def initialize(io, context = nil)
        unless io.respond_to?(:fileno)
          raise TypeError, "wrong argument type #{io.class} (expected IO)"
        end
        @io = io
        @context = context || SSLContext.new
        @transport = Transport__.new(io)
        @hostname = nil
        @sync_close = false
        @session = nil
        @connected = false
        @eof = false
        @sync = true
      end

      def connect
        return self if @connected
        verify = @context.respond_to?(:verify_mode) &&
                 @context.verify_mode != OpenSSL::SSL::VERIFY_NONE
        @transport.connect(@hostname.to_s, verify)
        @connected = true
        self
      end

      # There is no partial handshake here, so this is #connect; the argument is
      # accepted because net/protocol's ssl_socket_connect passes exception: false.
      def connect_nonblock(options = nil)
        connect
      end

      def accept
        raise NotImplementedError,
              "OpenSSL::SSL::SSLSocket#accept is not implemented: .NET's SslStream server side is not wired up"
      end
      alias_method :accept_nonblock, :accept

      # MRI checks the certificate against the hostname here; SslStream already
      # did it during the handshake unless verification was off altogether.
      def post_connection_check(hostname)
        return true if @context.respond_to?(:verify_mode) &&
                       @context.verify_mode == OpenSSL::SSL::VERIFY_NONE
        raise SSLError, "hostname \"#{hostname}\" does not match the server certificate" unless
          hostname.to_s.downcase == @hostname.to_s.downcase || @hostname.to_s.empty?
        true
      end

      def peer_cert
        der = @transport.peer_cert
        der.nil? ? nil : OpenSSL::X509::Certificate.new(der)
      end

      def peer_cert_chain
        cert = peer_cert
        cert.nil? ? nil : [cert]
      end

      def ssl_version
        @transport.protocol
      end

      # MRI's #cipher is [name, protocol, secret bits, algorithm bits]; .NET
      # reports the suite's name and nothing else about it.
      def cipher
        name = @transport.cipher
        return nil if name.nil?
        [name, ssl_version, 0, 0]
      end

      def state
        @connected ? "SSLOK " : "SSLNONE"
      end

      def pending
        @transport.pending
      end

      def verify_result
        OpenSSL::X509::V_OK
      end

      def sysread(length, buffer = nil)
        connect unless @connected
        data = @transport.read(length)
        if data.nil?
          @eof = true
          raise EOFError, "end of file reached"
        end
        buffer.nil? ? data : buffer.replace(data)
      end

      def read_nonblock(length, buffer = nil, exception: true)
        sysread(length, buffer)
      rescue EOFError
        raise if exception
        nil
      end

      def readpartial(length, buffer = nil)
        sysread(length, buffer)
      end

      def read(length = nil, buffer = nil)
        result = "".b
        if length.nil?
          loop do
            chunk = (sysread(16384) rescue nil)
            break if chunk.nil?
            result << chunk
          end
        else
          while result.bytesize < length
            chunk = (sysread(length - result.bytesize) rescue nil)
            break if chunk.nil?
            result << chunk
          end
          return nil if result.empty? && length > 0
        end
        buffer.nil? ? result : buffer.replace(result)
      end

      def gets(separator = $/)
        line = "".b
        while (byte = (sysread(1) rescue nil))
          line << byte
          break if separator && line.end_with?(separator)
        end
        line.empty? ? nil : line
      end

      def each_line(separator = $/)
        while (line = gets(separator))
          yield line
        end
        self
      end

      def syswrite(data)
        connect unless @connected
        @transport.write(data)
      end

      def write(*data)
        data.inject(0) { |total, item| total + syswrite(item.to_s) }
      end

      def write_nonblock(data, exception: true)
        syswrite(data)
      end

      def <<(data)
        syswrite(data.to_s)
        self
      end

      def print(*args)
        args.each { |arg| syswrite(arg.to_s) }
        nil
      end

      def puts(*args)
        return syswrite("\n") if args.empty?
        args.each do |arg|
          text = arg.to_s
          syswrite(text.end_with?("\n") ? text : text + "\n")
        end
        nil
      end

      def flush
        self
      end

      def sync
        @sync
      end

      def sync=(value)
        @sync = value
      end

      def eof?
        @eof
      end
      alias_method :eof, :eof?

      def closed?
        @transport.nil? || @io.closed?
      end

      def close
        @transport.close unless @transport.nil?
        @io.close if @sync_close && !@io.closed?
        nil
      end

      def fileno
        @io.fileno
      end

      def to_io
        @io
      end

      def inspect
        "#<#{self.class}:0x%08x>" % (object_id << 1)
      end

      # net/http asks for these on the context object, not here, but code that
      # configures the socket directly expects them to exist.
      def alpn_protocol
        nil
      end

      def npn_protocol
        nil
      end
    end

    # MRI's SSLServer wraps a TCPServer; .NET's server-side SslStream is not
    # wired up here, so the class exists only to be named.
    class SSLServer
      def initialize(*)
        raise NotImplementedError,
              "OpenSSL::SSL::SSLServer is not implemented: only the client side of SslStream is wired up"
      end
    end
  end

  module X509
    V_OK = 0 unless const_defined?(:V_OK, false)
  end
end
