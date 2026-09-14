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
# Ruby object over a (family, port, address) triple plus the pfamily/socktype/
# protocol tuple, with the packed-sockaddr parsing, the getaddrinfo(3) argument
# validation and the IPv6 classification predicates written out in Ruby.
class Addrinfo
  # getaddrinfo(3) rejects socket-type/protocol combinations that cannot name a
  # real socket, and CRuby surfaces that as SocketError.  The set is measured
  # against glibc: for a given socktype only these protocols are accepted.
  # SOCK_RAW takes anything, SOCK_RDM and SOCK_PACKET take nothing.
  IR_PROTOCOLS_BY_SOCKTYPE__ = {
    0 => [0, 17],   # unspecified: IPPROTO_IP / IPPROTO_HOPOPTS, IPPROTO_UDP
    1 => [0, 6],    # SOCK_STREAM: IPPROTO_IP, IPPROTO_TCP
    2 => [0, 17],   # SOCK_DGRAM:  IPPROTO_IP, IPPROTO_UDP
    3 => :any,      # SOCK_RAW
    4 => [],        # SOCK_RDM
    5 => [0],       # SOCK_SEQPACKET: IPPROTO_IP only
    10 => [],       # SOCK_PACKET
  }

  # sockaddr is either a packed sockaddr String as getsockname(2) returns it, or
  # the ["AF_INET", port, hostname, ip] Array IPSocket#addr produces.
  def initialize(sockaddr, family = nil, socktype = nil, protocol = nil)
    @canonname = nil
    @unix_path = nil
    @ip_address = nil
    @ip_port = nil
    @ir_host = nil

    if sockaddr.kind_of?(Array)
      __ir_init_from_array(sockaddr, family)
    else
      unless sockaddr.kind_of?(String)
        unless sockaddr.respond_to?(:to_str)
          raise TypeError, "no implicit conversion of #{sockaddr.class} into String"
        end
        sockaddr = sockaddr.to_str
      end
      __ir_init_from_packed(sockaddr, family)
    end

    @socktype = socktype.nil? ? 0 : Socket.__ir_socktype_arg(socktype)
    @protocol = protocol.nil? ? 0 : Socket.__ir_protocol_arg(protocol)
    __ir_validate_protocol
  end

  attr_reader :afamily, :pfamily, :socktype, :protocol, :canonname

  def __ir_init_from_array(sockaddr, family) # :nodoc:
    if sockaddr.size < 3
      raise SocketError, "unknown address family: #{sockaddr[0]}"
    end
    @afamily = Addrinfo.__af(sockaddr[0], (sockaddr[3] || sockaddr[2]).to_s)
    @ir_host = sockaddr[2].nil? ? nil : sockaddr[2].to_s
    @ip_address = (sockaddr[3] || sockaddr[2]).to_s
    @ip_port = sockaddr[1].to_i

    if family.nil?
      @pfamily = @afamily
    else
      @pfamily = Addrinfo.__af(family)
      unless @pfamily == Socket::PF_INET || @pfamily == Socket::PF_INET6
        raise SocketError, "unsupported protocol family: #{family}"
      end
      unless @pfamily == @afamily
        raise SocketError, "address family for hostname not supported"
      end
    end

    __ir_validate_address
  end
  private :__ir_init_from_array

  # The family byte of a packed sockaddr is the *platform* number -- 10 for
  # AF_INET6 on Linux -- not Socket::AF_INET6, which carries winsock's 23 (see
  # Socket.cs).  Accept both, plus the BSD/macOS numbers, so a sockaddr built
  # anywhere round-trips.
  def __ir_init_from_packed(bytes, family) # :nodoc:
    bytes = bytes.dup.force_encoding(Encoding::BINARY)
    raise SocketError, "too short sockaddr" if bytes.bytesize < 2
    case bytes.unpack("S")[0]
    when 1
      @afamily = Socket::AF_UNIX
      path = bytes.byteslice(2, bytes.bytesize - 2)
      nul = path.index("\0")
      @unix_path = nul ? path.byteslice(0, nul) : path
      @unix_path.force_encoding(Encoding::UTF_8)
    when 2
      raise SocketError, "too short sockaddr" if bytes.bytesize < 8
      @afamily = Socket::AF_INET
      @ip_port = bytes.byteslice(2, 2).unpack("n")[0]
      @ip_address = bytes.byteslice(4, 4).unpack("C4").join(".")
    when 10, 23, 28, 30
      raise SocketError, "too short sockaddr" if bytes.bytesize < 24
      @afamily = Socket::AF_INET6
      @ip_port = bytes.byteslice(2, 2).unpack("n")[0]
      @ip_address = Socket.__ir_unpack_ipv6(bytes.byteslice(8, 16))
    else
      raise SocketError, "unknown address family: #{bytes.unpack("S")[0]}"
    end
    # Without an explicit family a packed sockaddr leaves the protocol family
    # unspecified -- the family is already carried by the bytes themselves.
    @pfamily = family.nil? ? Socket::PF_UNSPEC : Addrinfo.__af(family)
  end
  private :__ir_init_from_packed

  def __ir_validate_address # :nodoc:
    return if @ip_address.nil? || @ip_address.empty?
    case @afamily
    when Socket::AF_INET
      ok = @ip_address =~ /\A\d{1,3}(\.\d{1,3}){3}\z/ &&
           @ip_address.split(".").all? { |o| o.to_i <= 255 }
      raise SocketError, "invalid address: #{@ip_address}" unless ok
    when Socket::AF_INET6
      raise SocketError, "invalid address: #{@ip_address}" unless @ip_address.include?(":")
      begin
        Socket.__ir_pack_ipv6(@ip_address)
      rescue StandardError
        raise SocketError, "invalid address: #{@ip_address}"
      end
    end
  end
  private :__ir_validate_address

  def __ir_validate_protocol # :nodoc:
    return unless @afamily == Socket::AF_INET || @afamily == Socket::AF_INET6
    allowed = IR_PROTOCOLS_BY_SOCKTYPE__[@socktype]
    if allowed.nil?
      raise SocketError, "unsupported socket type: #{@socktype}"
    end
    return if allowed == :any || allowed.include?(@protocol)
    raise SocketError, "ai_socktype not supported"
  end
  private :__ir_validate_protocol

  # Normalises "AF_INET" / :INET / Socket::AF_INET to a family number.
  def self.__af(af, address = nil) # :nodoc:
    case af
    when nil
      (address && address.include?(":")) ? Socket::AF_INET6 : Socket::AF_INET
    when Integer then af
    when Symbol, String
      name = af.to_s.sub(/\A(AF_|PF_)/, "").upcase
      const = "AF_#{name}"
      if Socket.const_defined?(const)
        Socket.const_get(const)
      else
        raise SocketError, "unknown socket address family: #{af}"
      end
    else
      raise SocketError, "unknown socket address family: #{af.inspect}"
    end
  end

  # An Addrinfo for a socket's own address: unlike a bare packed sockaddr, whose
  # protocol family is unspecified, CRuby reports pfamily == afamily here.
  def self.__ir_from_sockaddr(bytes, socktype = 0) # :nodoc:
    info = new(bytes, nil, socktype, 0)
    info.__ir_set_pfamily(info.afamily)
    info
  end

  def __ir_set_pfamily(family) # :nodoc:
    @pfamily = family
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

  # Internal constructor that skips the getaddrinfo(3) validation: the callers
  # below already know the combination is sound.
  def self.__ir_new_ip(ip, port, socktype, protocol, pfamily = nil, host = nil, canonname = nil) # :nodoc:
    info = allocate
    info.__ir_init_ip(ip, port, socktype, protocol, pfamily, host, canonname)
    info
  end

  def __ir_init_ip(ip, port, socktype, protocol, pfamily, host, canonname) # :nodoc:
    @afamily = ip.to_s.include?(":") ? Socket::AF_INET6 : Socket::AF_INET
    @pfamily = pfamily || @afamily
    @ip_address = ip.to_s
    @ip_port = port.to_i
    @socktype = socktype
    @protocol = protocol
    @canonname = canonname
    @unix_path = nil
    @ir_host = host
    # ::ffff:1.2.3.4 and 2001:0db8::0001 both have a canonical spelling; store
    # that, and remember the input for #inspect the way CRuby does.
    if @afamily == Socket::AF_INET6
      begin
        canonical = Socket.__ir_unpack_ipv6(Socket.__ir_pack_ipv6(@ip_address))
        @ir_host ||= @ip_address if canonical != @ip_address
        @ip_address = canonical
      rescue StandardError
      end
    end
    @ir_host = nil if @ir_host == @ip_address
  end

  def self.tcp(host, port)
    __ir_new_ip(__resolve(host), port, Socket::SOCK_STREAM, Socket::IPPROTO_TCP, nil, host && host.to_s)
  end

  def self.udp(host, port)
    __ir_new_ip(__resolve(host), port, Socket::SOCK_DGRAM, Socket::IPPROTO_UDP, nil, host && host.to_s)
  end

  def self.ip(host)
    __ir_new_ip(__resolve(host), 0, 0, 0, nil, host && host.to_s)
  end

  def self.unix(path, socktype = Socket::SOCK_STREAM)
    info = allocate
    info.__ir_init_unix(path.to_s, Socket.__ir_socktype_arg(socktype))
    info
  end

  def __ir_init_unix(path, socktype) # :nodoc:
    @afamily = Socket::AF_UNIX
    @pfamily = Socket::PF_UNIX
    @socktype = socktype
    @protocol = 0
    @canonname = nil
    @ip_address = nil
    @ip_port = nil
    @ir_host = nil
    @unix_path = path
  end

  # getaddrinfo(3).  IronRuby resolves through .NET's Dns, which has no notion
  # of a service name or of ai_flags, so the service, socktype and protocol
  # defaulting that glibc would do is written out here.
  def self.getaddrinfo(nodename, service = nil, family = nil, socktype = nil,
                       protocol = nil, flags = nil, timeout: nil)
    family = family.nil? ? nil : __af(family)
    socktype = socktype.nil? ? nil : Socket.__ir_socktype_arg(socktype)
    protocol = protocol.nil? ? nil : Socket.__ir_protocol_arg(protocol)

    port = case service
           when nil then 0
           when Integer then service
           else
             begin
               Socket.getservbyname(service.to_s)
             rescue StandardError
               service.to_s.to_i
             end
           end

    if socktype.nil?
      socktype = protocol == Socket::IPPROTO_UDP ? Socket::SOCK_DGRAM : Socket::SOCK_STREAM
    end
    if protocol.nil?
      protocol = socktype == Socket::SOCK_DGRAM ? Socket::IPPROTO_UDP : Socket::IPPROTO_TCP
      protocol = 0 unless socktype == Socket::SOCK_DGRAM || socktype == Socket::SOCK_STREAM
    end

    canonname = nil
    if flags && (flags.to_i & Socket::AI_CANONNAME) != 0
      # BasicSocket.do_not_reverse_lookup defaults to true, so Socket.getaddrinfo
      # answers with the numeric address; gethostbyname resolves forward, which
      # is what AI_CANONNAME asks for.
      canonname = begin
        Socket.gethostbyname(nodename.to_s)[0]
      rescue StandardError
        nodename.to_s
      end
    end

    addresses = begin
      Socket.getaddrinfo(nodename.to_s, nil).map { |entry| entry[3] }
    rescue StandardError
      [__resolve(nodename)]
    end
    addresses = [__resolve(nodename)] if addresses.empty?
    addresses.uniq!

    unless family.nil?
      want6 = (family == Socket::AF_INET6)
      filtered = addresses.select { |a| a.include?(":") == want6 }
      addresses = filtered unless filtered.empty?
    end

    addresses.map do |address|
      __ir_new_ip(address, port, socktype, protocol, family, nodename && nodename.to_s, canonname)
    end
  end

  def self.foreach(nodename, service = nil, family = nil, socktype = nil,
                   protocol = nil, flags = nil, timeout: nil, &block)
    getaddrinfo(nodename, service, family, socktype, protocol, flags).each(&block)
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
    @afamily == Socket::AF_UNIX
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

  def unix_path
    raise SocketError, "need AF_UNIX address" unless unix?
    @unix_path
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

  # --- IPv6 classification.  All of these are bit tests on the 8 16-bit groups.

  def __ir_v6_words # :nodoc:
    return nil unless ipv6?
    @ir_v6_words ||= Socket.__ir_pack_ipv6(@ip_address).unpack("n8")
  rescue StandardError
    nil
  end
  private :__ir_v6_words

  def ipv6_unspecified?
    w = __ir_v6_words
    !w.nil? && w.all? { |x| x == 0 }
  end

  def ipv6_loopback?
    w = __ir_v6_words
    !w.nil? && w[0, 7].all? { |x| x == 0 } && w[7] == 1
  end

  def ipv6_multicast?
    w = __ir_v6_words
    !w.nil? && (w[0] >> 8) == 0xff
  end

  def ipv6_linklocal?
    w = __ir_v6_words
    !w.nil? && (w[0] & 0xffc0) == 0xfe80
  end

  def ipv6_sitelocal?
    w = __ir_v6_words
    !w.nil? && (w[0] & 0xffc0) == 0xfec0
  end

  def ipv6_unique_local?
    w = __ir_v6_words
    !w.nil? && (w[0] & 0xfe00) == 0xfc00
  end

  def __ir_mc_scope?(scope) # :nodoc:
    w = __ir_v6_words
    !w.nil? && (w[0] >> 8) == 0xff && (w[0] & 0x000f) == scope
  end
  private :__ir_mc_scope?

  def ipv6_mc_nodelocal?
    __ir_mc_scope?(1)
  end

  def ipv6_mc_linklocal?
    __ir_mc_scope?(2)
  end

  def ipv6_mc_sitelocal?
    __ir_mc_scope?(5)
  end

  def ipv6_mc_orglocal?
    __ir_mc_scope?(8)
  end

  def ipv6_mc_global?
    __ir_mc_scope?(0xe)
  end

  def ipv6_v4mapped?
    w = __ir_v6_words
    !w.nil? && w[0, 5].all? { |x| x == 0 } && w[5] == 0xffff
  end

  def ipv6_v4compat?
    w = __ir_v6_words
    return false if w.nil?
    return false unless w[0, 6].all? { |x| x == 0 }
    tail = (w[6] << 16) | w[7]
    tail != 0 && tail != 1
  end

  def ipv6_to_ipv4
    return nil unless ipv6_v4mapped? || ipv6_v4compat?
    w = __ir_v6_words
    ip = [w[6] >> 8, w[6] & 0xff, w[7] >> 8, w[7] & 0xff].join(".")
    Addrinfo.__ir_new_ip(ip, @ip_port, @socktype, @protocol)
  end

  def afamily_name # :nodoc:
    "AF_#{Socket.__ir_family_name(@afamily)}"
  end

  def pfamily_name # :nodoc:
    "PF_#{Socket.__ir_family_name(@pfamily)}"
  end

  # CRuby prints a bare IPv4 address as a dotted quad, an IPv6 one in brackets
  # when it carries a port, and a relative UNIX path prefixed with "UNIX ".
  def inspect_sockaddr
    if unix?
      @unix_path.to_s.start_with?("/") ? @unix_path.dup : "UNIX #{@unix_path}"
    elsif ipv6?
      @ip_port.to_i == 0 ? @ip_address.dup : "[#{@ip_address}]:#{@ip_port}"
    elsif ipv4?
      @ip_port.to_i == 0 ? @ip_address.dup : "#{@ip_address}:#{@ip_port}"
    else
      "unknown address family #{@afamily}"
    end
  end

  def __ir_socktype_label # :nodoc:
    if ip?
      return nil if @socktype == 0 && @protocol == 0
      # A protocol of 0 still prints as TCP/UDP once the protocol family is
      # known -- that is how CRuby prints BasicSocket#local_address.
      named = @protocol != 0 || @pfamily != Socket::PF_UNSPEC
      return "TCP" if @socktype == Socket::SOCK_STREAM && named &&
                      (@protocol == Socket::IPPROTO_TCP || @protocol == 0)
      return "UDP" if @socktype == Socket::SOCK_DGRAM && named &&
                      (@protocol == Socket::IPPROTO_UDP || @protocol == 0)
    else
      return nil if @socktype == 0
    end
    "SOCK_#{Socket.__ir_const_name("SOCK_", @socktype)}"
  end
  private :__ir_socktype_label

  def inspect
    parts = [inspect_sockaddr]
    label = __ir_socktype_label
    parts << label if label
    parts << @canonname if @canonname
    parts << "(#{@ir_host})" if @ir_host
    "#<Addrinfo: #{parts.join(" ")}>"
  end

  def to_sockaddr
    if unix?
      Socket.sockaddr_un(@unix_path)
    else
      Socket.sockaddr_in(@ip_port.to_i, @ip_address.to_s)
    end
  end
  alias_method :to_s, :to_sockaddr

  # Socket.getnameinfo ignores its flags argument (the .NET resolver has no
  # equivalent), so the two flags that only suppress a lookup are applied here.
  def getnameinfo(flags = 0)
    return [Socket.gethostname, @unix_path.dup] if unix?
    flags = flags.to_i
    host, service = Socket.getnameinfo(to_sockaddr, flags)
    service = @ip_port.to_s if (flags & Socket::NI_NUMERICSERV) != 0
    host = @ip_address.dup if (flags & Socket::NI_NUMERICHOST) != 0
    [host, service]
  end

  def marshal_dump
    address = unix? ? @unix_path.dup : [@ip_address, @ip_port.to_s]
    protocol = if @protocol == 0 && !ip?
      0
    else
      "IPPROTO_#{Socket.__ir_const_name("IPPROTO_", @protocol)}"
    end
    socktype = @socktype == 0 ? 0 : "SOCK_#{Socket.__ir_const_name("SOCK_", @socktype)}"
    [afamily_name, address, pfamily_name, socktype, protocol, @canonname]
  end

  def marshal_load(array)
    afamily, address, pfamily, socktype, protocol, canonname = array
    @afamily = Addrinfo.__af(afamily)
    @pfamily = Addrinfo.__af(pfamily)
    @socktype = socktype == 0 ? 0 : Socket.__ir_socktype_arg(socktype)
    @protocol = protocol == 0 ? 0 : Socket.__ir_protocol_arg(protocol)
    @canonname = canonname
    @ir_host = nil
    @unix_path = nil
    @ip_address = nil
    @ip_port = nil
    if @afamily == Socket::AF_UNIX
      @unix_path = address
    else
      @ip_address = address[0]
      @ip_port = address[1].to_i
    end
    self
  end

  def to_a # :nodoc:
    [afamily_name, @ip_port, @ip_address, @ip_address]
  end

  def ==(other)
    other.kind_of?(Addrinfo) &&
      other.afamily == @afamily && other.pfamily == @pfamily &&
      other.socktype == @socktype && other.protocol == @protocol &&
      other.to_sockaddr == to_sockaddr
  end
  alias_method :eql?, :==

  def hash
    [@afamily, @pfamily, @socktype, @protocol, to_sockaddr].hash
  end

  # CRuby's Addrinfo#family_addrinfo: either an Addrinfo of a matching family,
  # or the arguments its own family needs (path for AF_UNIX, host+port for IP).
  def family_addrinfo(*args)
    raise ArgumentError, "no address specified" if args.empty?
    if args[0].kind_of?(Addrinfo)
      raise ArgumentError, "too many arguments" if args.size > 1
      other = args[0]
      unless other.pfamily == @pfamily && other.afamily == @afamily
        raise ArgumentError, "protocol family mismatch: #{other.inspect} for #{inspect}"
      end
      unless other.socktype == @socktype
        raise ArgumentError, "socket type mismatch: #{other.inspect} for #{inspect}"
      end
      return other
    end
    if unix?
      raise ArgumentError, "too many arguments" if args.size > 1
      return Addrinfo.unix(args[0], @socktype)
    end
    unless args.size == 2
      raise ArgumentError, "host and port are needed, got #{args.size} arguments"
    end
    Addrinfo.__ir_new_ip(Addrinfo.__resolve(args[0]), args[1], @socktype, @protocol, @pfamily)
  end

  # CRuby returns a Socket from #listen / #bind / #connect, not a TCPServer.
  def __ir_new_socket(socktype = nil) # :nodoc:
    Socket.new(@afamily, socktype || (@socktype == 0 ? Socket::SOCK_STREAM : @socktype), 0)
  end
  private :__ir_new_socket

  def listen(backlog = Socket::SOMAXCONN)
    sock = __ir_new_socket(Socket::SOCK_STREAM)
    begin
      sock.setsockopt(Socket::SOL_SOCKET, Socket::SO_REUSEADDR, true)
      sock.bind(to_sockaddr)
      begin
        sock.listen(backlog)
      rescue StandardError
        # Socket::SOMAXCONN is Int32::MaxValue on IronRuby (it comes from .NET's
        # SocketOptionName table, not from the platform), which .NET rejects.
        raise if backlog != Socket::SOMAXCONN
        sock.listen(128)
      end
    rescue Exception
      sock.close
      raise
    end
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end

  def bind # :yield: socket
    sock = __ir_new_socket
    begin
      sock.bind(to_sockaddr)
    rescue Exception
      sock.close
      raise
    end
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end

  def connect(timeout: nil) # :yield: socket
    sock = __ir_new_socket
    begin
      sock.connect(to_sockaddr)
    rescue Exception
      sock.close
      raise
    end
    return sock unless block_given?
    begin
      yield sock
    ensure
      sock.close unless sock.closed?
    end
  end

  def connect_from(*args, **opts, &block)
    local = family_addrinfo(*args)
    sock = __ir_new_socket
    begin
      sock.bind(local.to_sockaddr)
      sock.connect(to_sockaddr)
    rescue Exception
      sock.close
      raise
    end
    return sock unless block
    begin
      block.call(sock)
    ensure
      sock.close unless sock.closed?
    end
  end

  # Addrinfo#connect_to is the mirror image: self is the local address.
  def connect_to(*args, **opts, &block)
    remote = family_addrinfo(*args)
    remote.connect_from(self, &block)
  end
end

class BasicSocket
  # CRuby builds these from getsockname(2)/getpeername(2).  Going through the
  # packed sockaddr rather than through IPSocket#addr keeps them working for
  # Socket, which has no #addr at all, and keeps the IPv6 and AF_UNIX cases out
  # of the IPv4-shaped GetAddressArray in BasicSocket.cs.
  def local_address
    name = __ir_sockname_bytes || Socket.sockaddr_in(0, "0.0.0.0")
    Addrinfo.__ir_from_sockaddr(name, __ir_socktype)
  end

  def remote_address
    Addrinfo.__ir_from_sockaddr(getpeername, __ir_socktype)
  end

  # The address a client should connect to in order to reach this socket: the
  # local address, with a wildcard replaced by loopback.  An unbound socket has
  # no local address at all, which CRuby reports as SocketError.
  def connect_address
    name = __ir_sockname_bytes
    raise SocketError, "unbound socket" if name.nil?
    addr = Addrinfo.__ir_from_sockaddr(name, __ir_socktype)
    if addr.ipv4? && addr.ip_address == "0.0.0.0"
      Addrinfo.__ir_new_ip("127.0.0.1", addr.ip_port, addr.socktype, addr.protocol, addr.pfamily)
    elsif addr.ipv6? && addr.ipv6_unspecified?
      Addrinfo.__ir_new_ip("::1", addr.ip_port, addr.socktype, addr.protocol, addr.pfamily)
    else
      addr
    end
  end

  # .NET has no local endpoint for an unbound socket and reports that as a
  # NullReferenceException out of Socket.LocalEndPoint.
  def __ir_sockname_bytes # :nodoc:
    name = getsockname
    name.nil? || name.bytesize < 2 ? nil : name
  rescue Exception
    nil
  end
  private :__ir_sockname_bytes

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

  # Builds an Addrinfo from a packed sockaddr as getsockname/getpeername return
  # it. The family byte there is the *platform* number (AF_INET6 is 10 on Linux),
  # not Socket::AF_INET6, which carries winsock's 23 -- see Socket.cs.
  def self.__ir_addrinfo_from_packed(bytes, socktype = SOCK_STREAM) # :nodoc:
    return nil if bytes.nil? || bytes.bytesize < 8
    bytes = bytes.dup.force_encoding(Encoding::BINARY)
    port = bytes.byteslice(2, 2).unpack("n")[0]
    case bytes.unpack("S")[0]
    when 2
      ip = bytes.byteslice(4, 4).unpack("C4").join(".")
    when 10, 23, 28, 30
      return nil if bytes.bytesize < 24
      ip = __ir_unpack_ipv6(bytes.byteslice(8, 16))
    else
      return nil
    end
    protocol = socktype == SOCK_DGRAM ? IPPROTO_UDP : IPPROTO_TCP
    Addrinfo.new([nil, port, ip, ip], nil, socktype, protocol)
  end

  def self.accept_loop(*sockets) # :yield: socket, client_addrinfo
    sockets.flatten!
    raise ArgumentError, "no sockets" if sockets.empty?
    # CRuby waits in IO.select and then accepts without blocking. IronRuby's
    # IO.select reports every socket as readable straight away (its read wait
    # handle is a zero-byte overlapped receive, which completes at once), so that
    # would busy-spin; block in accept instead, which is interruptible. With the
    # single socket tcp_server_sockets creates this is equivalent.
    loop do
      sockets.each do |server|
        accepted = server.accept
        if accepted.kind_of?(Array)
          sock = accepted[0]
          yield sock, __ir_addrinfo_from_packed(sock.getpeername)
        else
          yield accepted, accepted.remote_address
        end
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
    # A Linux-only socket type with no SocketType member; it exists here only so
    # that Addrinfo can reject it the way getaddrinfo(3) does.
    "SOCK_PACKET"     => 10,
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
      # inet_ntop spells an IPv4-mapped or IPv4-compatible address with a
      # trailing dotted quad, but only when the embedded address needs more than
      # the low 16 bits -- ::1 and ::0.0.1.1 stay in hex.
      if words[0, 5].all? { |w| w == 0 } &&
         (words[5] == 0xffff || (words[5] == 0 && words[6] != 0))
        quad = [words[6] >> 8, words[6] & 0xff, words[7] >> 8, words[7] & 0xff].join(".")
        return words[5] == 0xffff ? "::ffff:#{quad}" : "::#{quad}"
      end
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

# The *_nonblock family: .NET reports "would block" as a SocketException, which
# surfaces as SocketError because Socket::SocketError *is* that CLR type.  CRuby
# raises an Errno tagged with IO::WaitReadable/IO::WaitWritable instead, and
# supports exception: false.  A full SocketException -> Errno mapping across the
# library is still missing; this covers only the non-blocking entry points,
# where the wrong class stops the spec suite from making progress at all.
class Socket
  def self.__ir_socket_error_code(error) # :nodoc:
    error.socket_error_code.to_s
  rescue StandardError, Exception
    ""
  end

  alias_method :__ir_raw_connect_nonblock, :connect_nonblock

  def connect_nonblock(sockaddr, exception: true)
    __ir_raw_connect_nonblock(sockaddr)
    0
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock", "InProgress"
      raise IO::EINPROGRESSWaitWritable, "Operation now in progress" if exception
      :wait_writable
    when "IsConnected"
      raise Errno::EISCONN
    else
      raise
    end
  end

  alias_method :__ir_raw_accept_nonblock, :accept_nonblock

  def accept_nonblock(exception: true)
    __ir_raw_accept_nonblock
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock"
      raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
      :wait_readable
    else
      raise
    end
  end
end

class TCPServer
  alias_method :__ir_raw_accept_nonblock, :accept_nonblock

  def accept_nonblock(exception: true)
    __ir_raw_accept_nonblock
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock"
      raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
      :wait_readable
    else
      raise
    end
  end
end

class TCPSocket
  # CRuby's third and fourth arguments are local_host and local_port; the C#
  # constructor reads the third as a local *port*, so TCPSocket.new(host, port,
  # nil) -- which the specs and plenty of real code do -- raised TypeError.
  class << self
    alias_method :__ir_raw_new, :new

    def new(remote_host, remote_port, local_host = nil, local_port = nil,
            connect_timeout: nil, open_timeout: nil, resolv_timeout: nil)
      # .NET's Socket.Connect takes no timeout and IronRuby has no non-blocking
      # connect, so the timeout cannot be enforced.  Connect anyway -- which is
      # what a large enough timeout would do -- and report a connection that did
      # not happen the way CRuby does, with IO::TimeoutError.
      timeout = connect_timeout || open_timeout
      begin
        if local_host.nil? && local_port.nil?
          __ir_raw_new(remote_host, remote_port)
        else
          __ir_raw_new(remote_host, remote_port, local_host, local_port || 0)
        end
      rescue Errno::ETIMEDOUT, Errno::EHOSTUNREACH, Errno::ENETUNREACH => e
        raise IO::TimeoutError, "Connection timed out" if timeout
        raise
      end
    end

    def open(*args, **opts, &block)
      sock = new(*args, **opts)
      return sock unless block
      begin
        block.call(sock)
      ensure
        sock.close unless sock.closed?
      end
    end
  end
end

class UDPSocket
  alias_method :__ir_raw_recvfrom_nonblock, :recvfrom_nonblock

  def recvfrom_nonblock(length, flags = nil, exception: true)
    flags.nil? ? __ir_raw_recvfrom_nonblock(length) : __ir_raw_recvfrom_nonblock(length, flags)
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock"
      raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
      :wait_readable
    else
      raise
    end
  end
end

class Socket
  # Socket.udp_server_* is plain Ruby in CRuby too (ext/socket/lib/socket.rb).
  # CRuby's version reads each datagram with recvmsg_nonblock so it can report
  # the *local* address the packet arrived on; .NET 8 exposes no cmsg surface,
  # so this uses recvfrom_nonblock and reports the socket's own bound address.
  class UDPSource
    attr_reader :remote_address, :local_address

    def initialize(remote_address, local_address, &reply_proc)
      @remote_address = remote_address
      @local_address = local_address
      @reply_proc = reply_proc
    end

    def inspect
      "#<Socket::UDPSource: #{@remote_address.inspect_sockaddr} to #{@local_address.inspect_sockaddr}>"
    end

    def reply(message)
      @reply_proc.call(message)
    end
  end

  def self.udp_server_sockets(host = nil, port = nil)
    host, port = nil, host if port.nil?
    sock = UDPSocket.new
    sock.bind(host || "0.0.0.0", port.to_i)
    sockets = [sock]
    return sockets unless block_given?
    begin
      yield sockets
    ensure
      sockets.each { |s| s.close unless s.closed? }
    end
  end

  def self.udp_server_recv(sockets) # :yield: message, udpsource
    sockets.each do |sock|
      begin
        message, sender = sock.recvfrom_nonblock(65536)
      rescue IO::WaitReadable
        next
      end
      remote = Addrinfo.new(sender, nil, Socket::SOCK_DGRAM, 0)
      local = sock.local_address
      yield message, UDPSource.new(remote, local) { |reply|
        sock.send(reply, 0, remote.ip_address, remote.ip_port)
      }
    end
  end

  def self.udp_server_loop_on(sockets, &block) # :yield: message, udpsource
    # CRuby waits in IO.select here. On IronRuby IO.select returns immediately for
    # a UDP socket -- its read wait handle comes from a zero-byte overlapped
    # receive, which UDP completes at once -- so that would busy-spin. Block in
    # recvfrom instead, which is interruptible. With the single socket
    # udp_server_sockets creates this is equivalent; several would be serialised.
    loop do
      sockets.each do |sock|
        message, sender = sock.recvfrom(65536)
        remote = Addrinfo.new(sender, nil, Socket::SOCK_DGRAM, 0)
        local = sock.local_address
        block.call(message, UDPSource.new(remote, local) { |reply|
          sock.send(reply, 0, remote.ip_address, remote.ip_port)
        })
      end
    end
  end

  def self.udp_server_loop(host = nil, port = nil, &block) # :yield: message, udpsource
    udp_server_sockets(host, port) { |sockets| udp_server_loop_on(sockets, &block) }
  end
end

# ---------------------------------------------------------------------------
# Argument shapes the C# layer does not accept, and the few singletons that are
# pure bookkeeping over what it does expose.

class BasicSocket
  alias_method :__ir_raw_recv, :recv
  alias_method :__ir_raw_recv_nonblock, :recv_nonblock

  # CRuby's optional third argument is an output buffer, and the result is that
  # very String -- with its encoding preserved.
  def __ir_into_buffer(data, buffer) # :nodoc:
    return data if buffer.nil?
    encoding = buffer.encoding
    buffer.replace(data)
    buffer.force_encoding(encoding)
    buffer
  end
  private :__ir_into_buffer

  def recv(length, flags = nil, buffer = nil)
    data = flags.nil? ? __ir_raw_recv(length) : __ir_raw_recv(length, flags)
    __ir_into_buffer(data, buffer)
  end

  def recv_nonblock(length, flags = nil, buffer = nil, exception: true)
    data = flags.nil? ? __ir_raw_recv_nonblock(length) : __ir_raw_recv_nonblock(length, flags)
    __ir_into_buffer(data, buffer)
  rescue SocketError => e
    raise unless Socket.__ir_socket_error_code(e) == "WouldBlock"
    raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
    :wait_readable
  end
end

class IPSocket
  alias_method :__ir_raw_addr, :addr
  alias_method :__ir_raw_peeraddr, :peeraddr

  # CRuby takes an optional reverse_lookup override; without it the tuple's
  # third element follows BasicSocket.do_not_reverse_lookup.
  def __ir_with_reverse_lookup(reverse_lookup) # :nodoc:
    return yield if reverse_lookup.nil?
    case reverse_lookup
    when true, :hostname then flag = false
    when false, :numeric then flag = true
    else raise ArgumentError, "invalid reverse_lookup flag: #{reverse_lookup.inspect}"
    end
    saved = do_not_reverse_lookup
    begin
      self.do_not_reverse_lookup = flag
      yield
    ensure
      self.do_not_reverse_lookup = saved
    end
  end
  private :__ir_with_reverse_lookup

  def addr(reverse_lookup = nil)
    __ir_with_reverse_lookup(reverse_lookup) { __ir_raw_addr }
  end

  def peeraddr(reverse_lookup = nil)
    __ir_with_reverse_lookup(reverse_lookup) { __ir_raw_peeraddr }
  end

  def inspect
    return "#<#{self.class}:(closed)>" if closed?
    family, port, _, address = __ir_raw_addr
    "#<#{self.class}:fd #{fileno}, #{family}, #{address}, #{port}>"
  rescue StandardError
    "#<#{self.class}:fd #{fileno}>"
  end
end

class TCPServer
  # TCPServer.new takes (port) or (host, port); the TCPSocket singleton #new
  # defined above is about the *client* shape and must not be inherited here.
  class << self
    # __ir_raw_new is TCPSocket's alias for the builtin constructor; aliasing it
    # again here would make TCPSocket.new recurse back into this override.
    def new(*args, &block)
      unless block.nil?
        warn "warning: TCPServer::new() does not take block; use TCPServer::open() instead"
      end
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)" if args.size > 2
      host, port = args.size == 1 ? [nil, args[0]] : args
      host = nil if !host.nil? && host.respond_to?(:to_str) && host.to_str.empty?
      __ir_raw_new(host, Socket.__ir_port_arg(port))
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

class UDPSocket
  class << self
    alias_method :__ir_raw_new, :new

    def new(family = Socket::AF_INET)
      __ir_raw_new(Socket.__ir_family_arg(family))
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

class Socket
  # bind/connect accept an Addrinfo as well as a packed sockaddr.
  alias_method :__ir_raw_bind, :bind
  alias_method :__ir_raw_connect, :connect

  def bind(sockaddr)
    __ir_raw_bind(Addrinfo === sockaddr ? sockaddr.to_sockaddr : sockaddr)
  end

  def connect(sockaddr)
    __ir_raw_connect(Addrinfo === sockaddr ? sockaddr.to_sockaddr : sockaddr)
  end

  def connect_nonblock(sockaddr, exception: true)
    __ir_raw_connect_nonblock(Addrinfo === sockaddr ? sockaddr.to_sockaddr : sockaddr)
    0
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock", "InProgress"
      raise IO::EINPROGRESSWaitWritable, "Operation now in progress" if exception
      :wait_writable
    when "IsConnected"
      raise Errno::EISCONN
    else
      raise
    end
  end

  # Socket#recvfrom_nonblock has no C# counterpart (only UDPSocket's has); poll
  # for readiness first and then do the ordinary recvfrom, which cannot block
  # once poll(2) has said the socket is readable.
  def recvfrom_nonblock(length, flags = nil, buffer = nil, exception: true)
    unless IO.select([self], nil, nil, 0)
      raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
      return :wait_readable
    end
    data, sender = flags.nil? ? recvfrom(length) : recvfrom(length, flags)
    unless buffer.nil?
      encoding = buffer.encoding
      buffer.replace(data)
      buffer.force_encoding(encoding)
      data = buffer
    end
    [data, Addrinfo.new(sender, nil, Socket::SOCK_DGRAM, 0)]
  end

  class << self
    alias_method :__ir_raw_getaddrinfo, :getaddrinfo
    alias_method :__ir_raw_gethostbyname, :gethostbyname

    # The C# Socket.getaddrinfo resolves names but ignores the family, socktype,
    # protocol and flags arguments and always reports socktype 0 / protocol 0.
    # Do the getaddrinfo(3) part of the job here: family filtering, the
    # AI_PASSIVE wildcard-vs-loopback choice for a nil host, one entry per
    # (socktype, protocol) pair, and the reverse_lookup override CRuby takes as
    # a 7th argument.
    def getaddrinfo(host, service = nil, family = nil, socktype = nil,
                    protocol = nil, flags = nil, reverse_lookup = nil)
      fam = family.nil? ? AF_UNSPEC : __ir_family_arg(family)
      unless fam == AF_UNSPEC || fam == AF_INET || fam == AF_INET6
        raise __ir_resolution_error("getaddrinfo: Address family for hostname not supported",
                                    EAI_FAMILY)
      end
      socktype = socktype.nil? ? 0 : __ir_socktype_arg(socktype)
      protocol = __ir_protocol_arg(protocol)
      port = __ir_service_arg(service) || 0
      passive = !flags.nil? && (flags.to_i & AI_PASSIVE) != 0

      addresses =
        if host.nil? || (host.respond_to?(:to_str) && host.to_str.empty?)
          fam == AF_INET6 ? [passive ? "::" : "::1"] : [passive ? "0.0.0.0" : "127.0.0.1"]
        else
          found = __ir_raw_getaddrinfo(host, nil, 0, 0, 0, 0).map { |entry| entry[3] }
          wanted = found.select { |a| a.include?(":") == (fam == AF_INET6) }
          fam == AF_UNSPEC || wanted.empty? ? found : wanted
        end

      reverse = if reverse_lookup.nil?
        !BasicSocket.do_not_reverse_lookup
      else
        reverse_lookup == true || reverse_lookup == :hostname
      end

      pairs = __ir_socktype_pairs(socktype, protocol)
      addresses.uniq.flat_map do |address|
        af = address.include?(":") ? AF_INET6 : AF_INET
        name = address
        if reverse
          name = begin
            getnameinfo([af == AF_INET6 ? "AF_INET6" : "AF_INET", port, address])[0]
          rescue StandardError
            address
          end
        end
        pairs.map do |st, pr|
          [af == AF_INET6 ? "AF_INET6" : "AF_INET", port, name, address, af, st, pr]
        end
      end
    end

    # getaddrinfo(3) with neither a socket type nor a protocol reports one entry
    # per usable pair; with one of the two given it fills in the other.
    def __ir_socktype_pairs(socktype, protocol) # :nodoc:
      return [[socktype, protocol]] unless protocol == 0
      case socktype
      when 0
        [[SOCK_STREAM, IPPROTO_TCP], [SOCK_DGRAM, IPPROTO_UDP]]
      when SOCK_STREAM then [[SOCK_STREAM, IPPROTO_TCP]]
      when SOCK_DGRAM then [[SOCK_DGRAM, IPPROTO_UDP]]
      else [[socktype, 0]]
      end
    end

    def __ir_resolution_error(message, code) # :nodoc:
      error = ResolutionError.new(message)
      error.instance_variable_set(:@__ir_error_code, code)
      error
    end

    def __ir_service_arg(service) # :nodoc:
      return nil if service.nil?
      return service if service.kind_of?(Integer)
      name = service.to_s
      return name.to_i if name =~ /\A\d+\z/
      return 0 if name.empty?
      begin
        getservbyname(name)
      rescue StandardError
        raise SocketError, "getaddrinfo: Servname not supported for ai_socktype"
      end
    end

    # TCPServer.new / TCPSocket.new coerce the port through #to_str and then
    # read it as a number or a service name.
    def __ir_port_arg(port) # :nodoc:
      return 0 if port.nil?
      return port if port.kind_of?(Integer)
      unless port.respond_to?(:to_str)
        raise TypeError, "no implicit conversion of #{port.class} into String"
      end
      __ir_service_arg(port.to_str)
    end
  end

  class << self
    # gethostbyname(3) understands the two magic names inet_addr(3) does.
    def gethostbyname(host)
      case host.to_s
      when "<broadcast>" then ["255.255.255.255", [], AF_INET, [255, 255, 255, 255].pack("C4")]
      when "<any>" then ["0.0.0.0", [], AF_INET, [0, 0, 0, 0].pack("C4")]
      else __ir_raw_gethostbyname(host)
      end
    end
  end

  class ResolutionError
    def error_code
      @__ir_error_code
    end
  end
end

# Socket.ip_address_list / Socket.getifaddrs over System.Net.NetworkInformation,
# which is the only interface enumeration .NET exposes.  It has no notion of the
# ifaddr flags or of a point-to-point destination address, so #flags reports the
# subset that can be derived from the interface status and #dstaddr is nil.
class Socket
  load_assembly 'System.Net.NetworkInformation'

  class Ifaddr
    def initialize(name, ifindex, flags, addr, netmask, broadaddr, dstaddr)
      @name, @ifindex, @flags = name, ifindex, flags
      @addr, @netmask, @broadaddr, @dstaddr = addr, netmask, broadaddr, dstaddr
    end

    attr_reader :name, :ifindex, :flags, :addr, :netmask, :broadaddr, :dstaddr

    def inspect
      "#<Socket::Ifaddr #{@name} #{@addr ? @addr.inspect_sockaddr : ""}>"
    end
  end

  IFF_UP__ = 0x1 unless const_defined?(:IFF_UP__, false)
  IFF_LOOPBACK__ = 0x8 unless const_defined?(:IFF_LOOPBACK__, false)

  def self.__ir_interfaces # :nodoc:
    System::Net::NetworkInformation::NetworkInterface.get_all_network_interfaces
  rescue Exception
    []
  end

  def self.__ir_netmask_for(address, prefix) # :nodoc:
    return nil if prefix.nil? || prefix < 0
    if address.include?(":")
      words = (0...8).map do |i|
        bits = [[prefix - i * 16, 0].max, 16].min
        (0xffff << (16 - bits)) & 0xffff
      end
      Addrinfo.ip(Socket.__ir_unpack_ipv6(words.pack("n8")))
    else
      mask = prefix.zero? ? 0 : ((0xffffffff << (32 - prefix)) & 0xffffffff)
      Addrinfo.ip([mask].pack("N").unpack("C4").join("."))
    end
  end

  def self.getifaddrs
    index = 0
    __ir_interfaces.flat_map do |ni|
      index += 1
      up = ni.operational_status.to_s == "Up"
      loopback = ni.network_interface_type.to_s == "Loopback"
      flags = (up ? IFF_UP__ : 0) | (loopback ? IFF_LOOPBACK__ : 0)
      name = ni.name.to_s
      unicast = begin
        ni.get_ip_properties.unicast_addresses.to_a
      rescue Exception
        []
      end
      unicast.map do |u|
        address = u.address.to_s.split("%").first.to_s
        prefix = begin
          u.prefix_length
        rescue Exception
          nil
        end
        Ifaddr.new(name, index, flags, Addrinfo.ip(address),
                   __ir_netmask_for(address, prefix), nil, nil)
      end
    end
  end

  def self.ip_address_list
    list = getifaddrs.map { |ifaddr| ifaddr.addr }.compact
    return list unless list.empty?
    [Addrinfo.ip("127.0.0.1")]
  end
end

# ---------------------------------------------------------------------------
# SocketException -> Errno.
#
# .NET reports every socket failure as a SocketException, and because
# Socket::SocketError *is* that CLR type an unmapped one surfaces in Ruby as
# SocketError.  CRuby raises an Errno for anything the kernel reported through
# errno and keeps SocketError for name resolution.  SocketError.cs translates
# the handful of codes that have a CLR Errno class; the rest of the errno table
# only exists in Ruby, so the general mapping lives here.

module IronRubySocketErrors__ # :nodoc: all
  BY_CODE = {
    "AccessDenied"                => :EACCES,
    "AddressAlreadyInUse"         => :EADDRINUSE,
    "AddressFamilyNotSupported"   => :EAFNOSUPPORT,
    "AddressNotAvailable"         => :EADDRNOTAVAIL,
    "ConnectionAborted"           => :ECONNABORTED,
    "ConnectionRefused"           => :ECONNREFUSED,
    "ConnectionReset"             => :ECONNRESET,
    "DestinationAddressRequired"  => :EDESTADDRREQ,
    "HostDown"                    => :EHOSTDOWN,
    "HostUnreachable"             => :EHOSTUNREACH,
    "Interrupted"                 => :EINTR,
    "InvalidArgument"             => :EINVAL,
    "IsConnected"                 => :EISCONN,
    "MessageSize"                 => :EMSGSIZE,
    "NetworkDown"                 => :ENETDOWN,
    "NetworkReset"                => :ENETRESET,
    "NetworkUnreachable"          => :ENETUNREACH,
    "NoBufferSpaceAvailable"      => :ENOBUFS,
    "NotConnected"                => :ENOTCONN,
    "NotSocket"                   => :ENOTSOCK,
    "OperationNotSupported"       => :EOPNOTSUPP,
    "ProtocolFamilyNotSupported"  => :EPFNOSUPPORT,
    "ProtocolNotSupported"        => :EPROTONOSUPPORT,
    "ProtocolOption"              => :ENOPROTOOPT,
    "ProtocolType"                => :EPROTOTYPE,
    "Shutdown"                    => :EPIPE,
    "SocketNotSupported"          => :ESOCKTNOSUPPORT,
    "TimedOut"                    => :ETIMEDOUT,
  }.freeze

  # WouldBlock has its own IO::WaitReadable/WaitWritable treatment at each
  # non-blocking entry point, and the resolver codes are what SocketError is for.
  KEEP = %w[WouldBlock InProgress Success HostNotFound NoData TryAgain NoRecovery
            TypeNotFound SystemNotReady VersionNotSupported].freeze

  def self.translate(error)
    if error.kind_of?(SocketError)
      code = Socket.__ir_socket_error_code(error)
      return error if KEEP.include?(code)
      name = BY_CODE[code]
      return error if name.nil? || !Errno.const_defined?(name)
      return Errno.const_get(name).new
    end
    if error.class.name == "System::ObjectDisposedException"
      return IOError.new("closed stream")
    end
    error
  end

  def self.wrap(klass, *names)
    names.each do |name|
      next unless klass.method_defined?(name) || klass.private_method_defined?(name)
      raw = :"__ir_errmap_#{name}"
      next if klass.method_defined?(raw) || klass.private_method_defined?(raw)
      klass.__send__(:alias_method, raw, name)
      klass.__send__(:define_method, name) do |*args, &block|
        begin
          __send__(raw, *args, &block)
        rescue Exception => error
          mapped = IronRubySocketErrors__.translate(error)
          raise mapped.equal?(error) ? error : mapped
        end
      end
      klass.__send__(:private, name) if klass.private_method_defined?(raw)
    end
  end
end

IronRubySocketErrors__.wrap(BasicSocket,
                            :recv, :send, :getsockname, :getpeername,
                            :setsockopt, :getsockopt, :shutdown,
                            :close_read, :close_write,
                            :read, :write, :sysread, :syswrite, :readpartial,
                            :gets, :readline, :readlines, :print, :puts, :<<, :flush,
                            :each_line)
IronRubySocketErrors__.wrap(Socket, :bind, :connect, :listen, :accept, :sysaccept, :recvfrom)
IronRubySocketErrors__.wrap(TCPServer, :accept, :listen, :sysaccept)
IronRubySocketErrors__.wrap(TCPSocket, :recvfrom)
IronRubySocketErrors__.wrap(UDPSocket, :bind, :connect, :recvfrom)

class TCPServer
  class << self
    alias_method :__ir_errmap_new, :new

    def new(*args, &block)
      __ir_errmap_new(*args, &block)
    rescue Exception => error
      mapped = IronRubySocketErrors__.translate(error)
      raise mapped.equal?(error) ? error : mapped
    end
  end
end

class TCPSocket
  class << self
    alias_method :__ir_errmap_new, :new

    def new(*args, **opts, &block)
      __ir_errmap_new(*args, **opts, &block)
    rescue Exception => error
      mapped = IronRubySocketErrors__.translate(error)
      raise mapped.equal?(error) ? error : mapped
    end
  end
end

# Socket#accept and #sysaccept answer [socket, Addrinfo]; the C# layer hands
# back the packed sockaddr getpeername(2) produced.  InvalidOperationException is
# what .NET raises for accept(2) on a socket that was never listened on, where
# the kernel -- and CRuby -- report EINVAL.
class Socket
  alias_method :__ir_raw_accept, :accept
  alias_method :__ir_raw_sysaccept, :sysaccept

  def __ir_accepted(pair) # :nodoc:
    return pair unless pair.kind_of?(Array) && pair.size == 2
    [pair[0], Addrinfo.__ir_from_sockaddr(pair[1], Socket::SOCK_STREAM)]
  end
  private :__ir_accepted

  def accept
    __ir_accepted(__ir_raw_accept)
  end

  def sysaccept
    pair = __ir_raw_sysaccept
    return pair unless pair.kind_of?(Array) && pair.size == 2
    [pair[0], Addrinfo.__ir_from_sockaddr(pair[1], Socket::SOCK_STREAM)]
  end

  def accept_nonblock(exception: true)
    __ir_accepted(__ir_raw_accept_nonblock)
  rescue SocketError => e
    case Socket.__ir_socket_error_code(e)
    when "WouldBlock"
      raise IO::EAGAINWaitReadable, "Resource temporarily unavailable" if exception
      :wait_readable
    else
      raise
    end
  end
end

module IronRubySocketErrors__ # :nodoc: all
  class << self
    alias_method :__ir_translate_socket, :translate

    def translate(error)
      if error.class.name == "System::InvalidOperationException"
        return Errno::EINVAL.new
      end
      __ir_translate_socket(error)
    end
  end
end

IronRubySocketErrors__.wrap(Socket, :accept, :sysaccept)

# ---------------------------------------------------------------------------
# The remaining shapes: half-closed sockets, Addrinfo destinations, the IPv6
# arms of the name-lookup singletons, and the Socket-returning factories.

class BasicSocket
  alias_method :__ir_half_close_read, :close_read
  alias_method :__ir_half_close_write, :close_write
  alias_method :__ir_dest_send, :send

  # CRuby's close_read/close_write only shut one direction down; the socket stays
  # open until both have been closed, and the closed direction answers IOError
  # rather than whatever errno the shutdown left behind.
  def close_read
    raise IOError, "closed stream" if closed?
    unless @__ir_read_closed
      @__ir_read_closed = true
      __ir_half_close_read rescue nil
    end
    close if @__ir_write_closed
    nil
  end

  def close_write
    raise IOError, "closed stream" if closed?
    unless @__ir_write_closed
      @__ir_write_closed = true
      __ir_half_close_write rescue nil
    end
    close if @__ir_read_closed
    nil
  end

  def __ir_check_readable # :nodoc:
    raise IOError, "not opened for reading" if @__ir_read_closed && !closed?
  end
  private :__ir_check_readable

  def __ir_check_writable # :nodoc:
    raise IOError, "not opened for writing" if @__ir_write_closed && !closed?
  end
  private :__ir_check_writable

  # A destination may be an Addrinfo as well as a packed sockaddr.
  def send(message, flags = 0, dest = nil)
    __ir_check_writable
    return __ir_dest_send(message, flags) if dest.nil?
    dest = dest.to_sockaddr if Addrinfo === dest
    __ir_dest_send(message, flags, dest)
  end
end

class Socket
  class << self
    alias_method :__ir_raw_getnameinfo, :getnameinfo
    alias_method :__ir_raw_gethostbyaddr, :gethostbyaddr

    # The C# getnameinfo builds an IPv4 endpoint out of the Array form, so an
    # AF_INET6 tuple never reached the resolver.  Pack it instead.
    def getnameinfo(sockaddr, flags = 0)
      if sockaddr.kind_of?(Array)
        port = sockaddr[1].to_i
        address = (sockaddr[3] || sockaddr[2]).to_s
        host, service = __ir_raw_getnameinfo(sockaddr_in(port, address), flags)
      else
        sockaddr = sockaddr.to_sockaddr if Addrinfo === sockaddr
        host, service = __ir_raw_getnameinfo(sockaddr, flags)
        port = sockaddr.to_str.byteslice(2, 2).unpack("n")[0]
        address = nil
      end
      if (flags.to_i & NI_NUMERICSERV) != 0
        service = port.to_s
      end
      if (flags.to_i & NI_NUMERICHOST) != 0 && address
        host = address
      end
      [host, service]
    end

    def __ir_hostent(name, aliases, addresses) # :nodoc:
      addresses = addresses.uniq
      family = addresses.first.to_s.include?(":") ? AF_INET6 : AF_INET
      packed = addresses.map do |address|
        address.include?(":") ? __ir_pack_ipv6(address) : address.split(".").map { |o| o.to_i }.pack("C4")
      end
      [name, aliases, family, *packed]
    end

    # The C# gethostbyname/gethostbyaddr stop at [name, aliases] for IPv6 and
    # never report the family or the packed address.
    def gethostbyname(host)
      text = host.to_s
      case text
      when "<broadcast>" then return ["255.255.255.255", [], AF_INET, [255, 255, 255, 255].pack("C4")]
      when "<any>" then return ["0.0.0.0", [], AF_INET, [0, 0, 0, 0].pack("C4")]
      end
      raw = __ir_raw_gethostbyname(host)
      addresses = begin
        __ir_raw_getaddrinfo(host, nil, 0, 0, 0, 0).map { |entry| entry[3] }
      rescue StandardError
        []
      end
      addresses = [text] if addresses.empty?
      numeric = text.include?(":") || text =~ /\A\d{1,3}(\.\d{1,3}){3}\z/
      __ir_hostent(numeric ? text : raw[0], raw[1] || [], addresses)
    end

    def gethostbyaddr(address, family = nil)
      raw = family.nil? ? __ir_raw_gethostbyaddr(address) : __ir_raw_gethostbyaddr(address, __ir_family_arg(family))
      bytes = address.kind_of?(String) ? address : address.to_str
      text = if bytes.bytesize == 16
        __ir_unpack_ipv6(bytes)
      else
        bytes.unpack("C4").join(".")
      end
      __ir_hostent(raw[0], raw[1] || [], [text])
    end
  end

  # CRuby's Socket.tcp / .tcp_server_sockets / .udp_server_sockets return Socket
  # instances, not TCPSocket/TCPServer/UDPSocket.
  def self.tcp(host, port, local_host = nil, local_port = nil,
               connect_timeout: nil, resolv_timeout: nil) # :yield: socket
    remote = Addrinfo.tcp(host, port)
    sock = if local_host || local_port
      remote.connect_from(local_host || (remote.ipv6? ? "::" : "0.0.0.0"), local_port.to_i)
    else
      remote.connect
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
    sockets = [Addrinfo.tcp(host || "0.0.0.0", port.to_i).listen]
    return sockets unless block_given?
    begin
      yield sockets
    ensure
      sockets.each { |s| s.close unless s.closed? }
    end
  end

  def self.udp_server_sockets(host = nil, port = nil)
    host, port = nil, host if port.nil?
    sock = Socket.new(host.to_s.include?(":") ? AF_INET6 : AF_INET, SOCK_DGRAM, 0)
    sock.bind(Socket.sockaddr_in(port.to_i, host || "0.0.0.0"))
    sockets = [sock]
    return sockets unless block_given?
    begin
      yield sockets
    ensure
      sockets.each { |s| s.close unless s.closed? }
    end
  end
end

class TCPSocket
  class << self
    def gethostbyname(host)
      Socket.gethostbyname(host)
    end
  end
end

IronRubySocketErrors__.wrap(BasicSocket, :close_read, :close_write, :send)

class BasicSocket
  # Reading from a socket whose read half is closed is IOError in CRuby, not the
  # errno the shutdown left behind.
  [[:read, :__ir_check_readable], [:readpartial, :__ir_check_readable],
   [:sysread, :__ir_check_readable], [:gets, :__ir_check_readable],
   [:readline, :__ir_check_readable], [:readlines, :__ir_check_readable],
   [:each_line, :__ir_check_readable], [:recv, :__ir_check_readable],
   [:write, :__ir_check_writable], [:syswrite, :__ir_check_writable],
   [:print, :__ir_check_writable], [:puts, :__ir_check_writable],
   [:<<, :__ir_check_writable]].each do |name, check|
    next unless method_defined?(name)
    raw = :"__ir_halfclose_#{name}"
    next if method_defined?(raw)
    alias_method raw, name
    define_method(name) do |*args, &block|
      __send__(check)
      __send__(raw, *args, &block)
    end
  end
end

class UDPSocket
  # UDPSocket defines its own #send (it also takes host/port), so the Addrinfo
  # destination has to be unpacked here too.
  alias_method :__ir_dest_send, :send

  def send(message, flags = 0, *dest)
    __ir_dest_send(message, flags, *dest.map { |d| Addrinfo === d ? d.to_sockaddr : d })
  end
end

IronRubySocketErrors__.wrap(UDPSocket, :send)
