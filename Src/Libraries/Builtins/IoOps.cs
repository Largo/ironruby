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

#if FEATURE_CORE_DLR
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using Utils = IronRuby.Runtime.Utils;

    /// <summary>
    /// Implementation of IO builtin class. 
    /// </summary>
    [RubyClass("IO", Extends = typeof(RubyIO)), Includes(typeof(RubyFileOps.Constants), typeof(Enumerable))]
    [Includes(typeof(PrintOps), Copy = true)]
    public class RubyIOOps {
        internal static Stream/*!*/ GetDescriptorStream(RubyContext/*!*/ context, int descriptor) {
            Stream stream = context.GetStream(descriptor);
            if (stream == null) {
                // A descriptor this process was handed rather than opened - what a child of
                // Process.spawn gets through a redirection - is not in IronRuby's table, so
                // IO.new(fd) used to answer EBADF for exactly the descriptors a program has
                // been told to use.
                stream = RubyIO.TryAdoptDescriptor(descriptor);
                if (stream != null) {
                    context.SetOrAllocateDescriptor(descriptor, stream);
                }
            }
            if (stream == null) {
                throw RubyExceptions.CreateEBADF();
            }
            return stream;
        }

        #region Constants

        [RubyConstant]
        public const int SEEK_SET = RubyIO.SEEK_SET;

        [RubyConstant]
        public const int SEEK_CUR = RubyIO.SEEK_CUR;

        [RubyConstant]
        public const int SEEK_END = RubyIO.SEEK_END;

        [RubyModule("WaitReadable")]
        public static class WaitReadable {
        }

        [RubyModule("WaitWritable")]
        public static class WaitWritable {
        }

        public static Exception/*!*/ NonBlockingError(RubyContext/*!*/ context, Exception/*!*/ exception, bool isRead) {
            RubyModule waitReadable;
            if (context.TryGetModule(isRead ? typeof(WaitReadable) : typeof(WaitWritable), out waitReadable)) {
                ModuleOps.ExtendObject(waitReadable, exception);
            }
            return exception;
        }

        #endregion

        #region Ruby Constructors

        [RubyConstructor]
        public static RubyIO/*!*/ CreateFile(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self,
            object descriptor,
            [Optional]object optionsOrMode,
            [Optional]object options) {

            return Reinitialize(toInt, toHash, toStr, new RubyIO(self.Context), descriptor, optionsOrMode, options);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyIO/*!*/ Reinitialize(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyIO/*!*/ self,
            object descriptor,
            [Optional]object optionsOrMode,
            [Optional]object optionsArgument) {

            var context = self.Context;

            // The options are a Hash or nothing at all. MRI takes an explicit nil as a third
            // argument that is not a Hash - "wrong number of arguments" - rather than as no
            // options, which is what a defaulted parameter would make of it.
            IDictionary<object, object> options = null;
            if (optionsArgument != Missing.Value) {
                var toHashSite = toHash.GetSite(TryConvertToHashAction.Make(context));
                options = (optionsArgument != null) ? toHashSite.Target(toHashSite, optionsArgument) : null;
                if (options == null) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 3, expected 1..2)");
                }
            }

            object _ = Missing.Value;
            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrMode, ref _);
            var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(toInt.Context));

            IOInfo info = new IOInfo();
            // A nil mode argument is MRI's "no mode here", which leaves the :mode option free to
            // supply one.
            if (optionsOrMode != Missing.Value && optionsOrMode != null) {
                int? m = toIntSite.Target(toIntSite, optionsOrMode);
                info = m.HasValue ? new IOInfo((IOMode)m) : IOInfo.Parse(context, Protocols.CastToString(toStr, optionsOrMode));
            }

            if (options != null) {
                info = info.AddOptions(toStr, options);
            }

            int? desc = toIntSite.Target(toIntSite, descriptor);
            if (!desc.HasValue) {
                throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(descriptor), "Fixnum");
            }
            Reinitialize(self, desc.Value, info);
            self.ConversionOptions = options;

            // #autoclose? is kept in an instance variable by the prelude, which is where the
            // option has to land for IO.new(fd, autoclose: false) to be visible.
            object autoclose;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("autoclose"), out autoclose)) {
                context.SetInstanceVariable(self, "@__autoclose", Protocols.IsTrue(autoclose));
            }

            return self;
        }

        internal static RubyIO/*!*/ Reinitialize(RubyIO/*!*/ io, int descriptor, IOInfo info) {
            IOMode mode = info.Mode;

            // A descriptor this process was handed rather than opened carries its own access
            // mode, and that is the one to believe: Ruby's default of "r" is an answer about a
            // path, and IO.new(fd) was not given one.
            IOMode adopted;
            bool known = io.Context.GetStream(descriptor) != null;
            if (!known && RubyIO.TryGetDescriptorMode(descriptor, out adopted)) {
                mode = (mode & ~IOMode.ReadWriteMask) | adopted;
            }

            Stream stream = GetDescriptorStream(io.Context, descriptor);

            // A mode the caller spelled out has to agree with what the descriptor was opened
            // for; MRI answers EINVAL when it does not. Only for a descriptor IronRuby opened -
            // an adopted one has just told us its mode and cannot disagree with itself.
            if (info.HasMode && known) {
                if ((mode.CanRead() && !stream.CanRead) || (mode.CanWrite() && !stream.CanWrite)) {
                    throw RubyExceptions.CreateEINVAL();
                }
            }

            io.Mode = mode;
            io.SetStream(stream);
            io.SetFileDescriptor(descriptor);

            if (info.HasEncoding) {
                io.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
            } else if ((mode & IOMode.PreserveEndOfLines) != 0) {
                // Binary mode with nothing said about encoding reads and writes bytes, which MRI
                // reports as an external encoding of ASCII-8BIT.
                io.SetEncodings(RubyEncoding.Binary, null);
            } else {
                // See RubyFileOps.Open: the defaults are resolved when the stream is opened.
                io.SetEncodings(null, null);
            }

            return io;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyIO/*!*/ InitializeCopy(RubyIO/*!*/ self, [NotNull]RubyIO/*!*/ source) {
            Stream stream = source.GetStream();
            int descriptor;

            // #dup is dup(2): the copy gets a descriptor of its own, so that reopening the
            // original and then reopening it back from the copy works - which is the whole
            // point of saving a stream before redirecting it. Sharing the table entry meant
            // the copy and the original were the same descriptor, so the save was a no-op.
            int duplicated = RubyIO.TryDuplicateDescriptor(source);
            if (duplicated >= 0) {
                stream = new FileStream(
                    new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)duplicated, true),
                    source.Mode.CanWrite() ? (source.Mode.CanRead() ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read,
                    1, false
                );
                descriptor = self.Context.AllocateFileDescriptor(stream);
            } else {
                descriptor = self.Context.DuplicateFileDescriptor(source.GetFileDescriptor());
            }

            self.SetStream(stream);
            self.SetFileDescriptor(descriptor);
            self.Mode = source.Mode;
            self.CopyEncodingsFrom(source);
            self.ConversionOptions = source.ConversionOptions;
            return self;
        }

        [RubyMethod("for_fd", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ ForFileDescriptor() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        #endregion

        #region reopen, sysopen

        // TODO: to_io

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(RubyIO/*!*/ self, [NotNull]RubyIO/*!*/ source) {
            // MRI's reopen is dup2(2): it points *this descriptor* at the other one's file,
            // which is why everything started afterwards inherits the redirection. Pointing
            // IronRuby's table entry at the other stream only redirects reads and writes made
            // from Ruby, so a redirected STDOUT went on reaching the terminal for every child
            // process - and this IO kept working through the other one's stream, which broke
            // as soon as that one was closed.
            if (RubyIO.TryRedirectDescriptor(self, source)) {
                self.Mode = source.Mode;
                return self;
            }

            self.Context.RedirectFileDescriptor(self.GetFileDescriptor(), source.GetFileDescriptor());
            self.SetStream(source.GetStream());
            self.Mode = source.Mode;
            return self;
        }

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(ConversionStorage<MutableString>/*!*/ toPath, RubyIO/*!*/ self, object path, [DefaultProtocol, Optional, NotNull]MutableString mode) {
            return Reopen(toPath, self, path, mode != null ? IOInfo.Parse(self.Context, mode) : new IOInfo(self.Mode));
        }

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(ConversionStorage<MutableString>/*!*/ toPath, RubyIO/*!*/ self, object path, int mode) {
            return Reopen(toPath, self, path, new IOInfo((IOMode)mode));
        }

        private static RubyIO/*!*/ Reopen(ConversionStorage<MutableString>/*!*/ toPath, RubyIO/*!*/ io, object pathObj, IOInfo info) {
            MutableString path = Protocols.CastToPath(toPath, pathObj);
            Stream newStream = RubyFile.OpenFileStream(io.Context, path.ToString(path.Encoding.Encoding), info.Mode);
            io.Context.SetStream(io.GetFileDescriptor(), newStream);
            io.SetStream(newStream);
            io.Mode = info.Mode;

            if (info.HasEncoding) {
                io.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
            }

            return io;
        }

        // TODO: params, conversions, options?

        [RubyMethod("sysopen", RubyMethodAttributes.PublicSingleton)]
        public static int SysOpen(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self, object pathObject, [Optional]object modeObject, [Optional]object perm) {

            // #to_path, and a nil mode or permission means "not given" rather than an empty one.
            MutableString path = Protocols.CastToPath(toPath, pathObject);
            MutableString mode = (modeObject == null || modeObject is Missing) ? null : Protocols.CastToString(toStr, modeObject);

            if (RubyFileOps.DirectoryExists(self.Context, path)) {
                // TODO: What file descriptor should be returned for a directory?
                return -1;
            }

            // The mode may carry an encoding suffix ("w:utf-8"), which only IOInfo parses.
            IOMode ioMode = (mode != null) ? IOInfo.Parse(self.Context, mode).Mode : IOMode.Default;

            // The descriptor stays open: MRI hands back a descriptor the caller owns and is
            // expected to wrap in an IO. Closing it here left nothing behind but a table index,
            // and IO.new(index) then read the *kernel's* descriptor of that number - some
            // unrelated pipe - to decide what the file had been opened for.
            RubyIO io = new RubyFile(self.Context, path.ToString(), ioMode);
            return io.GetFileDescriptor();
        }

        #endregion

        internal static object TryInvokeOpenBlock(RubyContext/*!*/ context, BlockParam/*!*/ block, RubyIO/*!*/ io) {
            if (block == null)
                return io;

            using (io) {
                object result;
                block.Yield(io, out result);
                return result;
            }
        }

        #region open

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return new RuleGenerator((metaBuilder, args, name) => {
                var targetClass = (RubyClass)args.Target;
                targetClass.BuildObjectConstructionNoFlow(metaBuilder, args, name);

                // TODO: initialize yields the block?
                if (args.Signature.HasBlock) {
                    // ignore flow builder set up so far, we need one that creates a BlockParam for library calls:
                    metaBuilder.ControlFlowBuilder = null;

                    if (metaBuilder.BfcVariable == null) {
                        metaBuilder.BfcVariable = metaBuilder.GetTemporary(typeof(BlockParam), "#bfc");
                    }

                    metaBuilder.Result = Ast.Call(new Func<UnaryOpStorage, BlockParam, object, object>(InvokeOpenBlock).GetMethodInfo(), 
                        Ast.Constant(new UnaryOpStorage(args.RubyContext)),
                        metaBuilder.BfcVariable, 
                        metaBuilder.Result
                    );

                    RubyMethodGroupInfo.RuleControlFlowBuilder(metaBuilder, args);
                } else {
                    metaBuilder.BuildControlFlow(args);
                }
            });
        }

        [Emitted]
        public static object InvokeOpenBlock(UnaryOpStorage/*!*/ closeStorage, BlockParam block, object obj) {
            object result = obj;
            if (!RubyOps.IsRetrySingleton(obj) && block != null) {
                try {
                    block.Yield(obj, out result);
                } finally {
                    try {
                        var site = closeStorage.GetCallSite("close");
                        site.Target(site, obj);                        
                    } catch (SystemException) {
                        // MRI: nop
                    }
                }
            }
            return result;
        }

        #endregion

        #region pipe, popen
#if FEATURE_PROCESS
        [RubyMethod("pipe", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static RubyArray/*!*/ OpenPipe(RubyClass/*!*/ self) {
            Stream reader, writer;
            RubyPipe.CreatePipe(out reader, out writer);
            RubyArray result = new RubyArray(2);
            result.Add(new RubyIO(self.Context, reader, IOMode.ReadOnly));
            result.Add(new RubyIO(self.Context, writer, IOMode.WriteOnly));
            return result;
        }

        // TODO: params, conversions, options?

        [RubyMethod("popen", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static object OpenPipe(RubyContext/*!*/ context, BlockParam block, RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ command, [DefaultProtocol, Optional, NotNull]MutableString modeString) {

            Process process;
            RubyIO io = OpenPipe(context, command, IOModeEnum.Parse(modeString), out process);
            if (block == null) {
                return io;
            }

            try {
                return TryInvokeOpenBlock(context, block, io);
            } finally {
                // MRI waits for the child when the block returns, so $? reports a finished
                // process rather than one still running
                try {
                    process.WaitForExit();
                } catch (SystemException) {
                    // process already reaped
                }
            }
        }

        [RubyMethod("popen", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static RubyIO/*!*/ OpenPipe(RubyContext/*!*/ context, RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ command, [DefaultProtocol, Optional, NotNull]MutableString modeString) {
            return OpenPipe(context, command, IOModeEnum.Parse(modeString));
        }

        public static RubyIO/*!*/ OpenPipe(
            RubyContext/*!*/ context, 
            MutableString/*!*/ command, 
            IOMode mode) {

            Process unused;
            return OpenPipe(context, command, mode, out unused);
        }

        internal static RubyIO/*!*/ OpenPipe(
            RubyContext/*!*/ context, 
            MutableString/*!*/ command, 
            IOMode mode,
            out Process/*!*/ process) {

            bool redirectStandardInput = mode.CanWrite();
            bool redirectStandardOutput = mode.CanRead();

            process = RubyProcess.CreateProcess(context, command, redirectStandardInput, redirectStandardOutput, false);

            StreamReader reader = null;
            StreamWriter writer = null;
            if (redirectStandardOutput) {
                reader = process.StandardOutput;
            }

            if (redirectStandardInput) {
                writer = process.StandardInput;
            }

            return new RubyIO(context, reader, writer, mode);
        }

#endif
        #endregion

        #region select

        /// <summary>
        /// IO.select(read, write, error, timeout). Blocks until one of the objects is ready, the
        /// timeout expires (nil), or - with no objects at all - forever.
        ///
        /// IronRuby's IOs are not all kernel descriptors: IO.pipe is an in-process queue, while a
        /// File or the read end of a popen really is a descriptor. So readiness is asked of the
        /// thing itself - poll(2) where there is a descriptor, the pipe's own state where there is
        /// not - and the wait is a poll loop rather than one blocking syscall. The loop also keeps
        /// the wait interruptible by Thread#kill and Thread#raise, and reports the thread as
        /// sleeping while it runs, which is what ruby/spec watches for before it writes to a pipe.
        /// </summary>
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            RubyContext/*!*/ context, object self,
            object read, [Optional]object write, [Optional]object error, [Optional]object timeout) {

            return SelectInternal(respondToStorage, toIoStorage, context, read, write, error, ToTimeInterval(context, timeout));
        }

        /// <summary>
        /// The timeout in milliseconds, or Timeout.Infinite for "wait as long as it takes". MRI
        /// reports a negative interval and NaN with these exact messages, which ruby/spec asserts.
        /// </summary>
        private static int ToTimeInterval(RubyContext/*!*/ context, object timeout) {
            if (timeout == null || timeout is Missing) {
                return Timeout.Infinite;
            }

            double seconds;
            if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is BigInteger) {
                seconds = (double)(BigInteger)timeout;
            } else {
                throw RubyExceptions.CreateTypeError("can't convert {0} into time interval",
                    context.GetClassDisplayName(timeout));
            }

            if (Double.IsNaN(seconds)) {
                throw RubyExceptions.CreateRangeError("NaN out of Time range");
            }
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            if (Double.IsPositiveInfinity(seconds)) {
                return Timeout.Infinite;
            }

            double ms = seconds * 1000;
            return (ms >= Int32.MaxValue) ? Timeout.Infinite : (int)ms;
        }

        /// <summary>What a select set asks of an IO.</summary>
        private enum Readiness { Read, Write, Error }

        /// <summary>One entry of a select set: the object the caller passed, and the IO behind it.</summary>
        private struct SelectEntry {
            public object Object;
            public RubyIO IO;
        }

        private static SelectEntry[]/*!*/ ToEntries(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context,
            object set, string/*!*/ argumentName) {

            if (set == null || set is Missing) {
                return new SelectEntry[0];
            }

            var array = set as RubyArray;
            if (array == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array)",
                    context.GetClassDisplayName(set));
            }

            var result = new SelectEntry[array.Count];
            for (int i = 0; i < array.Count; i++) {
                result[i] = new SelectEntry { Object = array[i], IO = ToSelectableIo(respondToStorage, toIoStorage, context, array[i]) };
            }
            return result;
        }

        /// <summary>
        /// MRI's rb_io_check_io: an IO is itself, anything else is asked for #to_io, and whatever
        /// comes back has to be an IO. The object the caller passed is what goes in the result, not
        /// the IO it named.
        /// </summary>
        private static RubyIO/*!*/ ToSelectableIo(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj) {

            var io = obj as RubyIO;
            if (io == null && Protocols.RespondTo(respondToStorage, obj, "to_io")) {
                var site = toIoStorage.GetCallSite("to_io", 0);
                io = site.Target(site, obj) as RubyIO;
            }

            if (io == null) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into IO", context.GetClassDisplayName(obj));
            }
            return io;
        }

        private static RubyArray SelectInternal(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context,
            object read, object write, object error, int timeoutMilliseconds) {

            var reads = ToEntries(respondToStorage, toIoStorage, context, read, "read");
            var writes = ToEntries(respondToStorage, toIoStorage, context, write, "write");
            var errors = ToEntries(respondToStorage, toIoStorage, context, error, "error");

            if (reads.Length == 0 && writes.Length == 0 && errors.Length == 0) {
                // Nothing to watch: MRI just sleeps, and IO.select(nil, nil, nil) is the documented
                // way to sleep forever in a thread that Thread#kill can still end.
                if (timeoutMilliseconds == Timeout.Infinite) {
                    ThreadOps.SleepForLibrary(Timeout.Infinite);
                } else if (timeoutMilliseconds > 0) {
                    ThreadOps.SleepForLibrary(timeoutMilliseconds);
                }
                return null;
            }

            long deadline = (timeoutMilliseconds == Timeout.Infinite)
                ? Int64.MaxValue
                : Environment.TickCount64 + timeoutMilliseconds;

            var info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            bool wasBlocked = info.Blocked;
            try {
                // A thread parked in select is asleep as far as Ruby is concerned.
                info.Blocked = true;

                while (true) {
                    RubyArray ready = CollectReady(reads, writes, errors);
                    if (ready != null) {
                        return ready;
                    }

                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) {
                        return null;
                    }

                    RubyUtils.CheckAsyncException();
                    Thread.Sleep((int)Math.Min(remaining, SelectPollIntervalMilliseconds));
                }
            } finally {
                info.Blocked = wasBlocked;
            }
        }

        // How long the select loop waits between readiness checks. Short enough that a spec timing
        // a 1 ms select does not notice, long enough not to spin a core.
        private const int SelectPollIntervalMilliseconds = 1;

        /// <summary>The three result arrays, or null when nothing is ready yet.</summary>
        private static RubyArray CollectReady(SelectEntry[]/*!*/ reads, SelectEntry[]/*!*/ writes, SelectEntry[]/*!*/ errors) {
            RubyArray readReady = CollectReady(reads, Readiness.Read);
            RubyArray writeReady = CollectReady(writes, Readiness.Write);
            RubyArray errorReady = CollectReady(errors, Readiness.Error);

            if (readReady.Count == 0 && writeReady.Count == 0 && errorReady.Count == 0) {
                return null;
            }

            var result = new RubyArray(3);
            result.Add(readReady);
            result.Add(writeReady);
            result.Add(errorReady);
            return result;
        }

        private static RubyArray/*!*/ CollectReady(SelectEntry[]/*!*/ entries, Readiness kind) {
            var result = new RubyArray();
            for (int i = 0; i < entries.Length; i++) {
                if (IsReady(entries[i].IO, kind)) {
                    result.Add(entries[i].Object);
                }
            }
            return result;
        }

        private static bool IsReady(RubyIO/*!*/ io, Readiness kind) {
            if (io.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }

            // The mode settles the two ends of a pipe: they share a queue, so only the mode says
            // which of them a read would ever come from. MRI gets the same answer from the kernel,
            // which knows each end by its own descriptor.
            if (kind == Readiness.Read && !io.Mode.CanRead()) {
                return false;
            }
            if (kind == Readiness.Write && !io.Mode.CanWrite()) {
                return false;
            }

            var stream = io.GetStream();
            if (kind == Readiness.Read && stream.DataBuffered) {
                return true;
            }

            var pipe = stream.BaseStream as RubyPipe;
            if (pipe != null) {
                switch (kind) {
                    case Readiness.Read: return pipe.CanReadWithoutBlocking;
                    // The queue is unbounded, so a write never blocks, and there is no out-of-band
                    // data on an in-process pipe for the error set to report.
                    case Readiness.Write: return true;
                    default: return false;
                }
            }

            int descriptor = io.NativeDescriptor;
            if (descriptor < 0) {
                // No descriptor and no pipe - a StringIO-like stream. MRI says a regular file is
                // always ready, and this is as close as we get.
                return kind != Readiness.Error;
            }

            short events;
            switch (kind) {
                case Readiness.Read: events = RubyIO.POLLIN; break;
                case Readiness.Write: events = RubyIO.POLLOUT; break;
                default: events = POLLPRI; break;
            }

            short[] revents = RubyIO.Poll(new object[] { descriptor }, new object[] { events }, 0);
            if (revents == null) {
                // No poll on this platform: MRI's answer for a plain file, which is what is left.
                return kind != Readiness.Error;
            }

            // A hung-up or broken descriptor is ready in the sense that the operation will not
            // block - it returns EOF or fails at once, which is what MRI reports too.
            const short broken = RubyIO.POLLERR | RubyIO.POLLHUP | RubyIO.POLLNVAL;
            if (kind != Readiness.Error && (revents[0] & broken) != 0) {
                return true;
            }
            return (revents[0] & events) != 0;
        }

        // Out-of-band data; only sockets ever report it, but the error set means nothing else.
        private const short POLLPRI = 0x002;

        #endregion

        #region close, close_read, close_write, closed?, close_on_exec (1.9)

        [RubyMethod("close")]
        public static void Close(RubyIO/*!*/ self) {
            if (self.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            self.Close();
        }

        // TODO:
        [RubyMethod("close_read")]
        public static void CloseReader(RubyIO/*!*/ self) {
            if (self.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            self.CloseReader();
        }

        // TODO:
        [RubyMethod("close_write")]
        public static void CloseWriter(RubyIO/*!*/ self) {
            if (self.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            self.CloseWriter();
        }
        
        [RubyMethod("closed?")]
        public static bool Closed(RubyIO/*!*/ self) {
            return self.Closed;
        }

        // TODO: 1.9 only
        // close_on_exec=
        // close_on_exec?

        #endregion

        //stat

        #region fcntl/ioctl, fsync/flush

        [RubyMethod("ioctl")]
        [RubyMethod("fcntl")]
        public static int FileControl(RubyIO/*!*/ self, [DefaultProtocol]int commandId, [Optional]MutableString arg) {
            return self.FileControl(commandId, (arg != null) ? arg.ConvertToBytes() : null);
        }

        [RubyMethod("ioctl")]
        [RubyMethod("fcntl")]
        public static int FileControl(RubyIO/*!*/ self, [DefaultProtocol]int commandId, int arg) {
            return self.FileControl(commandId, arg);
        }

        [RubyMethod("fsync")]
        [RubyMethod("flush")]
        public static void Flush(RubyIO/*!*/ self) {
            self.Flush();
        }

        #endregion

        #region eof, pid, to_i, binmode, sync, sync=, to_io, inspect

        [RubyMethod("eof")]
        [RubyMethod("eof?")]
        public static bool Eof(RubyIO/*!*/ self) {
            self.RequireReadable();
            return self.IsEndOfStream();
        }

        [RubyMethod("pid")]
        public static object Pid(RubyIO/*!*/ self) {
            return null;  // OK to return null on Windows
        }

        [RubyMethod("fileno")]
        [RubyMethod("to_i")]
        public static int FileNo(RubyIO/*!*/ self) {
            return self.GetFileDescriptor();
        }

        [RubyMethod("binmode")]
        public static RubyIO/*!*/ Binmode(RubyIO/*!*/ self) {
            if (!self.Closed && self.Position == 0) {
                self.PreserveEndOfLines = true;
            }
            return self;
        }

        [RubyMethod("binmode?")]
        public static bool IsBinmode(RubyIO/*!*/ self) {
            return self.IsBinmode;
        }

        [RubyMethod("stat", BuildConfig = "FEATURE_FILESYSTEM")]
        public static System.IO.FileSystemInfo/*!*/ Stat(RubyIO/*!*/ self) {
            return RubyFileOps.RubyStatOps.Create(self);
        }

        [RubyMethod("sync")]
        public static bool Sync(RubyIO/*!*/ self) {
            self.RequireOpen();
            return self.AutoFlush;
        }

        [RubyMethod("sync=")]
        public static bool Sync(RubyIO/*!*/ self, bool sync) {
            self.RequireOpen();
            self.AutoFlush = sync;
            return sync;
        }

        [RubyMethod("to_io")]
        public static RubyIO/*!*/ ToIO(RubyIO/*!*/ self) {
            return self;
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyIO/*!*/ self) {
            var result = MutableString.CreateMutable(self.Context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(self.Context.GetClassOf(self).GetName(self.Context));
            result.Append(':');
            if (self.Initialized) {
                switch (self.ConsoleStreamType) {
                    case ConsoleStreamType.Input: result.Append("<STDIN>"); break;
                    case ConsoleStreamType.Output: result.Append("<STDOUT>"); break;
                    case ConsoleStreamType.ErrorOutput: result.Append("<STDERR>"); break;
                    case null: result.Append("fd ").Append(self.GetFileDescriptor().ToString(CultureInfo.InvariantCulture)); break;
                }
            } else {
                RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(self.Context, self));
            }
            result.Append('>');
            return result;
        }

        #endregion

        #region isatty

        [RubyMethod("isatty")]
        [RubyMethod("tty?")]
        public static bool IsAtty(RubyIO/*!*/ self) {
            ConsoleStreamType? console = self.ConsoleStreamType;
            if (console == null) {
                return self.GetStream().BaseStream == Stream.Null;
            }

            int fd = GetStdHandleFd(console.Value);
            switch (Environment.OSVersion.Platform) {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    IntPtr handle = GetStdHandle(fd);
                    if (handle == IntPtr.Zero) {
                        throw new Win32Exception();
                    }

                    return GetFileType(handle) == FILE_TYPE_CHAR;

                default:
                    return isatty(fd) == 1;
            }
        }

        private static int GetStdHandleFd(ConsoleStreamType streamType) {
            switch (streamType) {
                case ConsoleStreamType.Input: return STD_INPUT_HANDLE;
                case ConsoleStreamType.Output: return STD_OUTPUT_HANDLE;
                case ConsoleStreamType.ErrorOutput: return STD_ERROR_HANDLE;
                default: throw Assert.Unreachable;
            }
        }

        private const int FILE_TYPE_CHAR = 0x0002;

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        [DllImport("kernel32")]
        private extern static IntPtr GetStdHandle(int nStdHandle);
        
        [DllImport("kernel32")]
        private extern static int GetFileType(IntPtr hFile);

        [DllImport ("libc")]
        private static extern int isatty(int desc);

        #endregion

        #region external_encoding, internal_encoding, set_encoding

        /// <summary>
        /// CRuby's rb_io_external_encoding (io.c): the pinned external encoding if there is
        /// one, and otherwise the current Encoding.default_external for a readable stream but
        /// nil for a write-only one - which is what a plain "w" or "r+" gets.
        /// </summary>
        [RubyMethod("external_encoding")]
        public static RubyEncoding GetExternalEncoding(RubyIO/*!*/ self) {
            if (self.Enc2 != null) {
                return self.Enc2;
            }
            if (self.Mode.CanWrite()) {
                return self.Enc;
            }
            return self.Enc ?? self.Context.DefaultExternalEncoding;
        }

        /// <summary>
        /// CRuby's rb_io_internal_encoding (io.c). The internal encoding is what the bytes get
        /// transcoded *to*, so it is nil whenever there is no transcoding to do.
        /// </summary>
        [RubyMethod("internal_encoding")]
        public static RubyEncoding GetInternalEncoding(RubyIO/*!*/ self) {
            return self.Enc2 != null ? self.Enc : null;
        }

        // TODO: to-str, last param to-hash

        [RubyMethod("set_encoding")]
        public static RubyIO/*!*/ SetEncodings(ConversionStorage<IDictionary<object, object>>/*!*/ toHash, ConversionStorage<MutableString>/*!*/ toStr,
            RubyIO/*!*/ self, object external, [Optional]object @internal, [Optional]IDictionary<object, object> options) {

            Protocols.TryConvertToOptions(toHash, ref options, ref external, ref @internal);
            self.ConversionOptions = options;

            RubyEncoding externalEncoding = null, internalEncoding = null;
            if (external != Missing.Value && external != null) {
                externalEncoding = Protocols.ConvertToEncoding(toStr, external);
            }
            if (@internal != Missing.Value && @internal != null) {
                if (external == null) {
                    // set_encoding(nil, <something>) goes down MRI's "the second argument names
                    // the internal encoding of a pair" path and tries to coerce nil to a String.
                    throw RubyExceptions.CreateTypeConversionError("nil", "String");
                }
                internalEncoding = Protocols.ConvertToEncoding(toStr, @internal);
            }
            return SetEncodings(self, externalEncoding, internalEncoding);
        }

        /// <summary>
        /// The conversion options the stream was opened with, for the transcoding the library
        /// code does on the way in and on the way out. Not a CRuby method.
        /// </summary>
        [RubyMethod("__conversion_options__", RubyMethodAttributes.PrivateInstance)]
        public static object GetConversionOptions(RubyIO/*!*/ self) {
            return self.ConversionOptions;
        }

        [RubyMethod("set_encoding")]
        public static RubyIO/*!*/ SetEncodings(RubyIO/*!*/ self, RubyEncoding external, [DefaultParameterValue(null)]RubyEncoding @internal) {
            self.SetEncodings(external, @internal);
            return self;
        }

        #endregion

        #region rewind, seek, sysseek, pos, tell, lineno

        [RubyMethod("rewind")]
        public static void Rewind(RubyContext/*!*/ context, RubyIO/*!*/ self) {
            self.Seek(0, SeekOrigin.Begin);
            self.LineNumber = 0;
        }

        [RubyMethod("seek")]
        public static int Seek(RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos, [DefaultProtocol, DefaultParameterValue(SEEK_SET)]int seekOrigin) {
            self.Seek(pos.ToInt64(), RubyIO.ToSeekOrigin(seekOrigin));
            return 0;
        }

        [RubyMethod("sysseek")]
        public static object SysSeek(RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos, [DefaultProtocol, DefaultParameterValue(SEEK_SET)]int seekOrigin) {
            self.Flush();
            self.Seek(pos.ToInt64(), RubyIO.ToSeekOrigin(seekOrigin));
            return pos.ToObject();
        }

        [RubyMethod("pos")]
        [RubyMethod("tell")]
        public static object/*!*/ Pos(RubyIO/*!*/ self) {
            if (self.Position <= Int32.MaxValue) {
                return (int)self.Position;
            }

            return (BigInteger)self.Position;
        }

        [RubyMethod("pos=")]
        public static void Pos(RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos) {
            self.Seek(pos.ToInt64(), SeekOrigin.Begin);
        }

        [RubyMethod("lineno")]
        public static int GetLineNumber(RubyIO/*!*/ self) {
            self.RequireOpen();
            return self.LineNumber;
        }

        [RubyMethod("lineno=")]
        public static void SetLineNumber(RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]int value) {
            self.RequireOpen();
            self.LineNumber = value;
        }

        #endregion

        #region write, syswrite, write_nonblock

        [RubyMethod("write")]
        public static int Write(RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {
            int bytesWritten = val.IsEmpty ? 0 : self.WriteBytes(val, 0, val.GetByteCount());
            if (self.AutoFlush) {
                self.Flush();
            }
            return bytesWritten;
        }

        [RubyMethod("write")]
        public static int Write(ConversionStorage<MutableString>/*!*/ tosConversion, RubyIO/*!*/ self, object obj) {
            return Write(self, Protocols.ConvertToString(tosConversion, obj));
        }

        [RubyMethod("syswrite")]
        public static int SysWrite(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyContext/*!*/ context, RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {

            RubyBufferedStream stream = self.GetWritableStream();
            if (stream.DataBuffered) {
                PrintOps.ReportWarning(writeStorage, tosConversion, MutableString.CreateAscii("syswrite for buffered IO"));
            }
            int bytes = Write(self, val);
            self.Flush();
            return bytes;
        }

        [RubyMethod("syswrite")]
        public static int SysWrite(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyContext/*!*/ context, RubyIO/*!*/ self, object obj) {
            return SysWrite(writeStorage, tosConversion, context, self, Protocols.ConvertToString(tosConversion, obj));
        }

        [RubyMethod("write_nonblock")]
        public static int WriteNoBlock(RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {
            self.RequireWritable();
            int result = -1;
            self.NonBlockingOperation(() => result = Write(self, val), false);
            return result;
        }

        [RubyMethod("write_nonblock")]
        public static int WriteNoBlock(ConversionStorage<MutableString>/*!*/ tosConversion, RubyIO/*!*/ self, object obj) {
            return Write(self, Protocols.ConvertToString(tosConversion, obj));
        }
        
        #endregion

        #region read, sysread, read_nonblock, readpartial

        private static MutableString PrepareReadBuffer(RubyIO/*!*/ io, MutableString buffer) {
            if (buffer == null) {
                buffer = MutableString.CreateBinary();
            } else {
                buffer.Clear();
            } 
#if TODO
            var internalEncoding = io.InternalEncoding ?? io.ExternalEncoding;

            if (buffer != null) {
                buffer.Clear();
                buffer.ForceEncoding(internalEncoding);
            } else if (io.ExternalEncoding == RubyEncoding.Binary && internalEncoding == RubyEncoding.Binary) {
                buffer = MutableString.CreateBinary();
            } else {
                buffer = MutableString.CreateMutable(internalEncoding);
            }
#endif            
            return buffer;
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(RubyIO/*!*/ self) {
            return Read(self, null, null);
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(RubyIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, Optional]MutableString buffer) {
            buffer = PrepareReadBuffer(self, buffer);
            self.AppendBytes(buffer, Int32.MaxValue);
            return buffer;
        }

        [RubyMethod("read")]
        public static MutableString Read(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            self.RequireReadable();
            if (bytes < 0) {
                throw RubyExceptions.CreateArgumentError("negative length -1 given");
            }

            buffer = PrepareReadBuffer(self, buffer);
            int bytesRead = self.AppendBytes(buffer, bytes);
            return (bytesRead == 0 && bytes != 0) ? null : buffer;
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            var stream = self.GetReadableStream();
            if (stream.DataBuffered) {
                throw RubyExceptions.CreateIOError("sysread for buffered IO");
            }

            // We use Flush to simulate non-buffered IO. 
            // A better approach would be to create a parallel FileStream with 
            // System.IO.FileOptions.WriteThrough (which corresponds to FILE_FLAG_NO_BUFFERING), and also maybe 
            // System.IO.FileOptions.SequentialScan (FILE_FLAG_SEQUENTIAL_SCAN).
            // TODO: sysopen does that?
            stream.Flush();

            var result = Read(self, bytes, buffer);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        [RubyMethod("read_nonblock")]
        public static MutableString ReadNoBlock(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            self.RequireReadable();
            MutableString result = null;
            self.NonBlockingOperation(() => result = Read(self, bytes, buffer), true);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        [RubyMethod("read", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Read(
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<int>/*!*/ fixnumCast,
            ConversionStorage<MutableString>/*!*/ toPath,
            RubyClass/*!*/ self,
            object path, 
            [Optional]object optionsOrLength, 
            [Optional]object optionsOrOffset,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrLength, ref optionsOrOffset);
            var site = fixnumCast.GetSite(ConvertToFixnumAction.Make(fixnumCast.Context));

            int length = (optionsOrLength != Missing.Value && optionsOrLength != null) ? site.Target(site, optionsOrLength) : 0;
            int offset = (optionsOrOffset != Missing.Value && optionsOrOffset != null) ? site.Target(site, optionsOrOffset) : 0;

            if (offset < 0) {
                throw RubyExceptions.CreateEINVAL();
            }

            if (length < 0) {
                throw RubyExceptions.CreateArgumentError("negative length {0} given", length);
            }

            // TODO: options

            using (RubyIO io = new RubyFile(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), IOMode.ReadOnly)) {
                if (offset > 0) {
                    io.Seek(offset, SeekOrigin.Begin);
                }

                if (optionsOrLength != Missing.Value && optionsOrLength != null) {
                    return Read(io, length, null);
                } else {
                    return Read(io);
                }
            }
        }

        //readpartial

        #endregion

        #region readchar, readbyte (1.9), readline, readlines

        // returns a string in 1.9
        [RubyMethod("readchar")]
        public static int ReadChar(RubyIO/*!*/ self) {
            self.RequireReadable();
            int c = self.ReadByteNormalizeEoln();
            
            if (c == -1) {
                throw new EOFError("end of file reached");
            }

            return c;
        }

        // readbyte

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, RubyIO/*!*/ self) {
            return ReadLine(scope, self, scope.RubyContext.InputSeparator, -1);
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, RubyIO/*!*/ self, DynamicNull separator) {
            return ReadLine(scope, self, null, -1);
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return ReadLine(scope, self, scope.RubyContext.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return ReadLine(scope, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(RubyScope/*!*/ scope, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {

            // no dynamic call, modifies $_ scope variable:
            MutableString result = Gets(scope, self, separator, limit);
            if (result == null) {
                throw new EOFError("end of file reached");
            }

            return result;
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, RubyIO/*!*/ self) {
            return ReadLines(context, self, context.InputSeparator, -1);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, RubyIO/*!*/ self, DynamicNull separator) {
            return ReadLines(context, self, null, -1);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return ReadLines(context, self, context.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return ReadLines(context, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            RubyArray result = new RubyArray();

            // no dynamic call, doesn't modify $_ scope variable:
            MutableString line;
            while ((line = self.ReadLineOrParagraph(separator, limit)) != null) {
                result.Add(line);
            }

            self.LineNumber += result.Count;
            context.InputProvider.LastInputLineNumber = self.LineNumber;
            return result;
        }

        // TODO: to_hash, to_str, to_int

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ path, [DefaultProtocol, DefaultParameterValue(-1)]int limit) {

            return ReadLines(self, path, self.Context.InputSeparator, limit);
        }

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString path, [DefaultProtocol]MutableString separator, 
            [DefaultProtocol, DefaultParameterValue(-1)]int limit) {

            using (RubyIO io = new RubyIO(self.Context, self.Context.Platform.OpenInputFileStream(path.ConvertToString()), IOMode.ReadOnly)) {
                return ReadLines(self.Context, io, separator, limit);
            }
        }

        #endregion

        #region getc, gets, ungetc, getbyte (1.9)

        [RubyMethod("getc")]
        public static object Getc(RubyIO/*!*/ self) {
            int c = self.ReadByteNormalizeEoln();
            return (c != -1) ? ScriptingRuntimeHelpers.Int32ToObject(c) : null;
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, RubyIO/*!*/ self) {
            return Gets(scope, self, scope.RubyContext.InputSeparator, -1);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, RubyIO/*!*/ self, DynamicNull separator) {
            return Gets(scope, self, null, -1);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return Gets(scope, self, scope.RubyContext.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return Gets(scope, self, separatorOrLimit.String(), -1);
            } 
        }

        [RubyMethod("gets")]
        public static MutableString Gets(RubyScope/*!*/ scope, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {

            MutableString result = self.ReadLineOrParagraph(separator, limit);
            if (result != null) {
                result.IsTainted = true;
            }

            scope.GetInnerMostClosureScope().LastInputLine = result;
            scope.RubyContext.InputProvider.LastInputLineNumber = ++self.LineNumber;

            return result;
        }

        [RubyMethod("ungetc")]
        public static void SetPreviousByte(RubyIO/*!*/ self, [DefaultProtocol]int b) {
            self.PushBack(unchecked((byte)b));
        }

        // TODO: 1.9 only
        // getbyte

        #endregion

        #region foreach, each, each_byte, each_line

        // TODO: to_hash, to_str, to_int

        [RubyMethod("foreach", RubyMethodAttributes.PublicSingleton)]
        public static void ForEach(BlockParam block, RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol, DefaultParameterValue(-1)]int limit) {
            ForEach(block, self, path, self.Context.InputSeparator, limit);
        }

        [RubyMethod("foreach", RubyMethodAttributes.PublicSingleton)]
        public static void ForEach(BlockParam block, RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ path,
            [DefaultProtocol]MutableString separator, [DefaultProtocol, DefaultParameterValue(-1)]int limit) {
            using (RubyIO io = new RubyIO(self.Context, self.Context.Platform.OpenInputFileStream(path.ConvertToString()), IOMode.ReadOnly)) {
                Each(self.Context, block, io, separator, limit);
            }
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self) {
            return Each(context, block, self, context.InputSeparator, -1);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, DynamicNull separator) {
            return Each(context, block, self, null, -1);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            if (separatorOrLimit.IsFixnum()) {
                return Each(context, block, self, context.InputSeparator, separatorOrLimit.Fixnum());
            } else {
                return Each(context, block, self, separatorOrLimit.String(), -1);
            }
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            self.RequireReadable();

            MutableString line;
            while ((line = self.ReadLineOrParagraph(separator, limit)) != null) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                line.IsTainted = true;
                context.InputProvider.LastInputLineNumber = ++self.LineNumber;

                object result;
                if (block.Yield(line, out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("each_byte")]
        public static object EachByte(BlockParam block, RubyIO/*!*/ self) {
            self.RequireReadable();
            object aByte;
            while ((aByte = Getc(self)) != null) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                object result;
                if (block.Yield((int)aByte, out result)) {
                    return result;
                }
            }
            return self;
        }

        #endregion

        #region copy_stream

        [RubyMethod("copy_stream", RubyMethodAttributes.PublicSingleton)]
        public static object CopyStream(
            ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ toInt, RespondToStorage/*!*/ respondTo,
            BinaryOpStorage/*!*/ writeStorage, CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ readStorage,
            RubyClass/*!*/ self, object src, object dst, [DefaultParameterValue(-1)]int count, [DefaultParameterValue(-1)]int src_offset) {

            if (count < -1) {
                throw RubyExceptions.CreateArgumentError("count should be >= -1");
            }

            if (src_offset < -1) {
                throw RubyExceptions.CreateArgumentError("src_offset should be >= -1");
            }

            RubyIO srcIO = src as RubyIO;
            RubyIO dstIO = dst as RubyIO;
            Stream srcStream = null, dstStream = null;
            var context = toPath.Context;
            CallSite<Func<CallSite, object, object, object>> writeSite = null;
            CallSite<Func<CallSite, object, object, object, object>> readSite = null;

            try {
                if (srcIO == null || dstIO == null) {
                    var toPathSite = toPath.GetSite(TryConvertToPathAction.Make(toPath.Context));
                    var srcPath = toPathSite.Target(toPathSite, src);
                    if (srcPath != null) {
                        srcStream = self.Context.Platform.OpenInputFileStream(context.DecodePath(srcPath), FileMode.Open, FileAccess.Read, FileShare.Read);
                    } else {
                        readSite = readStorage.GetCallSite("read", 2);
                    }

                    var dstPath = toPathSite.Target(toPathSite, dst);
                    if (dstPath != null) {
                        dstStream = self.Context.Platform.OpenInputFileStream(context.DecodePath(dstPath), FileMode.Truncate, FileAccess.ReadWrite, FileShare.Read);
                    } else {
                        writeSite = writeStorage.GetCallSite("write", 1);
                    }
                } else {
                    srcStream = srcIO.GetReadableStream();
                    dstStream = dstIO.GetWritableStream();
                }

                if (src_offset != -1) {
                    if (srcStream == null) {
                        throw RubyExceptions.CreateArgumentError("cannot specify src_offset for non-IO");
                    }
                    srcStream.Seek(src_offset, SeekOrigin.Current);
                }

                MutableString userBuffer = null;
                byte[] buffer = null;

                long bytesCopied = 0;
                long remaining = (count < 0) ? Int64.MaxValue : count;
                int minBufferSize = 16 * 1024;
                
                if (srcStream != null) {
                    buffer = new byte[Math.Min(minBufferSize, remaining)];
                }

                while (remaining > 0) {
                    int bytesRead;
                    int chunkSize = (int)Math.Min(minBufferSize, remaining);
                    if (srcStream != null) {
                        userBuffer = null;
                        bytesRead = srcStream.Read(buffer, 0, chunkSize);
                    } else {
                        userBuffer = MutableString.CreateBinary();
                        bytesRead = Protocols.CastToFixnum(toInt, readSite.Target(readSite, src, chunkSize, userBuffer));
                    }
                    
                    if (bytesRead <= 0) {
                        break;
                    }

                    if (dstStream != null) {
                        if (userBuffer != null) {
                            dstStream.Write(userBuffer, 0, bytesRead);
                        } else {
                            dstStream.Write(buffer, 0, bytesRead);
                        }
                    } else {
                        if (userBuffer == null) {
                            userBuffer = MutableString.CreateBinary(bytesRead).Append(buffer, 0, bytesRead);
                        } else {
                            userBuffer.SetByteCount(bytesRead);
                        }
                        writeSite.Target(writeSite, dst, userBuffer);
                    }
                    bytesCopied += bytesRead;
                    remaining -= bytesRead;
                }
                return Protocols.Normalize(bytesCopied);

            } finally {
                if (srcStream != null) {
                    srcStream.Dispose();
                }
                if (dstStream != null) {
                    dstStream.Dispose();
                }
            }
        }

        #endregion

        // TODO: 1.9 only
        // bytes -> Enumerable::Enumerator
        // lines -> Enumerable::Enumerator

        public static IOWrapper/*!*/ CreateIOWrapper(RespondToStorage/*!*/ respondToStorage, object io, FileAccess access) {
            return CreateIOWrapper(respondToStorage, io, access, 0x1000);
        }

        public static IOWrapper/*!*/ CreateIOWrapper(RespondToStorage/*!*/ respondToStorage, object io, FileAccess access, int bufferSize) {
            bool canRead, canWrite, canSeek, canFlush, canBeClosed;

            if (access == FileAccess.Read || access == FileAccess.ReadWrite) {
                canRead = Protocols.RespondTo(respondToStorage, io, "read");
            } else {
                canRead = false;
            }

            if (access == FileAccess.Write || access == FileAccess.ReadWrite) {
                canWrite = Protocols.RespondTo(respondToStorage, io, "write");
            } else {
                canWrite = false;
            }

            canSeek = Protocols.RespondTo(respondToStorage, io, "seek") && Protocols.RespondTo(respondToStorage, io, "tell");
            canFlush = Protocols.RespondTo(respondToStorage, io, "flush");
            canBeClosed = Protocols.RespondTo(respondToStorage, io, "close");

            return new IOWrapper(respondToStorage.Context, io, canRead, canWrite, canSeek, canFlush, canBeClosed, bufferSize);
        }
    }
}
