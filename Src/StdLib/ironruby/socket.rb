# ****************************************************************************
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Apache License, Version 2.0, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Sockets'

# Addrinfo.  CRuby implements this in C over getaddrinfo(3); here it is a thin
# Ruby object over the (family, port, hostname, ip) tuple that IPSocket#addr
# already produces and over IPSocket.getaddress for name resolution.  Only the
# IP families are supported - there is no AF_UNIX socket class in IronRuby.
class Addrinfo
  # sockaddr may be the ["AF_INET", port, hostname, ip] array that
  # IPSocket#addr returns, or a [host, port] pair, or a bare IP string.
  def initialize(sockaddr, family = nil, socktype = 0, protocol = 0)
    case sockaddr
    when Array
      if sockaddr.size >= 3
        af = sockaddr[0]
        @ip_port = sockaddr[1].to_i
        @ip_address = (sockaddr[3] || sockaddr[2]).to_s
      else
        af = family
        @ip_address = sockaddr[0].to_s
        @ip_port = sockaddr[1].to_i
      end
    when String
      af = family
      @ip_address = sockaddr
      @ip_port = 0
    else
      raise ArgumentError, "unsupported sockaddr: #{sockaddr.inspect}"
    end

    @afamily = Addrinfo.__af(af, @ip_address)
    @pfamily = family ? Addrinfo.__af(family, @ip_address) : @afamily
    @socktype = socktype.to_i
    @protocol = protocol.to_i
    @canonname = nil
  end

  attr_reader :afamily, :pfamily, :socktype, :protocol, :canonname

  # Normalises "AF_INET" / :INET / Socket::AF_INET / nil to a family number.
  def self.__af(af, address = nil) # :nodoc:
    case af
    when nil
      (address && address.include?(":")) ? Socket::AF_INET6 : Socket::AF_INET
    when Integer then af
    when Symbol, String
      name = af.to_s.sub(/\A(AF_|PF_)/, "").upcase
      case name
      when "INET" then Socket::AF_INET
      when "INET6" then Socket::AF_INET6
      when "UNSPEC" then Socket::AF_UNSPEC
      else
        const = "AF_#{name}"
        if Socket.const_defined?(const)
          Socket.const_get(const)
        else
          raise SocketError, "unknown socket address family: #{af}"
        end
      end
    else
      raise SocketError, "unknown socket address family: #{af.inspect}"
    end
  end

  def self.__resolve(host) # :nodoc:
    return "0.0.0.0" if host.nil?
    host = host.to_s
    return host if host.empty?
    begin
      IPSocket.getaddress(host)
    rescue StandardError
      host
    end
  end

  def self.tcp(host, port)
    new([nil, port.to_i, host, __resolve(host)], nil, Socket::SOCK_STREAM, Socket::IPPROTO_TCP)
  end

  def self.udp(host, port)
    new([nil, port.to_i, host, __resolve(host)], nil, Socket::SOCK_DGRAM, Socket::IPPROTO_UDP)
  end

  def self.ip(host)
    new([nil, 0, host, __resolve(host)], nil, 0, 0)
  end

  def ip?
    @afamily == Socket::AF_INET || @afamily == Socket::AF_INET6
  end

  def ipv4?
    @afamily == Socket::AF_INET
  end

  def ipv6?
    @afamily == Socket::AF_INET6
  end

  def unix?
    false
  end

  def ip_address
    raise SocketError, "need IPv4 or IPv6 address" unless ip?
    @ip_address
  end

  def ip_port
    raise SocketError, "need IPv4 or IPv6 address" unless ip?
    @ip_port
  end

  def ip_unpack
    [ip_address, ip_port]
  end

  def ipv4_loopback?
    ipv4? && @ip_address.start_with?("127.")
  end

  def ipv4_private?
    return false unless ipv4?
    a, b, = @ip_address.split(".").map { |x| x.to_i }
    a == 10 || (a == 172 && (16..31).include?(b)) || (a == 192 && b == 168)
  end

  def ipv4_multicast?
    ipv4? && (224..239).include?(@ip_address.split(".").first.to_i)
  end

  def ipv6_loopback?
    ipv6? && (@ip_address == "::1")
  end

  def afamily_name # :nodoc:
    case @afamily
    when Socket::AF_INET then "AF_INET"
    when Socket::AF_INET6 then "AF_INET6"
    else "AF_UNSPEC"
    end
  end

  def inspect_sockaddr
    ipv6? ? "[#{@ip_address}]:#{@ip_port}" : "#{@ip_address}:#{@ip_port}"
  end

  def to_s
    inspect_sockaddr
  end

  def to_sockaddr
    Socket.sockaddr_in(@ip_port, @ip_address)
  end
  def inspect
    "#<Addrinfo: #{inspect_sockaddr}#{@socktype == Socket::SOCK_STREAM ? ' TCP' : ''}>"
  end

  def to_a # :nodoc:
    [afamily_name, @ip_port, @ip_address, @ip_address]
  end

  def ==(other)
    other.kind_of?(Addrinfo) &&
      other.afamily == @afamily && other.ip_port == @ip_port &&
      other.ip_address == @ip_address && other.socktype == @socktype
  end
  alias_method :eql?, :==

  def hash
    [@afamily, @ip_port, @ip_address, @socktype].hash
  end

  def family_addrinfo(*args)
    if args.size == 2
      Addrinfo.tcp(args[0], args[1])
    else
      Addrinfo.tcp(args[0], 0)
    end
  end

  # Returns a listening TCPServer bound to this address.  CRuby returns a
  # Socket; TCPServer is IronRuby's only listening socket class and answers the
  # same #accept / #local_address / #close protocol.
  def listen(backlog = Socket::SOMAXCONN)
    server = TCPServer.new(@ip_address, @ip_port)
    begin
      server.listen(backlog)
    rescue StandardError
      # TCPServer.new already listens; a second listen() is harmless if it fails
    end
    return server unless block_given?
    begin
      yield server
    ensure
      server.close unless server.closed?
    end
  end

  def connect(timeout: nil) # :yield: socket
    sock = TCPSocket.new(@ip_address, @ip_port)
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end

  def connect_from(*args, &block)
    local = args.size == 1 ? args[0] : Addrinfo.tcp(args[0], args[1] || 0)
    local = Addrinfo.tcp(local, 0) unless local.kind_of?(Addrinfo)
    sock = TCPSocket.new(@ip_address, @ip_port, local.ip_address, local.ip_port)
    return sock unless block
    begin
      block.call(sock)
    ensure
      sock.close unless sock.closed?
    end
  end

  def bind # :yield: socket
    sock = @socktype == Socket::SOCK_DGRAM ? UDPSocket.new : TCPServer.new(@ip_address, @ip_port)
    sock.bind(@ip_address, @ip_port) if @socktype == Socket::SOCK_DGRAM
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end
end

class BasicSocket
  # CRuby returns an Addrinfo built from getsockname(2)/getpeername(2).  The
  # IPSocket#addr / #peeraddr tuples carry the same information.
  def local_address
    Addrinfo.new(addr, nil, __ir_socktype, 0)
  end

  def remote_address
    Addrinfo.new(peeraddr, nil, __ir_socktype, 0)
  end

  def __ir_socktype # :nodoc:
    kind_of?(UDPSocket) ? Socket::SOCK_DGRAM : Socket::SOCK_STREAM
  end
  private :__ir_socktype
end

# Socket.tcp / Socket.tcp_server_loop and friends are written in Ruby in CRuby
# too (ext/socket/lib/socket.rb).  CRuby drives them through Addrinfo, which we
# do not have; TCPSocket/TCPServer already perform the same name resolution
# internally, so they stand in for it here.
class Socket
  def self.tcp(host, port, local_host = nil, local_port = nil,
               connect_timeout: nil, resolv_timeout: nil) # :yield: socket
    sock = if local_host || local_port
      TCPSocket.new(host, port, local_host, local_port)
    else
      TCPSocket.new(host, port)
    end
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end

  def self.tcp_server_sockets(host = nil, port = nil)
    host, port = nil, host if port.nil?
    server = host ? TCPServer.new(host, port) : TCPServer.new(port)
    sockets = [server]
    return sockets unless block_given?
    begin
      yield sockets
    ensure
      sockets.each { |s| s.close unless s.closed? }
    end
  end

  def self.accept_loop(*sockets) # :yield: socket, client_addrinfo
    sockets.flatten!
    loop do
      readable = IO.select(sockets)[0]
      readable.each do |server|
        begin
          sock = server.accept_nonblock
        rescue IO::WaitReadable, Errno::EINTR, Errno::ECONNABORTED, Errno::EPROTO
          next
        end
        yield sock
      end
    end
  end

  def self.tcp_server_loop(host = nil, port = nil, &block) # :yield: socket, client_addrinfo
    tcp_server_sockets(host, port) { |sockets| accept_loop(sockets, &block) }
  end
end

# ---------------------------------------------------------------------------
# Control-message and socket-option constants that .NET has no equivalent for.
#
# Socket::Constants normally carries .NET's *winsock* numbering, because the
# values are handed straight to Socket.SetSocketOption, which maps its own enum
# to the platform ABI internally (see Socket.cs).  The names below have no
# SocketOptionName member at all, so nothing ever passes them to .NET; they
# exist only as cmsg/option identifiers.  Linux values are therefore both
# harmless and the most useful choice.
class Socket
  module Constants
  end

  {
    "SCM_RIGHTS"      => 1,
    "SCM_CREDENTIALS" => 2,
    "SCM_TIMESTAMP"   => 29,
    "IP_RECVTTL"      => 12,
    "IP_MTU"          => 14,
    "IPV6_CHECKSUM"   => 7,
    "IPV6_NEXTHOP"    => 9,
    "TCP_CORK"        => 3,
    "TCP_INFO"        => 11,
    "TCP_MAXSEG"      => 2,
    "UDP_CORK"        => 1,
    "EAI_ADDRFAMILY"  => -9,
  }.each do |name, value|
    # Socket copies Socket::Constants at class-definition time (Includes(Copy
    # = true) in Socket.cs), so a late addition has to be set on both.
    Constants.const_set(name, value) unless Constants.const_defined?(name, false)
    const_set(name, value) unless const_defined?(name, false)
  end

  # Raised by name resolution.  CRuby made this a SocketError subclass in 3.0.
  class ResolutionError < SocketError; end unless const_defined?(:ResolutionError, false)

  class << self
    # --- argument coercion shared by Socket::Option and Socket::AncillaryData ---
    #
    # CRuby accepts an Integer, or a Symbol/String naming the constant with the
    # family/level-specific prefix optional.  An unknown *name* is a SocketError;
    # something that is neither an Integer nor string-like is a TypeError.

    def __ir_name(value, what) # :nodoc:
      case value
      when Symbol then value.to_s
      when String then value
      else
        unless value.respond_to?(:to_str)
          raise TypeError, "no implicit conversion of #{value.class} into #{what}"
        end
        str = value.to_str
        raise TypeError, "no implicit conversion of #{value.class} into String" unless str.kind_of?(String)
        str
      end
    end

    def __ir_lookup(name) # :nodoc:
      return nil unless name =~ /\A[A-Z][A-Za-z0-9_]*\z/
      const_defined?(name, false) ? const_get(name) : nil
    end

    def __ir_family_arg(value) # :nodoc:
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      found = __ir_lookup("AF_#{name.upcase.sub(/\A(AF_|PF_)/, "")}")
      raise SocketError, "unknown socket domain: #{name}" if found.nil?
      found
    end

    def __ir_level_arg(family, value) # :nodoc:
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      upper = name.upcase
      return SOL_SOCKET if upper == "SOCKET" || upper == "SOL_SOCKET"
      if family == AF_INET || family == AF_INET6 || family == AF_UNSPEC
        upper = "IPPROTO_#{upper}" unless upper.start_with?("IPPROTO_")
        found = __ir_lookup(upper)
        return found if found
      end
      raise SocketError, "unknown protocol level: #{name}"
    end

    # SO_ / IP_ / IPV6_ / TCP_ / UDP_ depending on the level.
    def __ir_optname_prefix(level) # :nodoc:
      case level
      when SOL_SOCKET then "SO_"
      when IPPROTO_IP then "IP_"
      when IPPROTO_IPV6 then "IPV6_"
      when IPPROTO_TCP then "TCP_"
      when IPPROTO_UDP then "UDP_"
      end
    end

    def __ir_optname_arg(level, value) # :nodoc:
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      prefix = __ir_optname_prefix(level)
      if prefix
        upper = name.upcase
        upper = prefix + upper unless upper.start_with?(prefix)
        found = __ir_lookup(upper)
        return found if found
      end
      raise SocketError, "unknown socket level option name: #{name}"
    end

    # Control-message types live in a different namespace: SCM_ at SOL_SOCKET,
    # otherwise the protocol's own.  At a level with no cmsg namespace CRuby
    # insists on an Integer rather than reporting an unknown name.
    def __ir_cmsg_prefix(level) # :nodoc:
      level == SOL_SOCKET ? "SCM_" : __ir_optname_prefix(level)
    end

    def __ir_level_label(level) # :nodoc:
      case level
      when SOL_SOCKET then "socket level"
      when IPPROTO_IP then "IP"
      when IPPROTO_IPV6 then "IPv6"
      when IPPROTO_TCP then "TCP"
      when IPPROTO_UDP then "UDP"
      else level.to_s
      end
    end

    def __ir_cmsg_type_arg(level, value) # :nodoc:
      return value if value.kind_of?(Integer)
      prefix = __ir_cmsg_prefix(level)
      if prefix.nil?
        raise TypeError, "no implicit conversion of #{value.class} into Integer"
      end
      name = __ir_name(value, "Integer")
      upper = name.upcase
      upper = prefix + upper unless upper.start_with?(prefix)
      found = __ir_lookup(upper)
      return found if found
      raise SocketError, "unknown #{__ir_level_label(level)} control message: #{name}"
    end

    # --- reverse mapping, for #inspect ---

    def __ir_family_name(family) # :nodoc:
      case family
      when AF_UNSPEC then "UNSPEC"
      when AF_INET then "INET"
      when AF_INET6 then "INET6"
      when AF_UNIX then "UNIX"
      else family.to_s
      end
    end

    def __ir_level_name(level) # :nodoc:
      case level
      when SOL_SOCKET then "SOCKET"
      when IPPROTO_IP then "IP"
      when IPPROTO_IPV6 then "IPV6"
      when IPPROTO_TCP then "TCP"
      when IPPROTO_UDP then "UDP"
      else level.to_s
      end
    end

    def __ir_const_name(prefix, value) # :nodoc:
      return value.to_s if prefix.nil?
      name = constants.find do |c|
        c.to_s.start_with?(prefix) && (const_get(c) rescue nil) == value
      end
      name ? name.to_s[prefix.size..-1] : value.to_s
    end

    def __ir_optname_name(level, optname) # :nodoc:
      __ir_const_name(__ir_optname_prefix(level), optname)
    end

    def __ir_cmsg_type_name(level, type) # :nodoc:
      __ir_const_name(__ir_cmsg_prefix(level), type)
    end

    # --- IPv6 text <-> 16 bytes, needed by Socket::AncillaryData.ipv6_pktinfo ---

    def __ir_pack_ipv6(address) # :nodoc:
      text = address.to_s.split("%").first.to_s
      head, tail = text.include?("::") ? text.split("::", -1) : [text, nil]
      parts = ->(s) { s.nil? || s.empty? ? [] : s.split(":") }
      left = parts.call(head)
      right = parts.call(tail)
      # A trailing dotted quad counts as two groups.
      [left, right].each do |groups|
        if groups.last && groups.last.include?(".")
          quad = groups.pop.split(".").map { |d| d.to_i }
          groups << ((quad[0] << 8) | quad[1]).to_s(16)
          groups << ((quad[2] << 8) | quad[3]).to_s(16)
        end
      end
      fill = 8 - left.size - right.size
      raise ArgumentError, "invalid IPv6 address: #{address}" if fill < 0
      fill = 0 if tail.nil?
      words = left.map { |g| g.to_i(16) } + [0] * fill + right.map { |g| g.to_i(16) }
      raise ArgumentError, "invalid IPv6 address: #{address}" unless words.size == 8
      words.pack("n8")
    end

    def __ir_unpack_ipv6(bytes) # :nodoc:
      words = bytes.unpack("n8")
      # RFC 5952: compress the leftmost longest run of two or more zero groups.
      best_at = nil
      best_len = 1
      at = nil
      words.each_with_index do |w, i|
        if w == 0
          at ||= i
          if i - at + 1 > best_len
            best_len = i - at + 1
            best_at = at
          end
        else
          at = nil
        end
      end
      return words.map { |w| w.to_s(16) }.join(":") if best_at.nil?
      left = words[0, best_at].map { |w| w.to_s(16) }.join(":")
      right = words[(best_at + best_len)..-1].map { |w| w.to_s(16) }.join(":")
      "#{left}::#{right}"
    end
  end

  # Socket::Option -- a value object over (family, level, optname, data).  CRuby
  # implements it in C; nothing about it needs a syscall.
  class Option
    def initialize(family, level, optname, data)
      @family = Socket.__ir_family_arg(family)
      @level = Socket.__ir_level_arg(@family, level)
      @optname = Socket.__ir_optname_arg(@level, optname)
      unless data.kind_of?(String)
        unless data.respond_to?(:to_str)
          raise TypeError, "no implicit conversion of #{data.class} into String"
        end
        data = data.to_str
      end
      @data = data.dup
    end

    attr_reader :family, :level, :optname, :data

    def self.int(family, level, optname, integer)
      new(family, level, optname, [Integer(integer)].pack("i"))
    end

    def self.bool(family, level, optname, bool)
      new(family, level, optname, [bool ? 1 : 0].pack("i"))
    end

    def self.linger(onoff, secs)
      onoff = case onoff
              when true then 1
              when false, nil then 0
              else Integer(onoff)
              end
      new(Socket::AF_UNSPEC, Socket::SOL_SOCKET, Socket::SO_LINGER,
          [onoff, Integer(secs)].pack("i2"))
    end

    def __ir_check_size(expected, what) # :nodoc:
      return if @data.bytesize == expected
      raise TypeError, "size differ.  expected as sizeof(#{what})=#{expected} but #{@data.bytesize}"
    end
    private :__ir_check_size

    def int
      __ir_check_size(4, "int")
      @data.unpack("i")[0]
    end

    def bool
      __ir_check_size(4, "int")
      @data.unpack("i")[0] != 0
    end

    def linger
      unless @level == Socket::SOL_SOCKET && @optname == Socket::SO_LINGER
        raise TypeError, "linger socket option expected"
      end
      __ir_check_size(8, "struct linger")
      onoff, secs = @data.unpack("i2")
      [onoff != 0, secs]
    end

    def unpack(format)
      @data.unpack(format)
    end

    def to_s
      @data
    end

    def inspect
      "#<Socket::Option: #{Socket.__ir_family_name(@family)} " \
      "#{Socket.__ir_level_name(@level)} " \
      "#{Socket.__ir_optname_name(@level, @optname)} #{__ir_inspect_data}>"
    end

    def __ir_inspect_data # :nodoc:
      if @level == Socket::SOL_SOCKET && @optname == Socket::SO_LINGER && @data.bytesize == 8
        onoff, secs = @data.unpack("i2")
        "#{onoff == 0 ? "off" : "on"} #{secs}sec"
      elsif @level == Socket::SOL_SOCKET && @data.bytesize == 4
        @data.unpack("i")[0].to_s
      else
        @data.inspect
      end
    end
    private :__ir_inspect_data
  end

  # Socket::AncillaryData -- also a pure value object.  Constructing one takes no
  # syscall; only handing it to sendmsg/recvmsg would, and .NET 8 exposes no
  # cmsg surface for that (see Docs).
  class AncillaryData
    def initialize(family, level, type, data)
      @family = Socket.__ir_family_arg(family)
      @level = Socket.__ir_level_arg(@family, level)
      @type = Socket.__ir_cmsg_type_arg(@level, type)
      unless data.kind_of?(String)
        unless data.respond_to?(:to_str)
          raise TypeError, "no implicit conversion of #{data.class} into String"
        end
        data = data.to_str
      end
      @data = data.dup
      @unix_rights = nil
    end

    attr_reader :family, :level, :type, :data

    def self.int(family, level, type, integer)
      new(family, level, type, [Integer(integer)].pack("i"))
    end

    def self.unix_rights(*ios)
      ios.each do |io|
        raise TypeError, "IO expected" unless io.kind_of?(::IO)
      end
      data = new(Socket::AF_UNIX, Socket::SOL_SOCKET, Socket::SCM_RIGHTS,
                 ios.map { |io| io.fileno }.pack("i*"))
      data.__ir_set_unix_rights(ios)
      data
    end

    # struct in_pktinfo { int ipi_ifindex; struct in_addr ipi_spec_dst; struct in_addr ipi_addr; }
    def self.ip_pktinfo(addr, ifindex, spec_dst = addr)
      packed = [Integer(ifindex)].pack("i") +
               __ir_pack_ipv4(spec_dst) + __ir_pack_ipv4(addr)
      new(Socket::AF_INET, Socket::IPPROTO_IP, Socket::IP_PKTINFO, packed)
    end

    # struct in6_pktinfo { struct in6_addr ipi6_addr; unsigned int ipi6_ifindex; }
    def self.ipv6_pktinfo(addr, ifindex)
      packed = Socket.__ir_pack_ipv6(addr.ip_address) + [Integer(ifindex)].pack("I")
      new(Socket::AF_INET6, Socket::IPPROTO_IPV6, Socket::IPV6_PKTINFO, packed)
    end

    def self.__ir_pack_ipv4(addr) # :nodoc:
      addr.ip_address.split(".").map { |o| o.to_i }.pack("C4")
    end

    def __ir_set_unix_rights(ios) # :nodoc:
      @unix_rights = ios
    end

    def int
      unless @data.bytesize == 4
        raise TypeError, "size differ.  expected as sizeof(int)=4 but #{@data.bytesize}"
      end
      @data.unpack("i")[0]
    end

    def unix_rights
      unless @level == Socket::SOL_SOCKET && @type == Socket::SCM_RIGHTS
        raise TypeError, "SCM_RIGHTS ancillary data expected"
      end
      @unix_rights
    end

    def ip_pktinfo
      unless @level == Socket::IPPROTO_IP && @type == Socket::IP_PKTINFO
        raise TypeError, "IP_PKTINFO ancillary data expected"
      end
      ifindex = @data.unpack("i")[0]
      spec_dst = @data.byteslice(4, 4).unpack("C4").join(".")
      addr = @data.byteslice(8, 4).unpack("C4").join(".")
      [Addrinfo.ip(addr), ifindex, Addrinfo.ip(spec_dst)]
    end

    def ipv6_pktinfo
      unless @level == Socket::IPPROTO_IPV6 && @type == Socket::IPV6_PKTINFO
        raise TypeError, "IPV6_PKTINFO ancillary data expected"
      end
      [Addrinfo.ip(Socket.__ir_unpack_ipv6(@data.byteslice(0, 16))),
       @data.byteslice(16, 4).unpack("I")[0]]
    end

    def ipv6_pktinfo_addr
      ipv6_pktinfo[0]
    end

    def ipv6_pktinfo_ifindex
      ipv6_pktinfo[1]
    end

    def cmsg_is?(level, type)
      lvl = Socket.__ir_level_arg(@family, level)
      typ = Socket.__ir_cmsg_type_arg(lvl, type)
      @level == lvl && @type == typ
    end

    def inspect
      "#<Socket::AncillaryData: #{Socket.__ir_family_name(@family)} " \
      "#{Socket.__ir_level_name(@level)} " \
      "#{Socket.__ir_cmsg_type_name(@level, @type)} #{@data.inspect}>"
    end
  end
end

class Socket
  # sockaddr_un packing is pure byte work, so it does not need the AF_UNIX
  # socket classes IronRuby still lacks.  Linux layout: 2-byte family, then a
  # 108-byte NUL-padded path.
  SUN_PATH_MAX__ = 108 unless const_defined?(:SUN_PATH_MAX__, false)

  def self.sockaddr_un(path)
    path = Addrinfo === path ? path.unix_path : path
    path = path.to_str unless path.kind_of?(String)
    bytes = path.dup.force_encoding(Encoding::BINARY)
    if bytes.bytesize >= SUN_PATH_MAX__
      raise ArgumentError,
            "too long unix socket path (#{bytes.bytesize} bytes given but #{SUN_PATH_MAX__ - 1} bytes max)"
    end
    [AF_UNIX].pack("S") + bytes + ("\0" * (SUN_PATH_MAX__ - bytes.bytesize))
  end

  class << self
    alias_method :pack_sockaddr_un, :sockaddr_un
  end

  def self.unpack_sockaddr_un(sockaddr)
    if Addrinfo === sockaddr
      raise ArgumentError, "not an AF_UNIX sockaddr" unless sockaddr.unix?
      return sockaddr.unix_path
    end
    bytes = sockaddr.to_str.dup.force_encoding(Encoding::BINARY)
    raise ArgumentError, "too short sockaddr" if bytes.bytesize < 2
    unless bytes.unpack("S")[0] == AF_UNIX
      raise ArgumentError, "not an AF_UNIX sockaddr"
    end
    path = bytes.byteslice(2, bytes.bytesize - 2)
    nul = path.index("\0")
    nul ? path.byteslice(0, nul) : path
  end
end

# Minimal AF_UNIX support in Addrinfo -- enough for Socket.unpack_sockaddr_un.
# There is still no UNIXSocket class, so #listen/#connect stay unsupported.
class Addrinfo
  def self.unix(path, socktype = Socket::SOCK_STREAM)
    info = allocate
    info.__ir_init_unix(path.to_s, socktype)
    info
  end

  def __ir_init_unix(path, socktype) # :nodoc:
    @afamily = Socket::AF_UNIX
    @pfamily = Socket::AF_UNIX
    @socktype = socktype
    @protocol = 0
    @canonname = nil
    @ip_address = nil
    @ip_port = nil
    @unix_path = path
  end

  def unix?
    @afamily == Socket::AF_UNIX
  end

  def unix_path
    raise SocketError, "need AF_UNIX address" unless unix?
    @unix_path
  end
end

class IO
  # CRuby raises these from the *_nonblock family; they are plain Errno
  # subclasses tagged with the WaitReadable/WaitWritable marker modules.
  {
    "EAGAINWaitReadable" => [Errno::EAGAIN, WaitReadable],
    "EAGAINWaitWritable" => [Errno::EAGAIN, WaitWritable],
    "EWOULDBLOCKWaitReadable" => [Errno::EWOULDBLOCK, WaitReadable],
    "EWOULDBLOCKWaitWritable" => [Errno::EWOULDBLOCK, WaitWritable],
    "EINPROGRESSWaitReadable" => [Errno::EINPROGRESS, WaitReadable],
    "EINPROGRESSWaitWritable" => [Errno::EINPROGRESS, WaitWritable],
  }.each do |name, (parent, marker)|
    next if const_defined?(name, false)
    const_set(name, Class.new(parent) { include marker })
  end

  class TimeoutError < IOError; end unless const_defined?(:TimeoutError, false)
end

# Symbol/String arguments.  CRuby accepts :INET / "INET" / :AF_INET wherever it
# accepts Socket::AF_INET; the C# layer only takes Integers, so the coercion is
# done here, where Socket::Option's tables already live.
class Socket
  class << self
    def __ir_socktype_arg(value) # :nodoc:
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      found = __ir_lookup("SOCK_#{name.upcase.sub(/\ASOCK_/, "")}")
      raise SocketError, "unknown socket type: #{name}" if found.nil?
      found
    end

    def __ir_protocol_arg(value) # :nodoc:
      return 0 if value.nil?
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      found = __ir_lookup("IPPROTO_#{name.upcase.sub(/\AIPPROTO_/, "")}")
      raise SocketError, "unknown protocol: #{name}" if found.nil?
      found
    end

    def __ir_shutdown_arg(value) # :nodoc:
      return value if value.kind_of?(Integer)
      name = __ir_name(value, "Integer")
      case name.upcase.sub(/\ASHUT_/, "")
      when "RD" then SHUT_RD
      when "WR" then SHUT_WR
      when "RDWR" then SHUT_RDWR
      else raise SocketError, "unknown shutdown argument: #{name}"
      end
    end

    alias_method :__ir_raw_new, :new

    def new(domain, socktype, protocol = nil)
      __ir_raw_new(__ir_family_arg(domain), __ir_socktype_arg(socktype), __ir_protocol_arg(protocol))
    end

    def open(*args, &block)
      sock = new(*args)
      return sock unless block
      begin
        block.call(sock)
      ensure
        sock.close unless sock.closed?
      end
    end
  end
end

class BasicSocket
  alias_method :__ir_raw_shutdown, :shutdown
  alias_method :__ir_raw_getsockopt, :getsockopt
  alias_method :__ir_raw_setsockopt, :setsockopt

  def shutdown(how = Socket::SHUT_RDWR)
    __ir_raw_shutdown(Socket.__ir_shutdown_arg(how))
  end

  # CRuby returns a Socket::Option, not the raw bytes.
  def getsockopt(level, optname)
    lvl = Socket.__ir_level_arg(__ir_afamily, level)
    opt = Socket.__ir_optname_arg(lvl, optname)
    Socket::Option.new(__ir_afamily, lvl, opt, __ir_raw_getsockopt(lvl, opt))
  end

  def setsockopt(level, optname = nil, value = nil)
    if level.kind_of?(Socket::Option)
      option = level
      lvl, opt, value = option.level, option.optname, option.data
    else
      lvl = Socket.__ir_level_arg(__ir_afamily, level)
      opt = Socket.__ir_optname_arg(lvl, optname)
    end
    value = [value ? 1 : 0].pack("i") if value == true || value == false
    __ir_raw_setsockopt(lvl, opt, value)
    0
  end

  # The address family of the underlying socket.  There is no Ruby-visible
  # accessor for it, so read it back out of the sockaddr getsockname returns --
  # whose family byte is the *platform* number, not Socket::AF_* (those carry
  # winsock values on IronRuby).  An unbound socket has no local endpoint at
  # all, and .NET reports that as a NullReferenceException.
  def __ir_afamily # :nodoc:
    name = __ir_raw_getsockname_bytes
    return Socket::AF_INET if name.nil? || name.bytesize < 2
    case name.unpack("S")[0]
    when 1 then Socket::AF_UNIX
    when 2 then Socket::AF_INET
    when 10, 23, 28, 30 then Socket::AF_INET6
    else Socket::AF_UNSPEC
    end
  end

  def __ir_raw_getsockname_bytes # :nodoc:
    getsockname
  rescue Exception
    nil
  end
  private :__ir_raw_getsockname_bytes
end
