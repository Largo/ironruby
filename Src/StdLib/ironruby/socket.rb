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
