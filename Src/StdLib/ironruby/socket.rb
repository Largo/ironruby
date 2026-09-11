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
