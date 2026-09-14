/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

#if FEATURE_SYNC_SOCKETS

using System.Net.Sockets;
using Microsoft.Scripting.Runtime;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Runtime.InteropServices;
using System.Net;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("TCPSocket", BuildConfig = "FEATURE_SYNC_SOCKETS")]
    public class TCPSocket : IPSocket {
        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public TCPSocket(RubyContext/*!*/ context)
            : base(context) {
        }
        
        public TCPSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        [RubyConstructor]
        public static TCPSocket/*!*/ CreateTCPSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, [DefaultProtocol]MutableString remoteHost, object remotePort, [Optional]int localPort) {

            // Not sure what the semantics should be in this case but we make sure not to blow up.
            // Real-world code (Server.connect_to in memcache.rb in the memcache-client gem) does do "TCPSocket.new(host, port, 0)"
            if (localPort != 0) {
                throw new NotImplementedError();
            }

            return new TCPSocket(self.Context, CreateSocket(remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort)));
        }

        [RubyConstructor]
        public static TCPSocket/*!*/ CreateTCPSocket(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            RubyClass/*!*/ self, 
            [DefaultProtocol]MutableString remoteHost, object remotePort,
            [DefaultProtocol]MutableString localHost, object localPort) {

            return BindLocalEndPoint(
                CreateTCPSocket(stringCast, fixnumCast, self, remoteHost, remotePort, 0),
                localHost,
                ConvertToPortNum(stringCast, fixnumCast, localPort)
            );
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static TCPServer/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            TCPServer/*!*/ self, [DefaultProtocol]MutableString remoteHost, object remotePort, [Optional]int localPort) {

            // Not sure what the semantics should be in this case but we make sure not to blow up.
            // Real-world code (Server.connect_to in memcache.rb in the memcache-client gem) does do "TCPSocket.new(host, port, 0)"
            if (localPort != 0) {
                throw new NotImplementedError();
            }

            self.Socket = CreateSocket(remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort));
            return self;
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static TCPServer/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast,
            TCPServer/*!*/ self,
            [DefaultProtocol]MutableString remoteHost, object remotePort,
            [DefaultProtocol]MutableString localHost, object localPort) {

            self.Socket = CreateSocket(remoteHost, ConvertToPortNum(stringCast, fixnumCast, remotePort));
            BindLocalEndPoint(self, localHost, ConvertToPortNum(stringCast, fixnumCast, localPort));
            return self;
        }

        private static Socket/*!*/ CreateSocket(MutableString remoteHost, int port) {
            // Resolve first: the address family has to follow the address, not be pinned to
            // InterNetwork, or TCPSocket.new("::1", port) can never work and every IPv6-guarded
            // spec in the tree silently skips.
            IPAddress address = remoteHost != null ? GetHostAddress(remoteHost.ConvertToString()) : IPAddress.Loopback;
            Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try {
                socket.Connect(address, port);
            } catch (SocketException e) {
                socket.Close();
                throw SocketErrorOps.ToRubyException(e);
            }
            return socket;
        }

        private static TCPSocket/*!*/ BindLocalEndPoint(TCPSocket/*!*/ socket, MutableString localHost, int localPort) {
            AddressFamily family = socket.Socket.AddressFamily;
            IPAddress localIPAddress;
            if (localHost != null) {
                localIPAddress = GetHostAddress(localHost.ConvertToString(), family);
            } else {
                localIPAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            }
            IPEndPoint localEndPoint = new IPEndPoint(localIPAddress, localPort);
            try {
                socket.Socket.Bind(localEndPoint);
            } catch (SocketException e) {
                throw SocketErrorOps.ToRubyException(e);
            }
            return socket;
        }

        [RubyMethod("gethostbyname", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetHostByName(ConversionStorage<MutableString>/*!*/ stringCast, RubyClass/*!*/ self, object hostNameOrAddress) {
            return GetHostByName(self.Context, ConvertToHostString(stringCast, hostNameOrAddress), false);
        }
    }
}
#endif
