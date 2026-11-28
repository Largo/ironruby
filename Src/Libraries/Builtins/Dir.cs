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

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;

namespace IronRuby.Builtins {
    [RubyClass("Dir", Inherits = typeof(object)), Includes(typeof(Enumerable))]
    public class RubyDir : RubyObject {
        // null if closed
        private MutableString _dirName;
        private string[] _rawEntries;

        // The real directory descriptor behind #fileno, opened on demand: the entries come
        // out of System.IO, so nothing else here needs one and a Dir that is never asked
        // for its descriptor should not hold one open. -1 means "not open".
        private int _fd = -1;

        // _pos starts from -2 as ".", -1 as "..", 
        // 0 will be the first item from Directory.GetFileSystemEntries.
        private int _pos;

        // the `encoding:' option, which labels the names this directory reports
        private RubyEncoding _encoding;

        #region Construction

        public RubyDir(RubyClass/*!*/ cls) 
            : base(cls) {
        }

        public RubyDir(RubyClass/*!*/ cls, MutableString/*!*/ dirname) 
            : base(cls) {
            Reinitialize(this, dirname);
        }

        public RubyDir(RubyClass/*!*/ cls, MutableString/*!*/ dirname, RubyEncoding encoding)
            : this(cls, dirname) {
            _encoding = encoding;
        }

        /// <summary>
        /// The `encoding:' option every Dir reader takes. It names how the entries are labelled
        /// on the way out; the bytes are the filesystem's either way.
        /// </summary>
        private static RubyEncoding GetEncodingOption(RubyContext/*!*/ context, IDictionary<object, object> options) {
            if (options == null) {
                return null;
            }
            // It is the only keyword: MRI's dir_initialize refuses any other ([Bug #8060]).
            RubyEncoding result = null;
            var unknown = new List<string>();
            foreach (var entry in options) {
                var key = (entry.Key as RubySymbol);
                if (key == null || key.ToString() != "encoding") {
                    unknown.Add(context.Inspect(entry.Key).ToString());
                } else if (entry.Value != null) {
                    result = entry.Value as RubyEncoding;
                    if (result == null) {
                        var name = entry.Value as MutableString;
                        result = context.GetRubyEncoding(name ?? MutableString.Create(context.Inspect(entry.Value)));
                    }
                }
            }
            if (unknown.Count > 0) {
                throw RubyExceptions.CreateArgumentError(
                    (unknown.Count == 1 ? "unknown keyword: " : "unknown keywords: ") + String.Join(", ", unknown));
            }
            return result;
        }

        [RubyConstructor]
        public static RubyDir/*!*/ Create(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object dirname,
            [Keywords, Optional]IDictionary<object, object> options) {
            return new RubyDir(self, Protocols.CastToPath(toPath, dirname), GetEncodingOption(self.Context, options));
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyDir/*!*/ Reinitialize(ConversionStorage<MutableString>/*!*/ toPath, RubyDir/*!*/ self, object dirname,
            [Keywords, Optional]IDictionary<object, object> options) {
            self._encoding = GetEncodingOption(self.ImmediateClass.Context, options);
            return Reinitialize(self, Protocols.CastToPath(toPath, dirname));
        }

        private static RubyDir/*!*/ Reinitialize(RubyDir/*!*/ self, MutableString/*!*/ dirname) {
            self.Close();

            string strName = self.ImmediateClass.Context.DecodePath(dirname);
            try {
                self._rawEntries = self.Platform.GetFileSystemEntries(strName, "*");
            } catch (Exception ex) {
                throw ToRubyException(ex, strName, DirectoryOperation.Open);
            }
            self._dirName = dirname.Clone();
            self._pos = -2;
            return self;
        }

        #endregion

        #region Singleton Methods

        private static void SetCurrentDirectory(PlatformAdaptationLayer/*!*/ pal, string/*!*/ dir) {
            try {
                // TODO: MRI calls Win32 API SetCurrentDirctory directly, while BCL normalizes the path first.
                pal.CurrentDirectory = dir;
            } catch (Exception e) {
                throw ToRubyException(e, dir, DirectoryOperation.ChangeDir);
            }
        }

        /// <summary>
        /// raise a SystemCallError if the target directory does not exist
        /// </summary>
        /// <returns>0 if no block is given; otherwise, the value of the block</returns>
        [RubyMethod("chdir", RubyMethodAttributes.PublicSingleton)]
        public static object ChangeDirectory(ConversionStorage<MutableString>/*!*/ toPath, BlockParam block, RubyClass/*!*/ self, object dir) {
            var d = Protocols.CastToPath(toPath, dir);
            return ChangeDirectory(self.Context, self.Context.DecodePath(d), d, block);
        }

        /// <summary>
        /// change the directory to the value of the environment variable HOME or LOGDIR
        /// </summary>
        [RubyMethod("chdir", RubyMethodAttributes.PublicSingleton)]
        public static object ChangeDirectory(BlockParam block, RubyClass/*!*/ self) {
            // MRI's dir_s_chdir: HOME, then LOGDIR, and nothing else. On Windows MRI has set HOME
            // at startup if it was missing, so the profile directory stands in for it there.
            var pal = self.Context.Platform;
            string defaultDirectory = RubyEnvironment.GetVariable(pal, "HOME") ?? RubyEnvironment.GetVariable(pal, "LOGDIR");
            if (defaultDirectory == null && Environment.OSVersion.Platform == PlatformID.Win32NT) {
                defaultDirectory = RubyUtils.GetHomeDirectory(pal);
            }
            if (defaultDirectory == null) {
                throw RubyExceptions.CreateArgumentError("HOME/LOGDIR not set");
            }

            return ChangeDirectory(self.Context, defaultDirectory, self.Context.EncodePath(defaultDirectory), block);
        }

        private static object ChangeDirectory(RubyContext/*!*/ context, string/*!*/ strDir, MutableString/*!*/ dir, BlockParam block) {
            var pal = context.Platform;
            if (!ChdirAloneBlock(context, block != null)) {
                SetCurrentDirectory(pal, strDir);
                return 0;
            }

            // The block is this thread's from here on (ChdirAloneBlock entered it), so a chdir in
            // another thread fails until the directory is back, or until changing it has failed.
            string current;
            try {
                current = pal.CurrentDirectory;
                SetCurrentDirectory(pal, strDir);
            } catch {
                ChdirLeave(context);
                throw;
            }
            try {
                object result;
                block.Yield(dir, out result);
                return result;
            } finally {
                try {
                    SetCurrentDirectory(pal, current);
                } finally {
                    ChdirLeave(context);
                }
            }
        }

        #region chdir blocks

        // MRI's chdir_lock: the working directory is the process's, so while one thread is in a
        // chdir block another thread's chdir is an error, and a plain chdir inside the block is
        // a warning. The process is shared by every runtime in it, but a runtime only knows its
        // own threads, so each keeps its own.
        private sealed class ChdirLock {
            public readonly object Sync = new object();
            public System.Threading.Thread Thread;
            public int Blocking;
            public string Path;
            public int Line;
        }

        private static ChdirLock/*!*/ GetChdirLock(RubyContext/*!*/ context) {
            return (ChdirLock)context.GetOrCreateLibraryData(typeof(ChdirLock), () => new ChdirLock());
        }

        /// <summary>
        /// MRI's chdir_alone_block_p: whether this chdir is a block of its own, after refusing it
        /// in a thread other than the one whose block is running, and warning about a chdir
        /// without a block inside one. A block is entered (MRI's chdir_enter) here, under the
        /// same lock as the check: MRI enters it once the directory has changed, but with no GVL
        /// two threads could then both pass the check and both change directory. The caller
        /// leaves it (ChdirLeave) once the directory is restored, or if changing it fails.
        /// </summary>
        private static bool ChdirAloneBlock(RubyContext/*!*/ context, bool blockGiven) {
            var chdirLock = GetChdirLock(context);
            string path = null;
            int line = 0;
            lock (chdirLock.Sync) {
                if (chdirLock.Blocking == 0) {
                    if (!blockGiven) {
                        return false;
                    }
                    chdirLock.Blocking = 1;
                    chdirLock.Thread = System.Threading.Thread.CurrentThread;
                } else if (chdirLock.Thread != System.Threading.Thread.CurrentThread) {
                    throw RubyExceptions.CreateRuntimeError("conflicting chdir during another chdir block");
                } else if (blockGiven) {
                    chdirLock.Blocking++;
                    return true;
                } else {
                    path = chdirLock.Path;
                    line = chdirLock.Line;
                }
            }

            if (blockGiven) {
                // The outermost block's place is named in the warnings; finding it may walk the
                // stack, which is not done holding the lock. Only this thread reads it meanwhile.
                if (RubyUtils.TryGetCallerSourceLocation(context, out path, out line)) {
                    lock (chdirLock.Sync) {
                        chdirLock.Path = path;
                        chdirLock.Line = line;
                    }
                }
                return true;
            }

            context.ReportWarning(path != null
                ? String.Format("conflicting chdir during another chdir block\n{0}:{1}: note: previous chdir was here", path, line)
                : "conflicting chdir during another chdir block");
            return false;
        }

        private static void ChdirLeave(RubyContext/*!*/ context) {
            var chdirLock = GetChdirLock(context);
            lock (chdirLock.Sync) {
                if (--chdirLock.Blocking == 0) {
                    chdirLock.Thread = null;
                    chdirLock.Path = null;
                    chdirLock.Line = 0;
                }
            }
        }

        #endregion

        // On Windows these four are MRI's rb_f_notimplement stubs (no chroot, fchdir, dirfd or
        // fdopendir there): NotImplementedError, and #respond_to? denies them.
        private static Exception/*!*/ Unimplemented(string/*!*/ function) {
            return new NotImplementedError(function + "() function is unimplemented on this machine");
        }

        [RubyMethod("chroot", RubyMethodAttributes.PublicSingleton)]
        [RubyNotImplemented(OnWindowsOnly = true)]
        public static int ChangeRoot(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object dir) {
            if (!Posix.IsAvailable) {
                throw Unimplemented("chroot");
            }
            var path = Protocols.CastToPath(toPath, dir);
            string strPath = self.Context.DecodePath(path);

            int errno;
            if (Posix.ChRoot(strPath, out errno) != 0) {
                throw Posix.Error(errno, strPath, self.Context);
            }
            return 0;
        }

        /// <summary>
        /// fchdir(2): chdir to whatever directory a descriptor names. With a block the change
        /// lasts for the block only, and the block's value is the result.
        /// </summary>
        [RubyMethod("fchdir", RubyMethodAttributes.PublicSingleton)]
        [RubyNotImplemented(OnWindowsOnly = true)]
        public static object ChangeDirectoryToDescriptor(BlockParam block, RubyClass/*!*/ self, [DefaultProtocol]int fd) {
            if (!Posix.IsAvailable) {
                throw Unimplemented("fchdir");
            }

            // With a block, the block is this thread's from here on (ChdirAloneBlock entered it),
            // until the directory is back or changing it has failed.
            ChdirAloneBlock(self.Context, block != null);

            var pal = self.Context.Platform;
            string current = null;
            int errno;
            // MRI keeps a descriptor of the directory it came from and fchdirs back to it, so the
            // block may even remove that directory without the way back failing.
            int savedFd = -1;
            try {
                if (block != null) {
                    current = pal.CurrentDirectory;
                    savedFd = Posix.Open(".", Posix.O_RDONLY | Posix.O_DIRECTORY | Posix.O_CLOEXEC, out errno);
                }

                if (Posix.FChDir(fd, out errno) != 0) {
                    throw SyscallError(errno, "fchdir");
                }

                // The process directory moved behind the PAL's back, which keeps its own idea of it.
                SyncCurrentDirectory(pal);
            } catch {
                if (block != null) {
                    if (savedFd >= 0) {
                        int closeErrno;
                        Posix.Close(savedFd, out closeErrno);
                    }
                    ChdirLeave(self.Context);
                }
                throw;
            }

            if (block == null) {
                return 0;
            }

            try {
                object result;
                block.Yield(out result);
                return result;
            } finally {
                try {
                    if (savedFd >= 0 && Posix.FChDir(savedFd, out errno) == 0) {
                        try {
                            SyncCurrentDirectory(pal);
                        } catch (IOException) {
                            // the directory we are back in no longer exists and has no path
                        }
                        Posix.Close(savedFd, out errno);
                    } else {
                        if (savedFd >= 0) {
                            Posix.Close(savedFd, out errno);
                        }
                        pal.CurrentDirectory = current;
                    }
                } finally {
                    ChdirLeave(self.Context);
                }
            }
        }

        [RubyMethod("exist?", RubyMethodAttributes.PublicSingleton)]
        // #exists? was deprecated in 2.1 and removed in 3.9.
        public static bool Exists(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return RubyFileOps.DirectoryExists(self.Context, Protocols.CastToPath(toPath, path));
        }

        [RubyMethod("delete", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("rmdir", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("unlink", RubyMethodAttributes.PublicSingleton)]
        public static int RemoveDirectory(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object dirname) {
            string strDir = self.Context.DecodePath(Protocols.CastToPath(toPath, dirname));
            if (Posix.IsAvailable) {
                // rmdir(2) itself, as File.delete calls unlink(2): .NET reports rmdir's EACCES, EPERM and
                // EROFS as a bare IOException, and ToRubyException's guess at what that meant -
                // Directory.Exists, which is false when the parent cannot be searched - said ENOENT for
                // a directory in a parent without permissions. The kernel's errno is what MRI raises.
                int errno;
                if (Posix.RmDir(strDir, out errno) != 0) {
                    throw Posix.Error(errno, strDir, self.Context);
                }
                return 0;
            }
            // rmdir(2) does not follow a symbolic link: on a link to a directory it is ENOTDIR,
            // where the BCL's delete removed the link.
            if (GetEntryKind(strDir, false) == EntryKind.NotDirectory) {
                throw CreateENOTDIR(strDir);
            }
            try {
                self.Context.Platform.DeleteDirectory(strDir, false);
            } catch (Exception ex) {
                throw ToRubyException(ex, strDir, DirectoryOperation.Delete);
            }
            return 0;
        }

        [RubyMethod("entries", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetEntries(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object dirname, [Keywords, Optional]IDictionary<object, object> options) {
            return new RubyDir(self, Protocols.CastToPath(toPath, dirname), GetEncodingOption(self.Context, options))
                .GetEntries(self.Context);
        }

        [RubyMethod("foreach", RubyMethodAttributes.PublicSingleton)]
        public static object ForEach(ConversionStorage<MutableString>/*!*/ toPath, BlockParam block, RubyClass/*!*/ self, object dirname,
            [Keywords, Optional]IDictionary<object, object> options) {
            return new RubyDir(self, Protocols.CastToPath(toPath, dirname), GetEncodingOption(self.Context, options))
                .EnumerateEntries(self.Context, block, null);
        }

        [RubyMethod("getwd", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("pwd", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ GetCurrentDirectory(RubyClass/*!*/ self) {
            return self.Context.EncodePath(RubyUtils.CanonicalizePath(self.Context.Platform.CurrentDirectory));
        }

        #region glob

        /// <summary>
        /// The `base:`, `sort:` and `flags:` options of Dir.glob / Dir.[].
        ///
        /// IronRuby has no real keyword arguments - they arrive as a trailing Hash - so the
        /// options are split off the argument array by shape, the same way KernelOps handles
        /// `raise ..., cause:`. That is why these methods take `params object[]` instead of
        /// declaring the pattern and flags as parameters: with a declared `int flags` the
        /// keyword Hash bound to it and every call with an option failed with
        /// "no implicit conversion of Hash into Integer".
        /// </summary>
        private sealed class GlobOptions {
            internal int Flags;
            internal MutableString Base;
            internal bool Sort = true;
        }

        private static GlobOptions/*!*/ ParseGlobOptions(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ toInt,
            ref object[]/*!*/ args, bool allowFlags) {

            var options = new GlobOptions();
            if (args.Length == 0) {
                return options;
            }

            var hash = args[args.Length - 1] as IDictionary<object, object>;
            if (hash == null) {
                return options;
            }

            bool flagsGiven = false;
            foreach (var entry in hash) {
                var key = (entry.Key as RubySymbol)?.ToString();
                switch (key) {
                    case "base":
                        if (entry.Value != null) {
                            options.Base = Protocols.CastToPath(toPath, entry.Value);
                        }
                        break;

                    case "sort":
                        if (entry.Value is bool) {
                            options.Sort = (bool)entry.Value;
                        } else {
                            throw RubyExceptions.CreateArgumentError("expected true or false as sort: {0}",
                                toPath.Context.Inspect(entry.Value));
                        }
                        break;

                    case "flags":
                        if (!allowFlags) {
                            goto default;
                        }
                        options.Flags = Protocols.CastToFixnum(toInt, entry.Value);
                        flagsGiven = true;
                        break;

                    default:
                        throw RubyExceptions.CreateArgumentError("unknown keyword: :{0}", key ?? toPath.Context.Inspect(entry.Key).ToString());
                }
            }

            var rest = new object[args.Length - 1];
            Array.Copy(args, rest, rest.Length);
            args = rest;

            // A `flags:` keyword wins outright: MRI does not even convert the positional
            // second argument then, so Dir.glob('**', :ignored, flags: ...) is legal.
            if (flagsGiven && args.Length == 2) {
                var head = new object[1];
                head[0] = args[0];
                args = head;
            }
            return options;
        }

        /// <summary>
        /// Converts one pattern and rejects the NUL separated form, which MRI dropped:
        /// Dir.glob("a\0b") is an ArgumentError, not a two pattern glob.
        /// </summary>
        private static MutableString/*!*/ ToGlobPattern(ConversionStorage<MutableString>/*!*/ toPath, object pattern, bool single) {
            try {
                return Protocols.CastToPath(toPath, pattern);
            } catch (ArgumentException e) when (single && e.Message == "path name contains null byte") {
                // A lone glob pattern reports an embedded NUL in its own words; one of several
                // patterns (an Array, or Dir.[] with more arguments) is an ordinary path name.
                throw RubyExceptions.CreateArgumentError("nul-separated glob pattern is deprecated");
            }
        }

        private static IEnumerable<MutableString>/*!*/ GlobMatches(ConversionStorage<MutableString>/*!*/ toPath, RubyContext/*!*/ context,
            object pattern, GlobOptions/*!*/ options) {

            var patterns = pattern as IList<object>;
            if (patterns == null) {
                return IronRuby.Builtins.Glob.GetMatches(context, ToGlobPattern(toPath, pattern, true), options.Flags, options.Base, options.Sort);
            }

            // Dir.glob (but not Dir.[]) also accepts an Array of patterns; the results are
            // concatenated in pattern order.
            var all = new List<MutableString>();
            // A pattern's #to_path may change the Array under us, so the length is read anew
            // for every element, as MRI's dir_globs does.
            for (int i = 0; i < patterns.Count; i++) {
                var p = ToGlobPattern(toPath, patterns[i], false);
                all.AddRange(IronRuby.Builtins.Glob.GetMatches(context, p, options.Flags, options.Base, options.Sort));
            }
            return all;
        }

        [RubyMethod("glob", RubyMethodAttributes.PublicSingleton)]
        public static object Glob(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ toInt, BlockParam block,
            RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {

            var options = ParseGlobOptions(toPath, toInt, ref args, true);
            if (args.Length < 1 || args.Length > 2) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..2)", args.Length);
            }
            if (args.Length == 2) {
                options.Flags = Protocols.CastToFixnum(toInt, args[1]);
            }

            var matches = GlobMatches(toPath, self.Context, args[0], options);
            if (block != null) {
                foreach (var fileName in matches) {
                    object result;
                    if (block.Yield(fileName, out result)) {
                        return result;
                    }
                }
                return null;
            }

            RubyArray array = new RubyArray();
            foreach (var fileName in matches) {
                array.Add(fileName);
            }
            return array;
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Glob(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ toInt,
            RubyClass/*!*/ self, [NotNull]params object[]/*!*/ args) {

            // Dir.[] takes any number of patterns but no flags, positional or keyword.
            var options = ParseGlobOptions(toPath, toInt, ref args, false);

            RubyArray result = new RubyArray();
            foreach (var pattern in args) {
                foreach (var fileName in IronRuby.Builtins.Glob.GetMatches(self.Context,
                    ToGlobPattern(toPath, pattern, args.Length == 1), options.Flags, options.Base, options.Sort)) {
                    result.Add(fileName);
                }
            }

            return result;
        }

        #endregion

        [RubyMethod("mkdir", RubyMethodAttributes.PublicSingleton)]
        public static int MakeDirectory(ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ fixnumCast,
            RubyClass/*!*/ self, object dirname, [Optional]object permissions) {
            var platform = self.Context.Platform;

            string strDir = self.Context.DecodePath(Protocols.CastToPath(toPath, dirname));
            if (platform.FileExists(strDir) || platform.DirectoryExists(strDir)) {
                throw RubyExceptions.CreateEEXIST(strDir);
            }

            string containingDir = platform.GetDirectoryName(strDir);
            if (!String.IsNullOrEmpty(containingDir) && RunsThroughFile(containingDir)) {
                // mkdir(2) of "file/sub" is ENOTDIR, not a missing parent
                throw CreateENOTDIR(strDir);
            }
            if (!String.IsNullOrEmpty(containingDir) && !platform.DirectoryExists(containingDir)) {
                // mkdir(2) reports the path it was given, not the missing parent
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strDir);
            }
                
            // The permission argument is not decoration: Dir.mkdir(path, 01755) has to
            // reach mkdir(2) for the setuid/setgid/sticky bits to survive.
            if (permissions != Missing.Value && permissions != null && Posix.IsAvailable) {
                int mode = Protocols.CastToFixnum(fixnumCast, permissions);
                int errno;
                if (Posix.MkDir(strDir, mode, out errno) != 0) {
                    throw Posix.Error(errno, strDir, self.Context);
                }
                return 0;
            }

            try {
                platform.CreateDirectory(strDir);
            } catch (Exception ex) {
                throw ToRubyException(ex, strDir, DirectoryOperation.Create);
            }
            return 0;
        }

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static object Open(ConversionStorage<MutableString>/*!*/ toPath, BlockParam block, RubyClass/*!*/ self, object dirname,
            [Keywords, Optional]IDictionary<object, object> options) {
            RubyDir rd = new RubyDir(self, Protocols.CastToPath(toPath, dirname), GetEncodingOption(self.Context, options));

            try {
                object result;
                block.Yield(rd, out result);
                return result;
            } finally {
                Close(rd);
            }
        }

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RubyDir/*!*/ Open(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object dirname,
            [Keywords, Optional]IDictionary<object, object> options) {
            return new RubyDir(self, Protocols.CastToPath(toPath, dirname), GetEncodingOption(self.Context, options));
        }

        #endregion

        #region Public Instance Methods

        [RubyMethod("close")]
        public static void Close(RubyDir/*!*/ self) {
            // MRI 3.x: closing a closed Dir is a no-op, not an IOError.
            self.Close();
        }

        /// <summary>
        /// The descriptor MRI's DIR* holds. Nothing here needs one to read the directory, so it
        /// is opened the first time it is asked for and closed with the Dir.
        /// </summary>
        [RubyMethod("fileno")]
        [RubyNotImplemented(OnWindowsOnly = true)]
        public static int FileNo(RubyDir/*!*/ self) {
            self.ThrowIfClosed();
            return self.GetFileDescriptor();
        }

        [RubyMethod("for_fd", RubyMethodAttributes.PublicSingleton)]
        [RubyNotImplemented(OnWindowsOnly = true)]
        public static RubyDir/*!*/ ForFileDescriptor(RubyClass/*!*/ self, [DefaultProtocol]int fd) {
            if (!Posix.IsAvailable) {
                throw Unimplemented("for_fd");
            }

            // There is no fdopendir here, so the descriptor is turned back into the path it
            // names and the directory is read the ordinary way. The descriptor itself is kept,
            // so that #fileno answers it and #close closes it - including the second close of
            // a descriptor two Dirs share, which is the EBADF MRI reports.
            int errno;
            string path = Posix.ReadLink("/proc/self/fd/" + fd, out errno);
            if (path == null) {
                throw SyscallError(errno == 0 ? Posix.EBADF : errno, "fdopendir");
            }

            var result = new RubyDir(self, self.Context.EncodePath(path));
            result._fd = fd;
            return result;
        }

        [RubyMethod("each")]
        public static object Each(RubyContext/*!*/ context, BlockParam block, RubyDir/*!*/ self) {
            return self.EnumerateEntries(context, block, self);
        }

        [RubyMethod("to_path")]
        [RubyMethod("path")]
        public static MutableString GetPath(RubyContext/*!*/ context, RubyDir/*!*/ self) {
            return (self._dirName != null) ? self._dirName.Clone() : null;
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyDir/*!*/ self) {
            // MRI: #<Dir:/the/path>
            var result = MutableString.CreateMutable(context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(context.GetClassDisplayName(self));
            result.Append(':');
            if (self._dirName != null) {
                result.Append(self._dirName);
            }
            result.Append('>');
            return RubyUtils.BinaryIfAscii(result);
        }

        [RubyMethod("pos")]
        [RubyMethod("tell")]
        public static int GetCurrentPosition(RubyDir/*!*/ self) {
            self.ThrowIfClosed();

            return self._pos + 2;
        }

        /// <summary>
        /// Synonym for Dir#seek, but returns the position parameter
        /// </summary>
        [RubyMethod("pos=")]
        public static int SetPosition(RubyDir/*!*/ self, int pos) {
            self.ThrowIfClosed();

            self._pos = pos - 2;
            return pos;
        }

        [RubyMethod("read")]
        public static MutableString Read(RubyContext/*!*/ context, RubyDir/*!*/ self) {
            self.ThrowIfClosed();

            if (self._pos + 1 > self._rawEntries.Length) {
                return null;
            }

            MutableString ret;
            if (self._pos == -2) {
                ret = self.Label(context, context.EncodePath("."));
            } else if (self._pos == -1) {
                ret = self.Label(context, context.EncodePath(".."));
            } else {
                string name = context.Platform.GetFileName(self._rawEntries[self._pos]);
                MutableString encoded = context.TryEncodePath(name);
                if (encoded != null) {
                    ret = self.Label(context, encoded);
                } else {
                    // MRI does not fail on a name its encodings cannot convert: it hands back the
                    // bytes as they are, labelled with the file system encoding.
                    ret = MutableString.CreateBinary(System.Text.Encoding.UTF8.GetBytes(name), self._encoding ?? context.GetPathEncoding());
                }
            }
            self._pos++;
            return ret;
        }

        [RubyMethod("rewind")]
        public static RubyDir/*!*/ Rewind(RubyDir/*!*/ self) {
            self.ThrowIfClosed();

            self._pos = -2;
            return self;
        }

        [RubyMethod("seek")]
        public static RubyDir/*!*/ Seek(RubyDir/*!*/ self, int pos) {
            self.ThrowIfClosed();

            if (pos < 0) {
                self._pos = -2;
            } else if (pos > self._rawEntries.Length + 2) {
                self._pos = self._rawEntries.Length;
            } else {
                self._pos = pos - 2;
            }
            return self;
        }

        #endregion

        #region Helpers

        internal PlatformAdaptationLayer/*!*/ Platform {
            get { return ImmediateClass.Context.Platform; }
        }

        // MRI keeps answering #path after #close, so closed-ness is tracked by the
        // entry list rather than by dropping the name.
        private bool Closed {
            get { return _rawEntries == null; } 
        }

        private void Close() {
            _rawEntries = null;

            int fd = _fd;
            _fd = -1;
            if (fd >= 0) {
                int errno;
                if (Posix.Close(fd, out errno) != 0) {
                    throw SyscallError(errno, "closedir");
                }
            }
        }

        private int GetFileDescriptor() {
            if (_fd >= 0) {
                return _fd;
            }
            if (!Posix.IsAvailable) {
                throw Unimplemented("fileno");
            }

            string path = ImmediateClass.Context.DecodePath(_dirName);
            int errno;
            // Close-on-exec is the default for every descriptor Ruby opens.
            int fd = Posix.Open(path, Posix.O_RDONLY | Posix.O_DIRECTORY | Posix.O_CLOEXEC, out errno);
            if (fd < 0) {
                throw Posix.Error(errno, path, ImmediateClass.Context);
            }
            _fd = fd;
            return fd;
        }

        /// <summary>
        /// Posix.Error names the path a call failed on; a descriptor-taking call has no path,
        /// and MRI names the system call instead.
        /// </summary>
        private static Exception/*!*/ SyscallError(int errno, string/*!*/ syscall) {
            if (errno == Posix.EBADF) {
                // The exception is rebuilt through Errno::EBADF.new when it crosses into
                // Ruby, and that is what prefixes "Bad file descriptor - ", so what is
                // recorded here is the suffix on its own.
                return (Exception)RubyExceptionData.InitializeException(
                    new BadFileDescriptorError(), MutableString.CreateAscii(syscall));
            }
            return Posix.Error(errno, syscall);
        }

        /// <summary>
        /// The PAL keeps its own current directory, so a chdir done behind its back - fchdir(2),
        /// here - has to be read back out of the process.
        /// </summary>
        private static void SyncCurrentDirectory(PlatformAdaptationLayer/*!*/ pal) {
            pal.CurrentDirectory = Directory.GetCurrentDirectory();
        }

        private void ThrowIfClosed() {
            if (Closed) {
                throw RubyExceptions.CreateIOError("closed directory");
            }
        }

        private enum DirectoryOperation {
            Delete,
            Create,
            Open,
            ChangeDir,
        }

        private RubyArray/*!*/ GetEntries(RubyContext/*!*/ context) {
            ThrowIfClosed();

            RubyArray ret = new RubyArray(_rawEntries.Length + 2);
            ret.Add(Label(context, context.EncodePath(".")));
            ret.Add(Label(context, context.EncodePath("..")));
            foreach (string entry in _rawEntries) {
                var encoded = context.TryEncodePath(context.Platform.GetFileName(entry));
                if (encoded != null) {
                    ret.Add(Label(context, encoded));
                }
            }
            return ret;
        }

        /// <summary>
        /// A name as the caller asked to see it: labelled with the `encoding:' option if one was
        /// given, and otherwise transcoded into the default internal encoding when there is one -
        /// which is what MRI hands back for every other string that comes in from outside.
        /// </summary>
        private MutableString/*!*/ Label(RubyContext/*!*/ context, MutableString/*!*/ name) {
            if (_encoding != null) {
                name.ForceEncoding(_encoding);
                return name;
            }

            // A name the internal encoding has no characters for stays as it is, as MRI's
            // rb_str_conv_enc leaves a string it cannot convert: _<U+1F60E>.erb under EUC-KR.
            var internalEncoding = context.DefaultInternalEncoding;
            if (internalEncoding != null && internalEncoding != name.Encoding) {
                string text = name.ConvertToString();
                try {
                    internalEncoding.StrictEncoding.GetByteCount(text);
                } catch (System.Text.EncoderFallbackException) {
                    return name;
                }
                return MutableString.Create(text, internalEncoding);
            }

            return name;
        }

        private object EnumerateEntries(RubyContext/*!*/ context, BlockParam block, object defaultResult) {
            ThrowIfClosed();
            _pos = -2;

            foreach (object entry in GetEntries(context)) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                _pos++;

                object blockResult;
                if (block.Yield(entry, out blockResult)) {
                    return blockResult;
                }
            }

            return defaultResult;
        }

        /// <summary>
        /// True if <paramref name="path"/> or one of its parents exists and is not a directory:
        /// chdir(2), opendir(3), mkdir(2) and rmdir(2) all fail with ENOTDIR on "file" and on
        /// "file/sub", where the BCL reports both as a directory that is not there.
        ///
        /// Each component is looked at as the kernel resolves it, through symbolic links - a
        /// dangling link is a missing directory (ENOENT), not a file - except that rmdir(2) does
        /// not follow the last one (<paramref name="followLast"/> false): there a link is ENOTDIR,
        /// whatever it points at. A component that cannot be stat'd (missing, or EACCES) is not a
        /// file, so the caller's ENOENT or EACCES stands.
        /// </summary>
        private static bool RunsThroughFile(string path, bool followLast = true) {
            try {
                bool last = true;
                for (string p = path; !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p), last = false) {
                    switch (GetEntryKind(p, !last || followLast)) {
                        case EntryKind.NotDirectory: return true;
                        case EntryKind.Directory: return false;
                    }
                }
            } catch (Exception e) when (e is ArgumentException || e is PathTooLongException || e is NotSupportedException) {
                // a path the BCL cannot take apart is not one we can say anything about
            }
            return false;
        }

        private enum EntryKind { None, Directory, NotDirectory }

        private static EntryKind GetEntryKind(string/*!*/ path, bool followLinks) {
            if (Posix.IsAvailable) {
                Posix.StatData data;
                int errno;
                if (Posix.TryStat(path, followLinks, out data, out errno)) {
                    return data.FileType == Posix.S_IFDIR ? EntryKind.Directory : EntryKind.NotDirectory;
                }
                if (errno != 0) {
                    return EntryKind.None;
                }
                // no statx: fall back to the BCL
            }
            // File.Exists is also true for a dangling link on Unix; Windows has no fstatat to ask.
            return Directory.Exists(path) ? EntryKind.Directory : File.Exists(path) ? EntryKind.NotDirectory : EntryKind.None;
        }

        private static Exception/*!*/ CreateENOTDIR(string path) {
            // Errno::ENOTDIR is backed by DirectoryNotFoundException
            return new DirectoryNotFoundException("Not a directory - " + path);
        }

        // Maps the BCL's exception to the Errno MRI raises for the same call. The messages leave
        // out MRI's " @ function" (e.g. "@ dir_s_rmdir"), as the rest of IronRuby's Errno
        // messages do.
        private static Exception ToRubyException(Exception/*!*/ ex, string path, DirectoryOperation op) {
            Assert.NotNull(ex);
            
            Type exceptionType = ex.GetType();

            switch (op) {
                case DirectoryOperation.ChangeDir:
                    if (RunsThroughFile(path)) {
                        return CreateENOTDIR(path);
                    }
                    // .NET reports a missing directory as DirectoryNotFoundException, which
                    // IronRuby's Errno table maps to ENOTDIR; MRI's chdir says ENOENT.
                    if (ex is DirectoryNotFoundException || ex is FileNotFoundException) {
                        return RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
                    }
                    if (ex is UnauthorizedAccessException) {
                        break;
                    }
                    return RubyExceptions.CreateEINVAL(path);

                case DirectoryOperation.Open:
                    if (RunsThroughFile(path)) {
                        return CreateENOTDIR(path);
                    }
                    if (ex is UnauthorizedAccessException) {
                        break;
                    }
                    return RubyExceptions.CreateENOENT("No such file or directory - {0}", path);

                case DirectoryOperation.Delete:
                    if (ex is ArgumentException) {
                        return RubyExceptions.CreateEINVAL(path);
                    }
                    // DirectoryNotFoundException derives from IOException, so every rmdir
                    // failure used to collapse into EACCES. rmdir(2) distinguishes them and
                    // so does MRI; the filesystem is the only thing that can tell them apart.
                    if (ex is IOException) {
                        if (RunsThroughFile(path, false)) {
                            return CreateENOTDIR(path);
                        }
                        if (!Directory.Exists(path)) {
                            return RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
                        }
                        return new Errno.DirectoryNotEmptyError(path);
                    }
                    break;

                case DirectoryOperation.Create:
                    if (ex is ArgumentException) {
                        return RubyExceptions.CreateEINVAL(path);
                    }
                    // A name too long for the file system is not one that exists: mkdir(2) says
                    // ENAMETOOLONG, win32.c maps ERROR_FILENAME_EXCED_RANGE to ENOENT.
                    if (ex is PathTooLongException) {
                        return Path.DirectorySeparatorChar == '/'
                            ? new Errno.NameTooLongError(path)
                            : RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
                    }
                    if (ex is IOException) {
                        return RubyExceptions.CreateEEXIST(path);
                    }
                    break;
            }

            if (ex is UnauthorizedAccessException) {
                return Errno.CreateEACCES(path, ex);
            }

            // throw anyway
            return RubyExceptions.CreateSystemCallError("unknown scenario - {0}, {1}, {2}", exceptionType, path, op);
        }

        #endregion
    }
}
