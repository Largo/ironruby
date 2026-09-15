/* ****************************************************************************
 *
 * AF_UNIX sockets.
 *
 * IronRuby never had UNIXSocket or UNIXServer at all -- `require "socket"` left
 * both constants undefined, which is what the whole of spec/library/socket's
 * unixsocket and unixserver trees tripped over before anything else could be
 * measured.  .NET has had UnixDomainSocketEndPoint since Core 2.1, so the
 * connect/bind/accept half of this is ordinary System.Net.Sockets work; the two
 * pieces it has no answer for -- socketpair(2) and descriptor passing over
 * SCM_RIGHTS -- come from libc, in the same style as Src/Libraries/Builtins/Posix.cs.
 *
 * ***************************************************************************/

#if FEATURE_SYNC_SOCKETS

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Scripting.Runtime;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Sockets {

    [RubyClass("UNIXSocket", BuildConfig = "FEATURE_SYNC_SOCKETS")]
    public class UNIXSocket : RubyBasicSocket {

        /// <summary>
        /// Creates an uninitialized socket.
        /// </summary>
        public UNIXSocket(RubyContext/*!*/ context)
            : base(context) {
        }

        public UNIXSocket(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        #region Construction

        [RubyConstructor]
        public static UNIXSocket/*!*/ CreateUNIXSocket(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            return new UNIXSocket(self.Context, Connect(self.Context, path));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static UNIXSocket/*!*/ Reinitialize(UNIXSocket/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            self.Socket = Connect(self.Context, path);
            return self;
        }

        private static Socket/*!*/ Connect(RubyContext/*!*/ context, MutableString/*!*/ path) {
            string pathStr = context.DecodePath(path);
            Socket socket = NewUnixSocket(SocketType.Stream);
            try {
                socket.Connect(ToEndPoint(pathStr));
            } catch (Exception e) {
                socket.Close();
                throw ToConnectException(e, pathStr);
            }
            return socket;
        }

        /// <summary>
        /// .NET collapses the connect(2) errno for a path that does not exist into
        /// AddressNotAvailable, which would surface as a bare SocketError.  EADDRNOTAVAIL is not
        /// something an AF_UNIX connect can actually report, so the only thing it can mean here is
        /// the ENOENT that CRuby raises.
        /// </summary>
        internal static Exception/*!*/ ToConnectException(Exception/*!*/ e, string/*!*/ path) {
            SocketException se = e as SocketException;
            if (se != null && se.SocketErrorCode == SocketError.AddressNotAvailable) {
                return RubyExceptions.CreateENOENT("{0}", path);
            }
            return SocketErrorOps.ToRubyException(e);
        }

        internal static Socket/*!*/ NewUnixSocket(SocketType type) {
            return new Socket(AddressFamily.Unix, type, ProtocolType.Unspecified);
        }

        /// <summary>
        /// UnixDomainSocketEndPoint rejects an empty path outright, so a placeholder is needed
        /// wherever an endpoint has to exist before it is filled in (ReceiveFrom's `ref` argument).
        /// </summary>
        internal static EndPoint/*!*/ ToEndPoint(string/*!*/ path) {
            return new UnixDomainSocketEndPoint(path.Length == 0 ? "/" : path);
        }

        #endregion

        #region Addresses

        /// <summary>
        /// The path an endpoint names.  UnixDomainSocketEndPoint.ToString() answers "" for an
        /// unbound or anonymous socket -- exactly what CRuby's UNIXSocket#path reports for the
        /// client side of a connection and for both ends of a socketpair.
        /// </summary>
        internal static string/*!*/ PathOf(EndPoint endPoint) {
            return endPoint == null ? "" : endPoint.ToString();
        }

        internal static MutableString/*!*/ EncodePath(RubyContext/*!*/ context, string/*!*/ path) {
            return path.Length == 0 ? MutableString.CreateEmpty() : context.EncodePath(path);
        }

        internal static RubyArray/*!*/ AddressArray(RubyContext/*!*/ context, EndPoint endPoint) {
            RubyArray result = new RubyArray(2);
            result.Add(MutableString.CreateAscii("AF_UNIX"));
            result.Add(EncodePath(context, PathOf(endPoint)));
            return result;
        }

        [RubyMethod("path")]
        public static MutableString/*!*/ GetPath(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            return EncodePath(context, PathOf(self.Socket.LocalEndPoint));
        }

        [RubyMethod("addr")]
        public static RubyArray/*!*/ GetLocalAddress(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            return AddressArray(context, self.Socket.LocalEndPoint);
        }

        [RubyMethod("peeraddr")]
        public static RubyArray/*!*/ GetPeerAddress(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            EndPoint remote;
            try {
                remote = self.Socket.RemoteEndPoint;
            } catch (SocketException e) {
                throw SocketErrorOps.ToRubyException(e);
            }
            if (remote == null) {
                throw new Errno.NotConnectedError();
            }
            return AddressArray(context, remote);
        }

        /// <summary>
        /// recvfrom(2).  On a connected stream socket the kernel fills in no source address at all
        /// -- .NET leaves the `ref` endpoint untouched -- and CRuby reports the peer, so take it
        /// from getpeername(2).  A datagram socket does get a real source address.
        /// </summary>
        [RubyMethod("recvfrom")]
        public static RubyArray/*!*/ ReceiveFrom(ConversionStorage<int>/*!*/ fixnumCast, UNIXSocket/*!*/ self, int length) {
            return ReceiveFrom(fixnumCast, self, length, null);
        }

        [RubyMethod("recvfrom")]
        public static RubyArray/*!*/ ReceiveFrom(ConversionStorage<int>/*!*/ fixnumCast, UNIXSocket/*!*/ self,
            int length, object/*Numeric*/ flags) {

            SocketFlags socketFlags = ConvertToSocketFlag(fixnumCast, flags);
            byte[] buffer = new byte[length];
            Socket socket = self.Socket;

            EndPoint from;
            int received;
            if (socket.SocketType == SocketType.Stream) {
                received = Blocking(socket, SelectMode.SelectRead, () => socket.Receive(buffer, socketFlags));
                from = socket.Connected ? socket.RemoteEndPoint : null;
            } else {
                EndPoint sender = ToEndPoint("");
                received = Blocking(socket, SelectMode.SelectRead, () => socket.ReceiveFrom(buffer, socketFlags, ref sender));
                from = sender;
            }

            MutableString str = MutableString.CreateBinary();
            str.Append(buffer, 0, received);
            str.IsTainted = true;
            return RubyOps.MakeArray2(str, AddressArray(self.Context, from));
        }

        #endregion

        #region send_io, recv_io

        /// <summary>
        /// IO#fileno is an index into RubyContext's own descriptor table, not an operating system
        /// descriptor, so neither end of a descriptor pass can go through it: what travels over
        /// SCM_RIGHTS has to be the descriptor the kernel knows, and what comes back has to be
        /// given a table entry of its own before Ruby can name it.
        /// </summary>
        [RubyMethod("__ir_raw_send_io")]
        public static void SendDescriptor(UNIXSocket/*!*/ self, [NotNull]RubyIO/*!*/ io) {
            int descriptor = io.KernelDescriptor;
            if (descriptor < 0) {
                throw RubyExceptions.CreateIOError("cannot send an IO that has no file descriptor");
            }
            io.Flush();
            UnixDescriptorPassing.Send((int)self.Socket.Handle, descriptor);
        }

        [RubyMethod("__ir_raw_recv_io")]
        public static int ReceiveDescriptor(RubyContext/*!*/ context, UNIXSocket/*!*/ self) {
            Socket socket = self.Socket;
            int descriptor = Blocking(socket, SelectMode.SelectRead, () => UnixDescriptorPassing.Receive((int)socket.Handle));

            IOMode mode;
            if (!RubyIO.TryGetDescriptorMode(descriptor, out mode)) {
                mode = IOMode.ReadWrite;
            }
            IOMode access = mode & IOMode.ReadWriteMask;
            return context.AllocateFileDescriptor(
                new DescriptorStream(descriptor, access != IOMode.WriteOnly, access != IOMode.ReadOnly, true));
        }

        #endregion
    }

    [RubyClass("UNIXServer", BuildConfig = "FEATURE_SYNC_SOCKETS")]
    public class UNIXServer : UNIXSocket {

        public UNIXServer(RubyContext/*!*/ context)
            : base(context) {
        }

        public UNIXServer(RubyContext/*!*/ context, Socket/*!*/ socket)
            : base(context, socket) {
        }

        [RubyConstructor]
        public static UNIXServer/*!*/ CreateUNIXServer(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            return new UNIXServer(self.Context, Bind(self.Context, path));
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static UNIXServer/*!*/ Reinitialize(UNIXServer/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path) {
            self.Socket = Bind(self.Context, path);
            return self;
        }

        private static Socket/*!*/ Bind(RubyContext/*!*/ context, MutableString/*!*/ path) {
            string pathStr = context.DecodePath(path);
            Socket socket = NewUnixSocket(SocketType.Stream);
            try {
                socket.Bind(ToEndPoint(pathStr));
                socket.Listen(128);
            } catch (Exception e) {
                socket.Close();
                throw SocketErrorOps.ToRubyException(e);
            }
            return socket;
        }

        private Socket/*!*/ Accept() {
            return BlockingAccept(Socket, () => Socket.Accept());
        }

        [RubyMethod("accept")]
        public static UNIXSocket/*!*/ Accept(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            return new UNIXSocket(context, self.Accept());
        }

        [RubyMethod("accept_nonblock")]
        public static UNIXSocket/*!*/ AcceptNonBlocking(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            bool blocking = self.Socket.Blocking;
            try {
                self.Socket.Blocking = false;
                return Accept(context, self);
            } finally {
                self.Socket.Blocking = blocking;
            }
        }

        [RubyMethod("sysaccept")]
        public static int SysAccept(RubyContext/*!*/ context, UNIXServer/*!*/ self) {
            return Accept(context, self).GetFileDescriptor();
        }

        [RubyMethod("listen")]
        public static void Listen(UNIXServer/*!*/ self, [DefaultProtocol]int backlog) {
            self.Socket.Listen(backlog);
        }
    }

    /// <summary>
    /// The two AF_UNIX primitives with no managed equivalent.  Both are Linux/glibc shaped; the
    /// struct layouts below are the x86-64 and aarch64 ones, which are identical.
    /// </summary>
    internal static class UnixDescriptorPassing {

        [DllImport("libc", EntryPoint = "socketpair", SetLastError = true)]
        internal static extern int socketpair(int domain, int type, int protocol, int[] sv);

        [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
        private static extern IntPtr sys_sendmsg(int fd, ref MsgHdr message, int flags);

        [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
        private static extern IntPtr sys_recvmsg(int fd, ref MsgHdr message, int flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct IOVec {
            public IntPtr Base;
            public IntPtr Length;
        }

        // struct msghdr. The two padding fields are explicit rather than implied because the
        // marshaller lays the struct out exactly as declared.
        [StructLayout(LayoutKind.Sequential)]
        private struct MsgHdr {
            public IntPtr Name;
            public int NameLength;
            public int NamePadding;
            public IntPtr IOV;
            public IntPtr IOVLength;
            public IntPtr Control;
            public IntPtr ControlLength;
            public int Flags;
            public int FlagsPadding;
        }

        private const int SOL_SOCKET = 1;
        private const int SCM_RIGHTS = 1;

        // struct cmsghdr is { size_t cmsg_len; int cmsg_level; int cmsg_type; }, so the payload
        // starts at 16 and CMSG_SPACE(sizeof(int)) rounds 20 up to the next multiple of 8.
        private const int CmsgHeaderSize = 16;
        private const int CmsgDataOffset = CmsgHeaderSize;
        private const int CmsgLength = CmsgHeaderSize + 4;
        private const int CmsgSpace = 24;

        internal static void Send(int socketDescriptor, int descriptor) {
            Require();
            IntPtr payload = Marshal.AllocHGlobal(1);
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<IOVec>());
            IntPtr control = Marshal.AllocHGlobal(CmsgSpace);
            try {
                // sendmsg(2) on a stream socket refuses to carry ancillary data on its own, so the
                // descriptor rides along with one throwaway byte.
                Marshal.WriteByte(payload, 0, 0);
                Marshal.StructureToPtr(new IOVec { Base = payload, Length = (IntPtr)1 }, iov, false);

                WriteCmsgHeader(control, CmsgLength);
                Marshal.WriteInt32(control, CmsgDataOffset, descriptor);
                Marshal.WriteInt32(control, CmsgDataOffset + 4, 0);

                MsgHdr message = new MsgHdr {
                    Name = IntPtr.Zero,
                    NameLength = 0,
                    IOV = iov,
                    IOVLength = (IntPtr)1,
                    Control = control,
                    ControlLength = (IntPtr)CmsgSpace,
                };

                if ((long)sys_sendmsg(socketDescriptor, ref message, 0) < 0) {
                    throw Posix.Error(Marshal.GetLastWin32Error(), null);
                }
            } finally {
                Marshal.FreeHGlobal(control);
                Marshal.FreeHGlobal(iov);
                Marshal.FreeHGlobal(payload);
            }
        }

        internal static int Receive(int socketDescriptor) {
            Require();
            IntPtr payload = Marshal.AllocHGlobal(1);
            IntPtr iov = Marshal.AllocHGlobal(Marshal.SizeOf<IOVec>());
            IntPtr control = Marshal.AllocHGlobal(CmsgSpace);
            try {
                Marshal.StructureToPtr(new IOVec { Base = payload, Length = (IntPtr)1 }, iov, false);
                WriteCmsgHeader(control, 0);

                MsgHdr message = new MsgHdr {
                    Name = IntPtr.Zero,
                    NameLength = 0,
                    IOV = iov,
                    IOVLength = (IntPtr)1,
                    Control = control,
                    ControlLength = (IntPtr)CmsgSpace,
                };

                long received = (long)sys_recvmsg(socketDescriptor, ref message, 0);
                if (received < 0) {
                    throw Posix.Error(Marshal.GetLastWin32Error(), null);
                }

                long length = (long)Marshal.ReadIntPtr(control, 0);
                int level = Marshal.ReadInt32(control, 8);
                int type = Marshal.ReadInt32(control, 12);
                if ((long)message.ControlLength < CmsgLength || length < CmsgLength ||
                    level != SOL_SOCKET || type != SCM_RIGHTS) {
                    // The peer sent data but no descriptor. CRuby reports this as "file descriptor
                    // was not passed".
                    throw RubyExceptions.CreateIOError("file descriptor was not passed");
                }
                return Marshal.ReadInt32(control, CmsgDataOffset);
            } finally {
                Marshal.FreeHGlobal(control);
                Marshal.FreeHGlobal(iov);
                Marshal.FreeHGlobal(payload);
            }
        }

        private static void WriteCmsgHeader(IntPtr control, long length) {
            for (int i = 0; i < CmsgSpace; i++) {
                Marshal.WriteByte(control, i, 0);
            }
            Marshal.WriteIntPtr(control, 0, (IntPtr)length);
            Marshal.WriteInt32(control, 8, SOL_SOCKET);
            Marshal.WriteInt32(control, 12, SCM_RIGHTS);
        }

        private static void Require() {
            if (!Posix.IsAvailable) {
                throw new NotImplementedError("descriptor passing is not supported on this platform");
            }
        }
    }
}

#endif
