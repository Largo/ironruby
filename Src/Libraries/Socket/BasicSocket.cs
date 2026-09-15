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

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.StandardLibrary.FileControl;
using System.Numerics;
using IronRuby.Runtime.Calls;
using System.Globalization;
using IronRuby.Compiler;

namespace IronRuby.StandardLibrary.Sockets {
    [RubyClass("BasicSocket", BuildConfig = "FEATURE_SYNC_SOCKETS")]
    public abstract class RubyBasicSocket : RubyIO {
        // TODO: do these escape out of the library?
        private static readonly MutableString BROADCAST_STRING = MutableString.CreateAscii("<broadcast>").Freeze();

        private Socket _socket;
        private bool _doNotReverseLookup;

        [MultiRuntimeAware]
        private static readonly object BasicSocketClassKey = new object();

        internal static StrongBox<bool> DoNotReverseLookup(RubyContext/*!*/ context) {
            Assert.NotNull(context);

            // CRuby has defaulted this to true since 1.9; leaving it false made every
            // #addr/#peeraddr do a live reverse DNS lookup.
            return (StrongBox<bool>)context.GetOrCreateLibraryData(BasicSocketClassKey, () => new StrongBox<bool>(true));
        }

        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        protected RubyBasicSocket(RubyContext/*!*/ context)
            : base(context) {
            Mode = IOMode.ReadWrite | IOMode.PreserveEndOfLines;
            SetEncodings(RubyEncoding.Binary, null);
            _doNotReverseLookup = DoNotReverseLookup(context).Value;
        }

        /// <summary>
        /// Create a new RubyBasicSocket from a specified stream and mode
        /// </summary>
        protected RubyBasicSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, new SocketStream(socket), IOMode.ReadWrite | IOMode.PreserveEndOfLines) {
            _socket = socket;
            SetEncodings(RubyEncoding.Binary, null);
            // CRuby snapshots BasicSocket.do_not_reverse_lookup into the socket at creation.
            _doNotReverseLookup = DoNotReverseLookup(context).Value;
        }

        public override int SetReadTimeout(int timeout) {
            int old = _socket.ReceiveTimeout;
            _socket.ReceiveTimeout = timeout;
            return old;
        }

        public override void NonBlockingOperation(Action operation, bool isRead) {
            bool wasBlocking = _socket.Blocking;
            try {
                _socket.Blocking = false;
                operation();
            } catch (SocketException e) {
                if (e.SocketErrorCode == SocketError.WouldBlock) {
                    throw RubyIOOps.NonBlockingError(Context, new Errno.ResourceTemporarilyUnavailableError(), isRead);
                }
                throw;
            } finally {
                _socket.Blocking = wasBlocking;
            }
        }

        protected internal Socket/*!*/ Socket {
            get {
                if (_socket == null) {
                    throw RubyExceptions.CreateIOError("uninitialized stream"); 
                }
                return _socket; 
            }
            set {
                ContractUtils.RequiresNotNull(value, "value");
                Reset(new SocketStream(value), IOMode.ReadWrite | IOMode.PreserveEndOfLines);
                _socket = value;
            }
        }

        public override WaitHandle/*!*/ CreateReadWaitHandle() {
            return Socket.BeginReceive(Utils.EmptyBytes, 0, 0, SocketFlags.Peek, null, null).AsyncWaitHandle;
        }

        public override WaitHandle/*!*/ CreateWriteWaitHandle() {
            return Socket.BeginSend(Utils.EmptyBytes, 0, 0, SocketFlags.Peek, null, null).AsyncWaitHandle;
        }

        public override WaitHandle/*!*/ CreateErrorWaitHandle() {
            // TODO:
            throw new NotSupportedException();
        }

        // The Linux fcntl(2) command numbers, which is what io/nonblock.rb and IO#fcntl use;
        // Fcntl::F_SETFL in this tree is a made-up 1.
        private const int LinuxGetFileFlags = 3;
        private const int LinuxSetFileFlags = 4;
        private const int LinuxNonBlock = 0x800;

        // returns 0 on success, -1 on failure
        private int SetFileControlFlags(int flags) {
            Socket.Blocking = (flags & (RubyFileOps.Constants.NONBLOCK | LinuxNonBlock)) == 0;
            return 0;
        }

        public override int FileControl(int commandId, int arg) {
            switch (commandId) {
                case Fcntl.F_SETFL:
                case LinuxSetFileFlags:
                    return SetFileControlFlags(arg);
                case LinuxGetFileFlags:
                    // .NET owns the blocking flag; report it the way fcntl(2) would, which is
                    // what io/nonblock's #nonblock? and #nonblock= read back.
                    return Socket.Blocking ? 0 : LinuxNonBlock;
            }
            throw new NotSupportedException();
        }

        public override int FileControl(int commandId, byte[] arg) {
            // TODO:
            throw new NotSupportedException();
        }

#region do_not_reverse_lookup, for_fd

        /// <summary>
        /// Returns the value of the global reverse lookup flag.
        /// </summary>
        [RubyMethod("do_not_reverse_lookup", RubyMethodAttributes.PublicSingleton)]
        public static bool GetDoNotReverseLookup(RubyClass/*!*/ self) {
            return DoNotReverseLookup(self.Context).Value;
        }

        /// <summary>
        /// Sets the value of the global reverse lookup flag.
        /// If set to true, queries on remote addresses will return the numeric address but not the host name.
        /// Defaults to false.
        /// </summary>
        [RubyMethod("do_not_reverse_lookup=", RubyMethodAttributes.PublicSingleton)]
        public static void SetDoNotReverseLookup(RubyClass/*!*/ self, bool value) {
            Protocols.CheckSafeLevel(self.Context, 4);
            DoNotReverseLookup(self.Context).Value = value;
        }

        /// <summary>
        /// Returns the value of the global reverse lookup flag.
        /// </summary>
        [RubyMethod("do_not_reverse_lookup")]
        public static bool GetDoNotReverseLookup(RubyBasicSocket/*!*/ self) {
            return self._doNotReverseLookup;
        }

        /// <summary>
        /// Sets the value of the global reverse lookup flag.
        /// If set to true, queries on remote addresses will return the numeric address but not the host name.
        /// Defaults to false.
        /// </summary>
        [RubyMethod("do_not_reverse_lookup=")]
        public static void SetDoNotReverseLookup(RubyBasicSocket/*!*/ self, bool value) {
            Protocols.CheckSafeLevel(self.Context, 4);
            self._doNotReverseLookup = value;
        }

        /// <summary>
        /// Wraps an already open file descriptor into a socket object.  The descriptor is an
        /// index into the context's descriptor table (see RubyContext.AllocateFileDescriptor),
        /// so the socket it names is recoverable from the table's SocketStream; the result
        /// keeps the same fileno, as CRuby's does.
        /// </summary>
        [RubyMethod("for_fd", RubyMethodAttributes.PublicSingleton)]
        public static RubyBasicSocket/*!*/ ForFileDescriptor(RubyClass/*!*/ self, [DefaultProtocol]int fileDescriptor) {
            SocketStream stream = self.Context.GetStream(fileDescriptor) as SocketStream;
            if (stream == null) {
                throw RubyExceptions.CreateEBADF();
            }
            RubyBasicSocket result = CreateForClass(self, stream.Socket);
            result.SetFileDescriptor(fileDescriptor);
            return result;
        }

        /// <summary>
        /// socketpair(2).  There is no .NET equivalent, and the usual emulation -- bind a listener
        /// on a temporary path, connect, accept -- would give both ends a non-empty #path and leave
        /// a file behind, neither of which matches CRuby.  Wrapping the raw descriptors instead
        /// keeps them genuinely anonymous.  Defined on BasicSocket so that both Socket.pair and
        /// UNIXSocket.pair, which answer instances of their own class, can reach it.
        /// </summary>
        [RubyMethod("__ir_raw_socketpair", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ CreateSocketPair(RubyClass/*!*/ self, [DefaultProtocol]int type, [DefaultProtocol]int protocol) {
            int[] descriptors = new int[2];
            if (PosixMessages.socketpair(UnixAddressFamilyNumber, type, protocol, descriptors) != 0) {
                throw Posix.Error(Marshal.GetLastWin32Error(), null);
            }
            RubyArray result = new RubyArray(2);
            result.Add(CreateForClass(self, new Socket(new SafeSocketHandle((IntPtr)descriptors[0], true))));
            result.Add(CreateForClass(self, new Socket(new SafeSocketHandle((IntPtr)descriptors[1], true))));
            return result;
        }

        internal static RubyBasicSocket/*!*/ CreateForClass(RubyClass/*!*/ self, Socket/*!*/ socket) {
            Type type = self.GetUnderlyingSystemType();
            if (typeof(TCPServer).IsAssignableFrom(type)) {
                return new TCPServer(self.Context, socket);
            }
            if (typeof(TCPSocket).IsAssignableFrom(type)) {
                return new TCPSocket(self.Context, socket);
            }
            if (typeof(UDPSocket).IsAssignableFrom(type)) {
                return new UDPSocket(self.Context, socket);
            }
            if (typeof(UNIXServer).IsAssignableFrom(type)) {
                return new UNIXServer(self.Context, socket);
            }
            if (typeof(UNIXSocket).IsAssignableFrom(type)) {
                return new UNIXSocket(self.Context, socket);
            }
            return new RubySocket(self.Context, socket);
        }

        /// <summary>
        /// The wildcard endpoint of a socket's own family, for seeding ReceiveFrom.  Handing
        /// an IPv4 endpoint to an IPv6 socket is an ArgumentError from .NET.
        /// </summary>
        internal static EndPoint/*!*/ AnyEndPoint(AddressFamily family) {
            if (family == AddressFamily.Unix) {
                return UNIXSocket.ToEndPoint("");
            }
            return family == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);
        }

        /// <summary>
        /// Turns a packed sockaddr into an EndPoint.  The obvious
        /// Socket.LocalEndPoint.Create(...) does not work for a socket that was never bound:
        /// .NET has no local endpoint for one and returns null.
        /// </summary>
        internal static EndPoint/*!*/ CreateEndPoint(MutableString/*!*/ sockaddr) {
            byte[] bytes = sockaddr.ConvertToBytes();
            if (bytes.Length >= 2 && (bytes[0] | (bytes[1] << 8)) == UnixAddressFamilyNumber) {
                // sockaddr_un: the path runs to the first NUL, or to the end if there is none.
                int end = 2;
                while (end < bytes.Length && bytes[end] != 0) {
                    end++;
                }
                return UNIXSocket.ToEndPoint(Encoding.UTF8.GetString(bytes, 2, end - 2));
            }
            if (bytes.Length < 8) {
                throw RubyExceptions.CreateArgumentError("not a valid sockaddr");
            }
            int family = bytes[0] | (bytes[1] << 8);
            bool v6 = family == 10 || family == 23 || family == 28 || family == 30;
            AddressFamily addressFamily = v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            int size = v6 ? 28 : 16;
            SocketAddress address = new SocketAddress(addressFamily, size);
            for (int i = 2; i < size && i < bytes.Length; i++) {
                address[i] = bytes[i];
            }
            return new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, 0).Create(address);
        }

        /// <summary>
        /// The all-zero sockaddr CRuby's getsockname(2) reports for a socket that has never
        /// been bound.
        /// </summary>
        /// <summary>AF_UNIX on Linux; unlike AF_INET6 this number is the same everywhere.</summary>
        internal const int UnixAddressFamilyNumber = 1;

        private static MutableString/*!*/ EmptySocketAddress(AddressFamily family) {
            if (family == AddressFamily.Unix) {
                return MutableString.CreateBinary(new byte[] { UnixAddressFamilyNumber, 0 });
            }
            bool v6 = family == AddressFamily.InterNetworkV6;
            byte[] bytes = new byte[v6 ? 28 : 16];
            int number = v6 ? 10 : 2;
            bytes[0] = (byte)(number & 0xff);
            bytes[1] = (byte)(number >> 8);
            return MutableString.CreateBinary(bytes);
        }

#endregion

#region Blocking operations

        // CRuby reports a thread parked in a blocking socket call as "sleep". IronRuby derives
        // Thread#status from the CLR thread state, which stays Running while the thread sits in a
        // native socket call, so every blocking entry point has to flag itself the way
        // TCPServer#accept always has. Without this the ruby/spec idiom
        // "Thread.pass while t.status != 'sleep'" livelocks.
        internal static TResult Blocking<TResult>(Func<TResult>/*!*/ operation) {
            return BlockingCore(null, SelectMode.SelectRead, operation);
        }

        // A thread sitting inside a native socket call is not in WaitSleepJoin, so Thread.Interrupt
        // -- how Thread#kill and Thread#raise are delivered -- cannot reach it, and the thread can
        // never be killed. Wait for readiness in slices instead and check for a parked asynchronous
        // exception between them, which turns the wait into a Ruby safe point. mspec's block_caller
        // matcher does exactly "wait for status == sleep, then kill and join".
        private const int PollSliceMicroseconds = 50 * 1000;

        /// <summary>
        /// A read that waits for data. A *stream* socket that is not connected does not wait at all --
        /// recv(2) on it fails with ENOTCONN, and polling it would wait forever for readiness that can
        /// never come (TCPServer#gets is the case that matters). Datagram sockets do wait, connected
        /// or not.
        /// </summary>
        internal static TResult Blocking<TResult>(Socket socket, SelectMode mode, Func<TResult>/*!*/ operation) {
            bool waitable = socket != null && (socket.Connected || socket.SocketType != SocketType.Stream);
            return BlockingCore(waitable ? socket : null, mode, operation);
        }

        /// <summary>
        /// accept(2): a listening socket is never "connected", so it needs the wait unconditionally.
        /// </summary>
        internal static TResult BlockingAccept<TResult>(Socket/*!*/ socket, Func<TResult>/*!*/ operation) {
            if (!socket.IsBound) {
                // .NET answers accept(2) on an unbound or unlistened socket with an
                // InvalidOperationException, which surfaces in Ruby as TypeError; the kernel,
                // and CRuby, report EINVAL.
                throw new InvalidError();
            }
            try {
                return BlockingCore(socket, SelectMode.SelectRead, operation);
            } catch (InvalidOperationException) {
                throw new InvalidError();
            }
        }

        private static TResult BlockingCore<TResult>(Socket socket, SelectMode mode, Func<TResult>/*!*/ operation) {
            ThreadOps.RubyThreadInfo info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            bool wasBlocked = info.Blocked;
            info.Blocked = true;
            try {
                // Only a socket left in blocking mode can park here; the *_nonblock family has
                // already cleared Socket.Blocking and must not wait at all.
                if (socket != null && socket.Blocking) {
                    while (!socket.Poll(PollSliceMicroseconds, mode)) {
                        RubyUtils.CheckAsyncException();
                    }
                }
                return operation();
            } finally {
                info.Blocked = wasBlocked;
            }
        }

        internal static void Blocking(Action/*!*/ operation) {
            ThreadOps.RubyThreadInfo info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            bool wasBlocked = info.Blocked;
            info.Blocked = true;
            try {
                operation();
            } finally {
                info.Blocked = wasBlocked;
            }
        }

#endregion

#region Public Instance Methods

        [RubyMethod("close_read")]
        public static void CloseRead(RubyContext/*!*/ context, RubyBasicSocket/*!*/ self) {
            CheckSecurity(context, self, "can't close socket");
            // TODO: It would be nice to alter the SocketStream to be WriteOnly here
            self.Socket.Shutdown(SocketShutdown.Receive);
        }

        [RubyMethod("close_write")]
        public static void CloseWrite(RubyContext/*!*/ context, RubyBasicSocket/*!*/ self) {
            CheckSecurity(context, self, "can't close socket");
            // TODO: It would be nice to alter the SocketStream to be ReadOnly here
            self.Socket.Shutdown(SocketShutdown.Send);
        }

        [RubyMethod("shutdown")]
        public static int Shutdown(RubyContext/*!*/ context, RubyBasicSocket/*!*/ self, [DefaultProtocol, DefaultParameterValue(2)]int how) {
            CheckSecurity(context, self, "can't shutdown socket");
            if (how < 0 || 2 < how) {
                throw RubyExceptions.CreateArgumentError("`how' should be either 0, 1, 2");
            }
            // This used to close() instead, to work around webrick trouble in 2009. Closing is
            // observably wrong -- the socket stays open after shutdown in every other Ruby, and
            // #recv on a read-shutdown socket has to return "" rather than raise.
            self.Socket.Shutdown((SocketShutdown)how);
            return 0;
        }

        /// <summary>
        /// Sets a socket option. These are protocol and system specific, see your local sytem documentation for details. 
        /// </summary>
        /// <param name="level">level is an integer, usually one of the SOL_ constants such as Socket::SOL_SOCKET, or a protocol level.</param>
        /// <param name="optname">optname is an integer, usually one of the SO_ constants, such as Socket::SO_REUSEADDR.</param>
        /// <param name="value">value is the value of the option, it is passed to the underlying setsockopt() as a pointer to a certain number of bytes. How this is done depends on the type.</param>
        [RubyMethod("setsockopt")]
        public static void SetSocketOption(ConversionStorage<int>/*!*/ conversionStorage, RubyContext/*!*/ context, 
            RubyBasicSocket/*!*/ self, [DefaultProtocol]int level, [DefaultProtocol]int optname, int value) {

            Protocols.CheckSafeLevel(context, 2, "setsockopt");
            self.Socket.SetSocketOption((SocketOptionLevel)level, (SocketOptionName)optname, value);
        }

        /// <summary>
        /// Sets a socket option. These are protocol and system specific, see your local sytem documentation for details. 
        /// </summary>
        /// <param name="level">level is an integer, usually one of the SOL_ constants such as Socket::SOL_SOCKET, or a protocol level.</param>
        /// <param name="optname">optname is an integer, usually one of the SO_ constants, such as Socket::SO_REUSEADDR.</param>
        /// <param name="value">value is the value of the option, it is passed to the underlying setsockopt() as a pointer to a certain number of bytes. How this is done depends on the type.</param>
        [RubyMethod("setsockopt")]
        public static void SetSocketOption(ConversionStorage<int>/*!*/ conversionStorage, RubyContext/*!*/ context, 
            RubyBasicSocket/*!*/ self, [DefaultProtocol]int level, [DefaultProtocol]int optname, bool value) {

            Protocols.CheckSafeLevel(context, 2, "setsockopt");
            self.Socket.SetSocketOption((SocketOptionLevel)level, (SocketOptionName)optname, value);
        }

        /// <summary>
        /// Sets a socket option. These are protocol and system specific, see your local sytem documentation for details. 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="self"></param>
        /// <param name="level">level is an integer, usually one of the SOL_ constants such as Socket::SOL_SOCKET, or a protocol level.</param>
        /// <param name="optname">optname is an integer, usually one of the SO_ constants, such as Socket::SO_REUSEADDR.</param>
        /// <param name="value">value is the value of the option, it is passed to the underlying setsockopt() as a pointer to a certain number of bytes. How this is done depends on the type.</param>
        /// <example>
        /// Some socket options are integers with boolean values, in this case setsockopt could be called like this: 
        /// 
        ///   sock.setsockopt(Socket::SOL_SOCKET,Socket::SO_REUSEADDR, true)
        /// Some socket options are integers with numeric values, in this case setsockopt could be called like this: 
        /// 
        ///   sock.setsockopt(Socket::IPPROTO_IP, Socket::IP_TTL, 255)
        /// Option values may be structs. Passing them can be complex as it involves examining your system headers to determine the correct definition. An example is an ip_mreq, which may be defined in your system headers as: 
        /// 
        ///   struct ip_mreq {
        ///     struct  in_addr imr_multiaddr;
        ///     struct  in_addr imr_interface;
        ///   };
        /// In this case setsockopt could be called like this: 
        /// 
        ///   optval =  IPAddr.new("224.0.0.251") + Socket::INADDR_ANY
        ///   sock.setsockopt(Socket::IPPROTO_IP, Socket::IP_ADD_MEMBERSHIP, optval)
        /// </example>
        [RubyMethod("setsockopt")]
        public static void SetSocketOption(RubyContext/*!*/ context, RubyBasicSocket/*!*/ self, 
            [DefaultProtocol]int level, [DefaultProtocol]int optname, [DefaultProtocol, NotNull]MutableString/*!*/ value) {

            Protocols.CheckSafeLevel(context, 2, "setsockopt");
            self.Socket.SetSocketOption((SocketOptionLevel)level, (SocketOptionName)optname, value.ConvertToBytes());
        }

        /// <summary>
        /// Gets a socket option. These are protocol and system specific, see your local sytem documentation for details.
        /// </summary>
        /// <param name="level">level is an integer, usually one of the SOL_ constants such as Socket::SOL_SOCKET, or a protocol level.</param>
        /// <param name="optname">optname is an integer, usually one of the SO_ constants, such as Socket::SO_REUSEADDR.</param>
        /// <returns>The option is returned as a String with the data being the binary value of the socket option.</returns>
        /// <example>
        /// Some socket options are integers with boolean values, in this case getsockopt could be called like this: 
        ///  optval = sock.getsockopt(Socket::SOL_SOCKET,Socket::SO_REUSEADDR)
        ///  optval = optval.unpack "i"
        ///  reuseaddr = optval[0] == 0 ? false : true
        /// Some socket options are integers with numeric values, in this case getsockopt could be called like this: 
        ///  optval = sock.getsockopt(Socket::IPPROTO_IP, Socket::IP_TTL)
        ///  ipttl = optval.unpack("i")[0]
        /// Option values may be structs. Decoding them can be complex as it involves examining your system headers to determine the correct definition. An example is a +struct linger+, which may be defined in your system headers as: 
        ///   struct linger {
        ///     int l_onoff;
        ///     int l_linger;
        ///   };
        /// In this case getsockopt could be called like this: 
        ///   optval =  sock.getsockopt(Socket::SOL_SOCKET, Socket::SO_LINGER)
        ///   onoff, linger = optval.unpack "ii"
        /// </example>
        [RubyMethod("getsockopt")]
        public static MutableString GetSocketOption(ConversionStorage<int>/*!*/ conversionStorage, RubyContext/*!*/ context, 
            RubyBasicSocket/*!*/ self, [DefaultProtocol]int level, [DefaultProtocol]int optname) {
            Protocols.CheckSafeLevel(context, 2, "getsockopt");
            // struct linger is two ints; everything else the specs ask about is one.
            int size = (level == (int)SocketOptionLevel.Socket && optname == (int)SocketOptionName.Linger) ? 8 : 4;
            byte[] value = self.Socket.GetSocketOption((SocketOptionLevel)level, (SocketOptionName)optname, size);
            return MutableString.CreateBinary(value);
        }

        [RubyMethod("getsockname")]
        public static MutableString GetSocketName(RubyBasicSocket/*!*/ self) {
            EndPoint local = self.Socket.LocalEndPoint;
            if (local == null) {
                return EmptySocketAddress(self.Socket.AddressFamily);
            }
            SocketAddress addr = local.Serialize();
            byte[] bytes = new byte[addr.Size];
            for (int i = 0; i < addr.Size; ++i) {
                bytes[i] = addr[i];
            }
            return MutableString.CreateBinary(bytes);
        }

        [RubyMethod("getpeername")]
        public static MutableString GetPeerName(RubyBasicSocket/*!*/ self) {
            EndPoint remote = self.Socket.RemoteEndPoint;
            if (remote == null) {
                throw new Errno.NotConnectedError();
            }
            SocketAddress addr = remote.Serialize();
            byte[] bytes = new byte[addr.Size];
            for (int i = 0; i < addr.Size; ++i) {
                bytes[i] = addr[i];
            }
            return MutableString.CreateBinary(bytes);
        }

        [RubyMethod("send")]
        public static int Send(ConversionStorage<int>/*!*/ fixnumCast, 
            RubyBasicSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ message, object flags) {
            Protocols.CheckSafeLevel(fixnumCast.Context, 4, "send");
            SocketFlags socketFlags = ConvertToSocketFlag(fixnumCast, flags);
            return Blocking(() => self.Socket.Send(message.ConvertToBytes(), socketFlags));
        }

        [RubyMethod("send")]
        public static int Send(ConversionStorage<int>/*!*/ fixnumCast, RubyBasicSocket/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ message, object flags, [DefaultProtocol, NotNull]MutableString/*!*/ to) {
            Protocols.CheckSafeLevel(fixnumCast.Context, 4, "send");
            // Convert the parameters
            SocketFlags socketFlags = ConvertToSocketFlag(fixnumCast, flags);
            // Unpack the socket address information from the to parameter
            EndPoint toEndPoint = CreateEndPoint(to);
            return Blocking(() => self.Socket.SendTo(message.ConvertToBytes(), socketFlags, toEndPoint));
        }

        [RubyMethod("recv")]
        public static MutableString Receive(ConversionStorage<int>/*!*/ fixnumCast, RubyBasicSocket/*!*/ self, 
            [DefaultProtocol]int length, [DefaultParameterValue(null)]object flags) {

            SocketFlags sFlags = ConvertToSocketFlag(fixnumCast, flags);

            byte[] buffer = new byte[length];
            int received = Blocking(self.Socket, SelectMode.SelectRead, () => self.Socket.Receive(buffer, 0, length, sFlags));

            MutableString str = MutableString.CreateBinary(received);
            str.Append(buffer, 0, received);
            str.IsTainted = true;
            return str;
        }

        /// <summary>
        /// Receives up to length bytes from socket using recvfrom after O_NONBLOCK is set for the underlying file descriptor.
        /// </summary>
        /// <param name="length">Maximum number of bytes to receive</param>
        /// <param name="flags">flags is zero or more of the MSG_ options.</param>
        /// <returns>The data received. When recvfrom(2) returns 0, Socket#recv_nonblock returns an empty string as data. The meaning depends on the socket: EOF on TCP, empty packet on UDP, etc. </returns>
        /// <example>
        /// serv = TCPServer.new("127.0.0.1", 0)
        ///      af, port, host, addr = serv.addr
        ///      c = TCPSocket.new(addr, port)
        ///      s = serv.accept
        ///      c.send "aaa", 0
        ///      IO.select([s])
        ///      p s.recv_nonblock(10) #=> "aaa"
        /// </example>
        #region sendmsg, recvmsg

        /// <summary>
        /// sendmsg(2). Answers [errno, bytes-sent] rather than raising, because the Errno classes
        /// are reachable by number from Ruby (SystemCallError.new(message, errno)) and not from
        /// here -- Posix.Error only knows the file-system errnos, and this path can report
        /// EDESTADDRREQ, EMSGSIZE and EAGAIN, none of which it has.
        ///
        /// `controls` is an array of [level, type, data] triples, which is what
        /// Socket::AncillaryData carries.
        /// </summary>
        [RubyMethod("__ir_raw_sendmsg")]
        public static RubyArray/*!*/ SendMessage(RubyBasicSocket/*!*/ self, [NotNull]MutableString/*!*/ message,
            [DefaultProtocol]int flags, MutableString address, RubyArray controls, bool nonBlocking) {

            byte[] data = message.ConvertToBytes();
            byte[] name = (address != null) ? address.ConvertToBytes() : null;
            List<ControlMessage> ancillary = ToControlMessages(controls);
            int descriptor = (int)self.Socket.Handle;

            int errno = 0;
            long sent;
            if (nonBlocking) {
                sent = WithoutBlocking(self.Socket, () => PosixMessages.Send(descriptor, data, flags, name, ancillary, out errno));
            } else {
                sent = SendWaiting(self.Socket, descriptor, data, flags, name, ancillary, ref errno);
            }
            return RubyOps.MakeArray2(errno, (int)sent);
        }

        /// <summary>
        /// recvmsg(2). Answers [errno, data, sender-sockaddr, flags, controls]; `data` is nil when
        /// the call failed, and `sender-sockaddr` is nil when the kernel reported no source address
        /// at all, which is what a connected stream socket does.
        /// </summary>
        [RubyMethod("__ir_raw_recvmsg")]
        public static RubyArray/*!*/ ReceiveMessage(RubyContext/*!*/ context, RubyBasicSocket/*!*/ self,
            [DefaultProtocol]int maxMessageLength, [DefaultProtocol]int flags, [DefaultProtocol]int maxControlLength,
            bool nonBlocking) {

            Socket socket = self.Socket;
            int descriptor = (int)socket.Handle;

            int errno = 0;
            ReceivedMessage received;
            if (nonBlocking) {
                received = WithoutBlocking(socket,
                    () => PosixMessages.Receive(descriptor, maxMessageLength, flags, maxControlLength, out errno));
            } else {
                received = Blocking(socket, SelectMode.SelectRead,
                    () => PosixMessages.Receive(descriptor, maxMessageLength, flags, maxControlLength, out errno));
            }

            RubyArray result = new RubyArray(5);
            result.Add(errno);
            if (received == null) {
                result.Add(null);
                result.Add(null);
                result.Add(0);
                result.Add(new RubyArray(0));
                return result;
            }

            result.Add(MutableString.CreateBinary(received.Data));
            result.Add(received.SenderAddress != null ? MutableString.CreateBinary(received.SenderAddress) : null);
            result.Add(received.Flags);

            RubyArray ancillary = new RubyArray(received.Controls.Count);
            foreach (ControlMessage control in received.Controls) {
                ancillary.Add(RubyOps.MakeArray3(control.Level, control.Type, MutableString.CreateBinary(control.Data)));
            }
            result.Add(ancillary);
            return result;
        }

        private static List<ControlMessage> ToControlMessages(RubyArray controls) {
            if (controls == null || controls.Count == 0) {
                return null;
            }
            var result = new List<ControlMessage>(controls.Count);
            foreach (object item in controls) {
                RubyArray triple = item as RubyArray;
                if (triple == null || triple.Count != 3) {
                    throw RubyExceptions.CreateArgumentError("ancillary data must be [level, type, data]");
                }
                if (!(triple[0] is int) || !(triple[1] is int) || !(triple[2] is MutableString)) {
                    throw RubyExceptions.CreateArgumentError("ancillary data must be [Integer, Integer, String]");
                }
                result.Add(new ControlMessage((int)triple[0], (int)triple[1], ((MutableString)triple[2]).ConvertToBytes()));
            }
            return result;
        }

        /// <summary>
        /// A blocking sendmsg(2) that a Ruby thread can still be killed out of.
        ///
        /// A thread parked inside the syscall is Running as far as the CLR is concerned, so
        /// Thread#kill and Thread#raise -- which arrive as an Interrupt -- can never reach it, and
        /// "10.times { sock.sendmsg(huge) }.should block_caller" hung the whole suite. The reading
        /// side already solves this by waiting in poll slices; the writing side needs the same, and
        /// additionally needs O_NONBLOCK so that the syscall itself returns rather than parking.
        ///
        /// O_NONBLOCK also turns a large write into a partial one, so what a blocking sendmsg would
        /// have sent in one call is reassembled by sending the rest -- the ancillary data rides on
        /// the first pass only, exactly as one sendmsg(2) would have carried it.
        /// </summary>
        private static long SendWaiting(Socket/*!*/ socket, int descriptor, byte[]/*!*/ data, int flags,
            byte[] address, List<ControlMessage> controls, ref int errno) {

            ThreadOps.RubyThreadInfo info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            bool wasBlocked = info.Blocked;
            bool blocking = socket.Blocking;
            info.Blocked = true;
            try {
                socket.Blocking = false;
                int offset = 0;
                while (true) {
                    long sent = PosixMessages.Send(descriptor, data, offset, data.Length - offset, flags,
                        address, offset == 0 ? controls : null, out errno);

                    if (sent >= 0) {
                        offset += (int)sent;
                        if (sent == 0 || offset >= data.Length) {
                            return offset;
                        }
                        continue;
                    }
                    if (errno != Posix.EWOULDBLOCK) {
                        return (offset != 0) ? offset : -1;
                    }
                    errno = 0;
                    while (!socket.Poll(PollSliceMicroseconds, SelectMode.SelectWrite)) {
                        RubyUtils.CheckAsyncException();
                    }
                    RubyUtils.CheckAsyncException();
                }
            } finally {
                socket.Blocking = blocking;
                info.Blocked = wasBlocked;
            }
        }

        /// <summary>
        /// Runs a raw syscall with O_NONBLOCK set, which is how the *_nonblock family gets EAGAIN
        /// out of the kernel instead of waiting. The poll-first wrapper is deliberately skipped:
        /// the whole point is to not wait for readiness.
        /// </summary>
        private static TResult WithoutBlocking<TResult>(Socket/*!*/ socket, Func<TResult>/*!*/ operation) {
            bool blocking = socket.Blocking;
            try {
                socket.Blocking = false;
                return operation();
            } finally {
                socket.Blocking = blocking;
            }
        }

        #endregion

        [RubyMethod("recv_nonblock")]
        public static MutableString/*!*/ ReceiveNonBlocking(ConversionStorage<int>/*!*/ fixnumCast, RubyBasicSocket/*!*/ self,
            [DefaultProtocol]int length, [DefaultParameterValue(null)]object flags) {

            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return Receive(fixnumCast, self, length, flags);
            } finally {
                // Reset the blocking
                self.Socket.Blocking = blocking;
            }
        }

#endregion

#region Internal Helpers

        internal static void CheckSecurity(RubyContext/*!*/ context, object self, string message) {
            if (context.CurrentSafeLevel >= 4 && context.IsObjectTainted(self)) {
                throw RubyExceptions.CreateSecurityError("Insecure: " + message);
            }
        }

        // TODO: handle other invalid addresses
        internal static IPHostEntry/*!*/ GetHostEntry(IPAddress/*!*/ address, bool doNotReverseLookup) {
            Assert.NotNull(address);
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Loopback)) {
                return MakeEntry(address, doNotReverseLookup);
            } else {
                return Dns.GetHostEntry(address);
            }
        }

        // TODO: handle other invalid addresses
        internal static IPHostEntry/*!*/ GetHostEntry(string/*!*/ hostNameOrAddress, bool doNotReverseLookup) {
            Assert.NotNull(hostNameOrAddress);
            if (hostNameOrAddress == IPAddress.Any.ToString()) {
                return MakeEntry(IPAddress.Any, doNotReverseLookup);
            } else if (hostNameOrAddress == IPAddress.Loopback.ToString()) {
                return MakeEntry(IPAddress.Loopback, doNotReverseLookup);
            } else {
                return Dns.GetHostEntry(hostNameOrAddress);
            }
        }

        internal static IPAddress/*!*/ GetHostAddress(string/*!*/ hostNameOrAddress) {
            IPAddress address;
            if (IPAddress.TryParse(hostNameOrAddress, out address)) {
                return address;
            }
            // TODO: map exceptions

            var addresses = Dns.GetHostAddresses(hostNameOrAddress);

            // prefer V4 address:
            foreach (var hostAddress in addresses) {
                if (hostAddress.AddressFamily == AddressFamily.InterNetwork) {
                    return hostAddress;
                }
            }

            return addresses[0];
        }

        /// <summary>
        /// Resolve preferring a particular address family -- needed when a socket already
        /// exists and its local address has to match it.
        /// </summary>
        internal static IPAddress/*!*/ GetHostAddress(string/*!*/ hostNameOrAddress, AddressFamily family) {
            IPAddress address;
            if (IPAddress.TryParse(hostNameOrAddress, out address)) {
                return address;
            }
            var addresses = Dns.GetHostAddresses(hostNameOrAddress);
            foreach (var hostAddress in addresses) {
                if (hostAddress.AddressFamily == family) {
                    return hostAddress;
                }
            }
            return addresses[0];
        }

        internal static IPHostEntry/*!*/ MakeEntry(IPAddress/*!*/ address, bool doNotReverseLookup) {
            var str = IPAddressToHostName(address, doNotReverseLookup);
            return new IPHostEntry() {
                AddressList = new[] { address },
                Aliases = new[] { str },
                HostName = str
            };
        }

        internal static RubyArray/*!*/ GetHostByName(RubyContext/*!*/ context, string/*!*/ hostNameOrAddress, bool packIpAddresses) {
            return CreateHostEntryArray(context, GetHostEntry(hostNameOrAddress, DoNotReverseLookup(context).Value), packIpAddresses);
        }

        internal RubyArray/*!*/ GetAddressArray(EndPoint/*!*/ endPoint) {
            return GetAddressArray(Context, endPoint, _doNotReverseLookup);
        }

        internal static RubyArray/*!*/ GetAddressArray(RubyContext/*!*/ context, EndPoint/*!*/ endPoint) {
            return GetAddressArray(context, endPoint, DoNotReverseLookup(context).Value);
        }

        internal static RubyArray/*!*/ GetAddressArray(RubyContext/*!*/ context, EndPoint/*!*/ endPoint, bool doNotReverseLookup) {
            if (endPoint.AddressFamily == AddressFamily.Unix) {
                // A sockaddr_un has no port and no host, so the tuple is only two elements long.
                return UNIXSocket.AddressArray(context, endPoint);
            }

            RubyArray result = new RubyArray(4);

            IPEndPoint ep = (IPEndPoint)endPoint;
            result.Add(MutableString.CreateAscii(AddressFamilyToString(ep.AddressFamily)));
            result.Add(ep.Port);
            result.Add(HostNameToMutableString(context, IPAddressToHostName(ep.Address, doNotReverseLookup)));
            result.Add(MutableString.CreateAscii(ep.Address.ToString()));
            return result;
        }

        internal static MutableString/*!*/ HostNameToMutableString(RubyContext/*!*/ context, string/*!*/ str) {
            if (str.IsAscii()) {
                return MutableString.CreateAscii(str);
            } else {
                return MutableString.Create(str, context.GetPathEncoding());
            }
        }

        internal static string/*!*/ IPAddressToHostName(IPAddress/*!*/ address, bool doNotReverseLookup) {
            if (address.Equals(IPAddress.Any) || doNotReverseLookup) {
                return address.ToString();
            } else {
                return Dns.GetHostEntry(address).HostName;
            }
        }

        private static string AddressFamilyToString(AddressFamily af) {
            // for the most part we can just use the upper-cased AddressFamily name
            // of the enum value, but for some types we need to explicitly map the
            // correct names
            switch (af) {
                case AddressFamily.InterNetwork: return "AF_INET";
                case AddressFamily.DataLink: return "AF_DLI";
                case AddressFamily.HyperChannel: return "AF_HYLINK";
                case AddressFamily.Banyan: return "AF_BAN";
                case AddressFamily.InterNetworkV6: return "AF_INET6";
                case AddressFamily.Ieee12844: return "AF_12844";
                case AddressFamily.NetworkDesigners: return "AF_NETDES";
                default:
                    string name = Enum.GetName(typeof(AddressFamily), af);
                    return (name != null) ?
                        "AF_" + name.ToUpperInvariant() :
                        "unknown:" + ((int)af).ToString(CultureInfo.InvariantCulture);
            }
        }

        internal static SocketFlags ConvertToSocketFlag(ConversionStorage<int>/*!*/ conversionStorage, object flags) {
            if (flags == null) {
                return SocketFlags.None;
            }
            return (SocketFlags)Protocols.CastToFixnum(conversionStorage, flags);
        }

        internal static AddressFamily ConvertToAddressFamily(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, 
            object family) {

            // Default is AF_INET
            if (family == null) {
                return AddressFamily.InterNetwork;
            }
            // If it is a Fixnum then assume it is just the constant value
            if (family is int) {
                return (AddressFamily)(int)family;
            }
            // Convert to a string (using to_str) and then look up the value
            MutableString strFamily = Protocols.CastToString(stringCast, family);
            foreach (AddressFamilyName name in FamilyNames) {
                if (name.Name.Equals(strFamily)) {
                    return name.Family;
                }
            }
            // Convert to a Fixnum (using to_i) and hope it is a valid AddressFamily constant
            return (AddressFamily)Protocols.CastToFixnum(fixnumCast, strFamily);
        }

        internal static MutableString ToAddressFamilyString(AddressFamily family) {
            foreach (AddressFamilyName name in FamilyNames) {
                if (name.Family == family) {
                    return name.Name;
                }
            }
            throw new SocketException((int)SocketError.AddressFamilyNotSupported);
        }


        internal static string/*!*/ ConvertToHostString(uint address) {
            // Ruby uses Little Endian whereas .NET uses Big Endian IP values
            byte[] bytes = new byte[4];
            for (int i = bytes.Length - 1; i >= 0; --i) {
                bytes[i] = (byte)(address & 0xff);
                address >>= 8;
            }
            return new IPAddress(bytes).ToString();
        }

        internal static string/*!*/ ConvertToHostString(BigInteger/*!*/ address) {
            Assert.NotNull(address);
            ulong u;
            if (address.AsUInt64(out u)) {
                if (u <= UInt32.MaxValue) {
                    // IPv4:
                    return ConvertToHostString((uint)u);
                } else {
                    // IPv6:
                    byte[] bytes = new byte[8];
                    for (int i = bytes.Length - 1; i >= 0; --i) {
                        bytes[i] = (byte)(u & 0xff);
                        u >>= 8;
                    }
                    return new IPAddress(bytes).ToString();
                }
            } else {
                throw RubyExceptions.CreateRangeError("bignum too big to convert into `quad long'");
            }
        }

        internal static string/*!*/ ConvertToHostString(MutableString hostName) {
            if (hostName == null) {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            if (hostName.IsEmpty) {
                return IPAddress.Any.ToString();
            } else if (hostName.Equals(BROADCAST_STRING)) {
                return IPAddress.Broadcast.ToString();
            }
            return hostName.ConvertToString();
        }

        internal static string/*!*/ ConvertToHostString(ConversionStorage<MutableString>/*!*/ stringCast, object hostName) {
            if (hostName is int) {
                return ConvertToHostString((int)hostName);
            } else if (hostName is BigInteger bignum) {
                return ConvertToHostString(bignum);
            } else if (hostName != null) {
                return ConvertToHostString(Protocols.CastToString(stringCast, hostName));
            } else {
                return ConvertToHostString((MutableString)null);
            }
        }
        
        /// <summary>
        /// Converts an Integer to a Fixnum.
        /// Don't call any conversion methods--just handles Fixnum and Bignum
        /// </summary>
        /// <param name="value"></param>
        /// <returns>true if value is an Integer, false otherwise</returns>
        /// <exception cref="ArgumentOutOfRangeException">Throws a RangeError if value is a
        /// BigInteger but can't be converted to a Fixnum</exception>
        internal static bool IntegerAsFixnum(object value, out int result) {
            if (value is int) {
                result = (int)value;
                return true;
            }

            if (value is BigInteger bignum) {
                if (!bignum.AsInt32(out result)) {
                    throw RubyExceptions.CreateRangeError("bignum too big to convert into `long'");
                }
                return true;
            }

            result = 0;
            return false;
        }

        internal static int ConvertToPortNum(ConversionStorage<MutableString>/*!*/ stringCast, ConversionStorage<int>/*!*/ fixnumCast, object port) {
            // conversion protocol: if it's a Fixnum, return it
            // otherwise, convert to string & then convert the result to a Fixnum
            if (port is int) {
                return (int)port;
            }

            if (port == null) {
                return 0;
            }

            MutableString serviceName = Protocols.CastToString(stringCast, port);
            ServiceName service = SearchForService(serviceName);
            if (service != null) {
                return service.Port;
            }

            int i = 0;
            if (Int32.TryParse(serviceName.ToString(), out i)) {
                return i;
            }

            throw SocketErrorOps.Create(MutableString.FormatMessage("Invalid port number or service name: `{0}'.", serviceName));
        }

        internal static ServiceName SearchForService(int port) {
            foreach (ServiceName name in ServiceNames) {
                if (name.Port == port) {
                    return name;
                }
            }
            return null;
        }

        internal static ServiceName SearchForService(MutableString/*!*/ serviceName) {
            foreach (ServiceName name in ServiceNames) {
                if (name.Name.Equals(serviceName)) {
                    return name;
                }
            }
            return null;
        }

        internal static ServiceName SearchForService(MutableString/*!*/ serviceName, MutableString/*!*/ protocol) {
            foreach (ServiceName name in ServiceNames) {
                if (name.Name.Equals(serviceName) && name.Protocol.Equals(protocol)) {
                    return name;
                }
            }
            return null;
        }

        internal static RubyArray/*!*/ CreateHostEntryArray(RubyContext/*!*/ context, IPHostEntry/*!*/ hostEntry, bool packIpAddresses) {
            RubyArray result = new RubyArray(4);
            
            // host name:
            result.Add(HostNameToMutableString(context, hostEntry.HostName));

            // aliases:
            RubyArray aliases = new RubyArray(hostEntry.Aliases.Length);
            foreach (string alias in hostEntry.Aliases) {
                aliases.Add(HostNameToMutableString(context, alias));
            }
            result.Add(aliases);

            // address (the first IPv4):
            foreach (IPAddress address in hostEntry.AddressList) {
                if (address.AddressFamily == AddressFamily.InterNetwork) {
                    result.Add((int)address.AddressFamily);
                    if (packIpAddresses) {
                        byte[] bytes = address.GetAddressBytes();
                        MutableString str = MutableString.CreateBinary();
                        str.Append(bytes, 0, bytes.Length);
                        result.Add(str);
                    } else {
                        result.Add(MutableString.CreateAscii(address.ToString()));
                    }
                    break;
                }
                
            }
            return result;
        }


        private struct AddressFamilyName {
            public readonly MutableString/*!*/ Name;
            public readonly AddressFamily Family;

            public AddressFamilyName(string/*!*/ name, AddressFamily family) {
                Name = MutableString.CreateAscii(name);
                Family = family;
            }
        }

        private static AddressFamilyName[] FamilyNames = new[] {
            new AddressFamilyName("AF_INET", AddressFamily.InterNetwork),
            new AddressFamilyName("AF_UNIX", AddressFamily.Unix),
            //new AddressFamilyName("AF_AX25", AddressFamily.Ax),
            new AddressFamilyName("AF_IPX", AddressFamily.Ipx),
            new AddressFamilyName("AF_APPLETALK", AddressFamily.AppleTalk),
            new AddressFamilyName("AF_UNSPEC", AddressFamily.Unspecified),
            new AddressFamilyName("AF_INET6", AddressFamily.InterNetworkV6),
            //new AddressFamilyName("AF_LOCAL", AddressFamily.Local),
            new AddressFamilyName("AF_IMPLINK", AddressFamily.ImpLink),
            new AddressFamilyName("AF_PUP", AddressFamily.Pup),
            new AddressFamilyName("AF_CHAOS", AddressFamily.Chaos),
            new AddressFamilyName("AF_NS", AddressFamily.NS),
            new AddressFamilyName("AF_ISO", AddressFamily.Iso),
            new AddressFamilyName("AF_OSI", AddressFamily.Osi),
            new AddressFamilyName("AF_ECMA", AddressFamily.Ecma),
            new AddressFamilyName("AF_DATAKIT", AddressFamily.DataKit),
            new AddressFamilyName("AF_CCITT", AddressFamily.Ccitt),
            new AddressFamilyName("AF_SNA", AddressFamily.Sna),
            new AddressFamilyName("AF_DEC", AddressFamily.DecNet),
            new AddressFamilyName("AF_DLI", AddressFamily.DataLink),
            new AddressFamilyName("AF_LAT", AddressFamily.Lat),
            new AddressFamilyName("AF_HYLINK", AddressFamily.HyperChannel),
            //new AddressFamilyName("AF_ROUTE", AddressFamily.Route),
            //new AddressFamilyName("AF_LINK", AddressFamily.Link),
            //new AddressFamilyName("AF_COIP", AddressFamily.Coip),
            //new AddressFamilyName("AF_CNT", AddressFamily.Cnt),
            //new AddressFamilyName("AF_SIP", AddressFamily.Sip),
            //new AddressFamilyName("AF_NDRV", AddressFamily.Nrdv),
            //new AddressFamilyName("AF_ISDN", AddressFamily.Isdn),
            //new AddressFamilyName("AF_NATM", AddressFamily.NATM),
            //new AddressFamilyName("AF_SYSTEM", AddressFamily.System),
            new AddressFamilyName("AF_NETBIOS", AddressFamily.NetBios),
            //new AddressFamilyName("AF_PPP", AddressFamily.Ppp),
            new AddressFamilyName("AF_ATM", AddressFamily.Atm),
            //new AddressFamilyName("AF_NETGRAPH", AddressFamily.Netgraph),
            new AddressFamilyName("AF_MAX", AddressFamily.Max),
            //new AddressFamilyName("AF_E164", AddressFamily.E164),
        };

        internal sealed class ServiceName {
            public readonly int Port;
            public readonly MutableString Protocol;
            public readonly MutableString Name;

            public ServiceName(int port, string/*!*/ protocol, string/*!*/ name) {
                Port = port;
                Protocol = MutableString.CreateAscii(protocol);
                Name = MutableString.CreateAscii(name);
            }
        }

        private static ServiceName[] ServiceNames = new[] {
            new ServiceName(7, "tcp", "echo"),
            new ServiceName(7, "udp", "echo"),
            new ServiceName(9, "tcp", "discard"),
            new ServiceName(9, "udp", "discard"),
            new ServiceName(11, "tcp", "systat"),
            new ServiceName(11, "udp", "systat"),
            new ServiceName(13, "tcp", "daytime"),
            new ServiceName(13, "udp", "daytime"),
            new ServiceName(15, "tcp", "netstat"),
            new ServiceName(17, "tcp", "qotd"),
            new ServiceName(17, "udp", "qotd"),
            new ServiceName(19, "tcp", "chargen"),
            new ServiceName(19, "udp", "chargen"),
            new ServiceName(20, "tcp", "ftp-data"),
            new ServiceName(21, "tcp", "ftp"),
            new ServiceName(23, "tcp", "telnet"),
            new ServiceName(25, "tcp", "smtp"),
            new ServiceName(37, "tcp", "time"),
            new ServiceName(37, "udp", "time"),
            new ServiceName(39, "udp", "rlp"),
            new ServiceName(42, "tcp", "name"),
            new ServiceName(42, "udp", "name"),
            new ServiceName(43, "tcp", "whois"),
            new ServiceName(53, "tcp", "domain"),
            new ServiceName(53, "udp", "domain"),
            new ServiceName(53, "tcp", "nameserver"),
            new ServiceName(53, "udp", "nameserver"),
            new ServiceName(57, "tcp", "mtp"),
            new ServiceName(67, "udp", "bootp"),
            new ServiceName(69, "udp", "tftp"),
            new ServiceName(77, "tcp", "rje"),
            new ServiceName(79, "tcp", "finger"),
            new ServiceName(80, "tcp", "http"),
            new ServiceName(87, "tcp", "link"),
            new ServiceName(95, "tcp", "supdup"),
            new ServiceName(101, "tcp", "hostnames"),
            new ServiceName(102, "tcp", "iso-tsap"),
            new ServiceName(103, "tcp", "dictionary"),
            new ServiceName(103, "tcp", "x400"),
            new ServiceName(104, "tcp", "x400-snd"),
            new ServiceName(105, "tcp", "csnet-ns"),
            new ServiceName(109, "tcp", "pop"),
            new ServiceName(109, "tcp", "pop2"),
            new ServiceName(110, "tcp", "pop3"),
            new ServiceName(111, "tcp", "portmap"),
            new ServiceName(111, "udp", "portmap"),
            new ServiceName(111, "tcp", "sunrpc"),
            new ServiceName(111, "udp", "sunrpc"),
            new ServiceName(113, "tcp", "auth"),
            new ServiceName(115, "tcp", "sftp"),
            new ServiceName(117, "tcp", "path"),
            new ServiceName(117, "tcp", "uucp-path"),
            new ServiceName(119, "tcp", "nntp"),
            new ServiceName(123, "udp", "ntp"),
            new ServiceName(137, "udp", "nbname"),
            new ServiceName(138, "udp", "nbdatagram"),
            new ServiceName(139, "tcp", "nbsession"),
            new ServiceName(144, "tcp", "NeWS"),
            new ServiceName(153, "tcp", "sgmp"),
            new ServiceName(158, "tcp", "tcprepo"),
            new ServiceName(161, "tcp", "snmp"),
            new ServiceName(162, "tcp", "snmp-trap"),
            new ServiceName(170, "tcp", "print-srv"),
            new ServiceName(175, "tcp", "vmnet"),
            new ServiceName(315, "udp", "load"),
            new ServiceName(400, "tcp", "vmnet0"),
            new ServiceName(500, "udp", "sytek"),
            new ServiceName(512, "udp", "biff"),
            new ServiceName(512, "tcp", "exec"),
            new ServiceName(513, "tcp", "login"),
            new ServiceName(513, "udp", "who"),
            new ServiceName(514, "tcp", "shell"),
            new ServiceName(514, "udp", "syslog"),
            new ServiceName(515, "tcp", "printer"),
            new ServiceName(517, "udp", "talk"),
            new ServiceName(518, "udp", "ntalk"),
            new ServiceName(520, "tcp", "efs"),
            new ServiceName(520, "udp", "route"),
            new ServiceName(525, "udp", "timed"),
            new ServiceName(526, "tcp", "tempo"),
            new ServiceName(530, "tcp", "courier"),
            new ServiceName(531, "tcp", "conference"),
            new ServiceName(531, "udp", "rvd-control"),
            new ServiceName(532, "tcp", "netnews"),
            new ServiceName(533, "udp", "netwall"),
            new ServiceName(540, "tcp", "uucp"),
            new ServiceName(543, "tcp", "klogin"),
            new ServiceName(544, "tcp", "kshell"),
            new ServiceName(550, "udp", "new-rwho"),
            new ServiceName(556, "tcp", "remotefs"),
            new ServiceName(560, "udp", "rmonitor"),
            new ServiceName(561, "udp", "monitor"),
            new ServiceName(600, "tcp", "garcon"),
            new ServiceName(601, "tcp", "maitrd"),
            new ServiceName(602, "tcp", "busboy"),
            new ServiceName(700, "udp", "acctmaster"),
            new ServiceName(701, "udp", "acctslave"),
            new ServiceName(702, "udp", "acct"),
            new ServiceName(703, "udp", "acctlogin"),
            new ServiceName(704, "udp", "acctprinter"),
            new ServiceName(704, "udp", "elcsd"),
            new ServiceName(705, "udp", "acctinfo"),
            new ServiceName(706, "udp", "acctslave2"),
            new ServiceName(707, "udp", "acctdisk"),
            new ServiceName(750, "tcp", "kerberos"),
            new ServiceName(750, "udp", "kerberos"),
            new ServiceName(751, "tcp", "kerberos_master"),
            new ServiceName(751, "udp", "kerberos_master"),
            new ServiceName(752, "udp", "passwd_server"),
            new ServiceName(753, "udp", "userreg_server"),
            new ServiceName(754, "tcp", "krb_prop"),
            new ServiceName(888, "tcp", "erlogin"),
            new ServiceName(1109, "tcp", "kpop"),
            new ServiceName(1167, "udp", "phone"),
            new ServiceName(1524, "tcp", "ingreslock"),
            new ServiceName(1666, "udp", "maze"),
            new ServiceName(2049, "udp", "nfs"),
            new ServiceName(2053, "tcp", "knetd"),
            new ServiceName(2105, "tcp", "eklogin"),
            new ServiceName(5555, "tcp", "rmt"),
            new ServiceName(5556, "tcp", "mtb"),
            new ServiceName(9535, "tcp", "man"),
            new ServiceName(9536, "tcp", "w"),
            new ServiceName(9537, "tcp", "mantst"),
            new ServiceName(10000, "tcp", "bnews"),
            new ServiceName(10000, "udp", "rscs0"),
            new ServiceName(10001, "tcp", "queue"),
            new ServiceName(10001, "udp", "rscs1"),
            new ServiceName(10002, "tcp", "poker"),
            new ServiceName(10002, "udp", "rscs2"),
            new ServiceName(10003, "tcp", "gateway"),
            new ServiceName(10003, "udp", "rscs3"),
            new ServiceName(10004, "tcp", "remp"),
            new ServiceName(10004, "udp", "rscs4"),
            new ServiceName(10005, "udp", "rscs5"),
            new ServiceName(10006, "udp", "rscs6"),
            new ServiceName(10007, "udp", "rscs7"),
            new ServiceName(10008, "udp", "rscs8"),
            new ServiceName(10009, "udp", "rscs9"),
            new ServiceName(10010, "udp", "rscsa"),
            new ServiceName(10011, "udp", "rscsb"),
            new ServiceName(10012, "tcp", "qmaster"),
            new ServiceName(10012, "udp", "qmaster")
        };

#endregion
    }
}
#endif
