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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using IronRuby.Compiler;
using System.Globalization;
using IronRuby.Runtime.Conversions;
using System.Runtime.CompilerServices;
using System.Reflection;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    /// <summary>
    /// File builtin class. Derives from IO
    /// </summary>
    [RubyClass("File", Extends = typeof(RubyFile))]
    public static class RubyFileOps {
        internal static bool FileExists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            return context.Platform.FileExists(context.DecodePath(path));
        }

        internal static bool DirectoryExists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            return context.Platform.DirectoryExists(context.DecodePath(path));
        }

        internal static bool Exists(RubyContext/*!*/ context, MutableString/*!*/ path) {
            var strPath = context.DecodePath(path);
            return context.Platform.DirectoryExists(strPath) || context.Platform.FileExists(strPath);
        }

        #region Construction

        [RubyConstructor]
        public static RubyFile/*!*/ CreateFile(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toPath,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self,
            object descriptorOrPath, 
            [Optional]object optionsOrMode, 
            [Optional]object optionsOrPermissions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            return Reinitialize(toInt, toHash, toPath, toStr, new RubyFile(self.Context), descriptorOrPath, optionsOrMode, optionsOrPermissions, options);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyFile/*!*/ Reinitialize(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toPath,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyFile/*!*/ self,
            object descriptorOrPath, 
            [Optional]object optionsOrMode, 
            [Optional]object optionsOrPermissions,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            var context = self.Context;
            
            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrMode, ref optionsOrPermissions);
            var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(toInt.Context));

            IOInfo info = new IOInfo();
            if (optionsOrMode != Missing.Value) {
                int? m = toIntSite.Target(toIntSite, optionsOrMode);
                info = m.HasValue ? new IOInfo((IOMode)m) : IOInfo.Parse(context, Protocols.CastToString(toStr, optionsOrMode));
            }

            int permissions = 0;
            if (optionsOrPermissions != Missing.Value) {
                int? p = toIntSite.Target(toIntSite, optionsOrPermissions);
                if (!p.HasValue) {
                    throw RubyExceptions.CreateTypeConversionError(context.GetClassName(optionsOrPermissions), "Integer");
                }
                permissions = p.Value;
            }

            if (options != null) {
                info = info.AddOptions(toStr, options);
            }

            // TODO: permissions
            
            // descriptor or path:
            int? descriptor = toIntSite.Target(toIntSite, descriptorOrPath);
            if (descriptor.HasValue) {
                RubyIOOps.Reinitialize(self, descriptor.Value, info);
            } else {
                Reinitialize(self, Protocols.CastToPath(toPath, descriptorOrPath), info, permissions);
            }

            return self;
        }

        private static void Reinitialize(RubyFile/*!*/ file, MutableString/*!*/ path, IOInfo info, int permission) {
            var strPath = file.Context.DecodePath(path);
            var stream = RubyFile.OpenFileStream(file.Context, strPath, info.Mode);

            file.Path = strPath;
            file.Mode = info.Mode;
            file.SetStream(stream);
            file.SetFileDescriptor(file.Context.AllocateFileDescriptor(stream));

            if (info.HasEncoding) {
                file.ExternalEncoding = info.ExternalEncoding;
                file.InternalEncoding = info.InternalEncoding;
            }
        }
        
        #endregion

        #region Declared Constants

        static RubyFileOps() {
            ALT_SEPARATOR = MutableString.CreateAscii(AltDirectorySeparatorChar.ToString()).Freeze();
            SEPARATOR = MutableString.CreateAscii(DirectorySeparatorChar.ToString()).Freeze();
            Separator = SEPARATOR;
            PATH_SEPARATOR = MutableString.CreateAscii(PathSeparatorChar.ToString()).Freeze();
        }

        private const char AltDirectorySeparatorChar = '\\';
        private const char DirectorySeparatorChar = '/';
        private const char PathSeparatorChar = ';';

        internal static bool IsDirectorySeparator(int c) {
            return c == DirectorySeparatorChar || c == AltDirectorySeparatorChar;
        }

        [RubyConstant]
        public readonly static MutableString ALT_SEPARATOR;

        [RubyConstant]
        public readonly static MutableString PATH_SEPARATOR;

        [RubyConstant]
        public readonly static MutableString SEPARATOR;

        [RubyConstant]
        public readonly static MutableString Separator = SEPARATOR;

        private const string NUL_VALUE = "NUL";

        [RubyModule("Constants")]
        public static class Constants {
            [RubyConstant]
            public readonly static int APPEND = (int)IOMode.WriteAppends;
            [RubyConstant]
            public readonly static int BINARY = (int)IOMode.PreserveEndOfLines;
            [RubyConstant]
            public readonly static int CREAT = (int)IOMode.CreateIfNotExists;
            [RubyConstant]
            public readonly static int EXCL = (int)IOMode.ErrorIfExists;
            [RubyConstant]
            public readonly static int FNM_CASEFOLD = 0x08;
            [RubyConstant]
            public readonly static int FNM_DOTMATCH = 0x04;
            [RubyConstant]
            public readonly static int FNM_NOESCAPE = 0x01;
            [RubyConstant]
            public readonly static int FNM_PATHNAME = 0x02;
            [RubyConstant]
            public readonly static int FNM_SYSCASE = 0x08;
            [RubyConstant]
            public readonly static int LOCK_EX = 0x02;
            [RubyConstant]
            public readonly static int LOCK_NB = 0x04;
            [RubyConstant]
            public readonly static int LOCK_SH = 0x01;
            [RubyConstant]
            public readonly static int LOCK_UN = 0x08;
            [RubyConstant]
            public readonly static int NONBLOCK = (int)IOMode.WriteOnly;
            [RubyConstant]
            public readonly static int RDONLY = (int)IOMode.ReadOnly;
            [RubyConstant]
            public readonly static int RDWR = (int)IOMode.ReadWrite;
            [RubyConstant]
            public readonly static int TRUNC = (int)IOMode.Truncate;
            [RubyConstant]
            public readonly static int WRONLY = (int)IOMode.WriteOnly;
        }

        #endregion

        internal const int WriteModeMask = 0x80; // Oct 0200
        internal const int ReadWriteMode = 0x1B6; // Oct 0666

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return RubyIOOps.Open();
        }

        #region chmod, chown, lchmod, lchown, umask

        [RubyMethod("chmod")]
        public static int Chmod(RubyFile/*!*/ self, [DefaultProtocol]int permission) {
            self.RequireInitialized();
            if (Posix.IsAvailable) {
                int fd = GetNativeFileDescriptor(self);
                int errno;
                if (fd >= 0) {
                    if (Posix.FChmod(fd, permission, out errno) != 0) {
                        throw Posix.Error(errno, self.Path);
                    }
                    return 0;
                }
            }
            if (self.Path == null) {
                throw new NotSupportedException("TODO: cannot chmod for files without path");
            }
            Chmod(self.Path, permission);
            return 0;
        }

        [RubyMethod("chmod", RubyMethodAttributes.PublicSingleton)]
        public static int Chmod(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, [DefaultProtocol]int permission, params object[]/*!*/ paths) {
            foreach (var path in paths) {
                Chmod(self.Context.DecodePath(Protocols.CastToPath(toPath, path)), permission);
            }
            return paths.Length;
        }

        [RubyMethod("lchmod", RubyMethodAttributes.PublicSingleton)]
        public static int LChmod(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, [DefaultProtocol]int permission, params object[]/*!*/ paths) {
            // Linux has no lchmod(2): the permission bits of a symbolic link are
            // meaningless there, so there is nothing correct to do.
            throw new IronRuby.Builtins.NotImplementedError("lchmod() function is unimplemented on this machine");
        }

        internal static void Chmod(string/*!*/ path, int permission) {
#if FEATURE_FILESYSTEM
            if (Posix.IsAvailable) {
                int errno;
                if (Posix.Chmod(path, permission, out errno) != 0) {
                    throw Posix.Error(errno, path);
                }
                return;
            }
            FileAttributes oldAttributes = File.GetAttributes(path);
            if ((permission & WriteModeMask) == 0) {
                File.SetAttributes(path, oldAttributes | FileAttributes.ReadOnly);
            } else {
                File.SetAttributes(path, oldAttributes & ~FileAttributes.ReadOnly);
            }
#endif
        }

        private static int ToUidGid(RubyContext/*!*/ context, object value, bool owner) {
            if (value == null) {
                return -1;
            }
            if (value is int) {
                return (int)value;
            }
            throw RubyExceptions.CreateUnexpectedTypeError(context, value, "Integer");
        }

        [RubyMethod("chown")]
        public static int ChangeOwner(RubyContext/*!*/ context, RubyFile/*!*/ self, object owner, object group) {
            self.RequireInitialized();
            int uid = ToUidGid(context, owner, true);
            int gid = ToUidGid(context, group, false);

            if (Posix.IsAvailable) {
                int fd = GetNativeFileDescriptor(self);
                int errno;
                if (fd >= 0) {
                    if (Posix.FChown(fd, uid, gid, out errno) != 0) {
                        throw Posix.Error(errno, self.Path);
                    }
                }
            }
            return 0;
        }

        [RubyMethod("chown", RubyMethodAttributes.PublicSingleton)]
        public static int ChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            params object[]/*!*/ paths) {

            return ChangeOwner(toPath, self, owner, group, paths, true);
        }

        [RubyMethod("lchown", RubyMethodAttributes.PublicSingleton)]
        public static int LChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            params object[]/*!*/ paths) {

            return ChangeOwner(toPath, self, owner, group, paths, false);
        }

        private static int ChangeOwner(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object owner, object group,
            object[]/*!*/ paths, bool followLinks) {

            var context = self.Context;
            int uid = ToUidGid(context, owner, true);
            int gid = ToUidGid(context, group, false);

            foreach (var path in paths) {
                string strPath = context.DecodePath(Protocols.CastToPath(toPath, path));
                if (Posix.IsAvailable) {
                    int errno;
                    int rc = followLinks ? Posix.Chown(strPath, uid, gid, out errno) : Posix.LChown(strPath, uid, gid, out errno);
                    if (rc != 0) {
                        throw Posix.Error(errno, strPath);
                    }
                } else if (!Exists(context, Protocols.CastToPath(toPath, path))) {
                    throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strPath);
                }
            }
            return paths.Length;
        }

        internal static readonly object UmaskKey = new object();

        private static bool IsUnixPlatform {
            get { return System.IO.Path.DirectorySeparatorChar == '/'; }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")]
        private static extern int NativeUmask(int mask);

        [RubyMethod("umask", RubyMethodAttributes.PublicSingleton)]
        public static int GetUmask(RubyClass/*!*/ self, [DefaultProtocol]int mask) {
            if (IsUnixPlatform) {
                return NativeUmask(mask);
            }
            int result = (int)self.Context.GetOrCreateLibraryData(UmaskKey, () => 0);
            self.Context.TrySetLibraryData(UmaskKey, CalculateUmask(mask));
            return result;
        }

        [RubyMethod("umask", RubyMethodAttributes.PublicSingleton)]
        public static int GetUmask(RubyClass/*!*/ self) {
            if (IsUnixPlatform) {
                int current = NativeUmask(0);
                NativeUmask(current);
                return current;
            }
            return (int)self.Context.GetOrCreateLibraryData(UmaskKey, () => 0);
        }

        private static int CalculateUmask(int mask) {
            return (mask % 512) / 128 * 128;
        }
        
        #endregion

        #region delete, unlink, truncate, rename

        [RubyMethod("delete", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("unlink", RubyMethodAttributes.PublicSingleton)]
        public static int Delete(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            if (!self.Context.Platform.FileExists(strPath)) {
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strPath);
            }

            Delete(self.Context, strPath);     
            return 1;
        }

        internal static void Delete(RubyContext/*!*/ context, string/*!*/ path) {
            try {
                context.Platform.DeleteFile(path, true);
#if FEATURE_FILESYSTEM
            } catch (DirectoryNotFoundException) {
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
#endif
            } catch (IOException e) {
                throw Errno.CreateEACCES(e.Message, e);
            }
        }

        [RubyMethod("delete", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("unlink", RubyMethodAttributes.PublicSingleton)]
        public static int Delete(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, params object[] paths) {
            foreach (MutableString path in paths) {
                Delete(toPath, self, path);
            }

            return paths.Length;
        }

#if FEATURE_FILESYSTEM
        [RubyMethod("truncate", BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Truncate(RubyFile/*!*/ self, [DefaultProtocol]int size) {
            if (size < 0) {
                throw new InvalidError();
            }

            self.Length = size;
            return 0;
        }

        [RubyMethod("truncate", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Truncate(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path, [DefaultProtocol]int size) {
            if (size < 0) {
                throw new InvalidError();
            }
            using (RubyFile f = new RubyFile(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), IOMode.ReadWrite)) {
                f.Length = size;
            }
            return 0;
        }
#endif

        [RubyMethod("rename", RubyMethodAttributes.PublicSingleton)]
        public static int Rename(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            var context = self.Context;

            string strOldPath = context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNewPath = context.DecodePath(Protocols.CastToPath(toPath, newPath));

            if (strOldPath.Length == 0 || strNewPath.Length == 0) {
                throw RubyExceptions.CreateENOENT();
            }

            if (!context.Platform.FileExists(strOldPath) && !context.Platform.DirectoryExists(strOldPath)) {
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", oldPath);
            }

            if (RubyUtils.ExpandPath(context.Platform, strOldPath) == RubyUtils.ExpandPath(context.Platform, strNewPath)) {
                return 0;
            }

            if (context.Platform.FileExists(strNewPath)) {
                Delete(context, strNewPath);
            }

            try {
                context.Platform.MoveFileSystemEntry(strOldPath, strNewPath);
            } catch (IOException e) {
                throw Errno.CreateEACCES(e.Message, e);
            }

            return 0;
        }

        #endregion

        #region path, basename, dirname, extname, expand_path, absolute_path, fnmatch

        [RubyMethod("path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ ToPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return Protocols.CastToPath(toPath, path);
        }

        [RubyMethod("basename", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ BaseName(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self,
            object path, [DefaultProtocol, NotNull, Optional]MutableString suffix) {
            return BaseName(Protocols.CastToPath(toPath, path), suffix);
        }

        private static MutableString/*!*/ BaseName(MutableString/*!*/ path, MutableString suffix) {
            if (path.IsEmpty) {
                return path;
            }

            string strPath = path.ConvertToString();
            string[] parts = strPath.Split(new[] { DirectorySeparatorChar, AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) {
                return MutableString.CreateMutable(path.Encoding).Append((char)path.GetLastChar()).TaintBy(path);
            }

            bool isWindows = Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX;
            if (isWindows) {
                string first = parts[0];
                if (strPath.Length >= 2 && IsDirectorySeparator(strPath[0]) && IsDirectorySeparator(strPath[1])) {
                    // UNC: skip 2 parts 
                    if (parts.Length <= 2) {
                        return MutableString.CreateMutable(path.Encoding).Append(DirectorySeparatorChar).TaintBy(path);
                    }
                } else if (first.Length == 2 && Tokenizer.IsLetter(first[0]) && first[1] == ':') {
                    // skip drive letter "X:"
                    if (parts.Length <= 1) {
                        var result = MutableString.CreateMutable(path.Encoding).TaintBy(path);
                        if (strPath.Length > 2) {
                            result.Append(strPath[2]);
                        }
                        return result;
                    }
                }
            }

            string last = parts[parts.Length - 1];
            if (MutableString.IsNullOrEmpty(suffix)) {
                return MutableString.CreateMutable(last, path.Encoding);
            }

            StringComparison comparison = Environment.OSVersion.Platform == PlatformID.Unix ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int matchLength = last.Length;

            if (suffix != null) {
                string strSuffix = suffix.ToString();
                if (strSuffix.LastCharacter() == '*' && strSuffix.Length > 1) {
                    int suffixIdx = last.LastIndexOf(
                        strSuffix.Substring(0, strSuffix.Length - 1),
                        comparison
                    );
                    if (suffixIdx >= 0 && suffixIdx + strSuffix.Length <= last.Length) {
                        matchLength = suffixIdx;
                    }
                } else if (last.EndsWith(strSuffix, comparison)) {
                    matchLength = last.Length - strSuffix.Length;
                }
            }

            return MutableString.CreateMutable(path.Encoding).Append(last, 0, matchLength).TaintBy(path);
        }

        [RubyMethod("dirname", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ DirName(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return DirName(Protocols.CastToPath(toPath, path));
        }

        private static MutableString/*!*/ DirName(MutableString/*!*/ path) {
            string strPath = path.ConvertToString();
            string directoryName = strPath;

            if (IsValidPath(strPath)) {
                strPath = StripPathCharacters(strPath);

                // handle top-level UNC paths
                directoryName = Path.GetDirectoryName(strPath);
                if (directoryName == null) {
                    return MutableString.CreateMutable(strPath, path.Encoding);
                }

                string fileName = Path.GetFileName(strPath);
                if (!String.IsNullOrEmpty(fileName)) {
                    directoryName = StripPathCharacters(strPath.Substring(0, strPath.LastIndexOf(fileName, StringComparison.Ordinal)));
                }
            } else {
                if (directoryName.Length > 1) {
                    directoryName = "//";
                }
            }

            directoryName = String.IsNullOrEmpty(directoryName) ? "." : directoryName;
            return MutableString.CreateMutable(directoryName, path.Encoding);
        }

        private static bool IsValidPath(string path) {
            foreach (char c in path) {
                if (c != '/' && c != '\\') {
                    return true;
                }
            }
            return false;

        }

        private static string StripPathCharacters(string path) {
            int limit = 0;
            for (int charIndex = path.Length - 1; charIndex > 0; charIndex--) {
                if (!((path[charIndex] == '/') || (path[charIndex] == '\\')))
                    break;
                limit++;
            }
            if (limit > 0) {
                limit--;
                if (path.Length == 3 && path[1] == ':') limit--;
                return path.Substring(0, path.Length - limit - 1);
            }
            return path;
        }

        [RubyMethod("extname", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ GetExtension(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            MutableString pathStr = Protocols.CastToPath(toPath, path);
            return MutableString.Create(RubyUtils.GetExtension(pathStr.ConvertToString()), pathStr.Encoding).TaintBy(pathStr);
        }

        [RubyMethod("expand_path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ ExpandPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(null)]object basePath) {
            var context = self.Context;

            string result = RubyUtils.ExpandPath(
                context.Platform,
                context.DecodePath(Protocols.CastToPath(toPath, path)),
                (basePath == null) ? context.Platform.CurrentDirectory : context.DecodePath(Protocols.CastToPath(toPath, basePath)),
                true
            );

            return self.Context.EncodePath(result);
        }

        [RubyMethod("absolute_path", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ AbsolutePath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(null)]object basePath) {
            var context = self.Context;

            string result = RubyUtils.ExpandPath(
                context.Platform,
                context.DecodePath(Protocols.CastToPath(toPath, path)),
                (basePath == null) ? context.Platform.CurrentDirectory : context.DecodePath(Protocols.CastToPath(toPath, basePath)),
                false
            );

            return self.Context.EncodePath(result);
        }

        [RubyMethod("fnmatch", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("fnmatch?", RubyMethodAttributes.PublicSingleton)]
        public static bool FnMatch(ConversionStorage<MutableString>/*!*/ toPath, object/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ pattern, object path, [Optional]int flags) {

            return Glob.FnMatch(pattern.ConvertToString(), Protocols.CastToPath(toPath, path).ConvertToString(), flags);
        }

        #endregion

        #region split, join

        [RubyMethod("split", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Split(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            MutableString p = Protocols.CastToPath(toPath, path);
            RubyArray result = new RubyArray(2);
            result.Add(DirName(p));
            result.Add(BaseName(p, null));
            return result;
        }

        [RubyMethod("join", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Join(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, params object[]/*!*/ parts) {
            MutableString result = MutableString.CreateMutable(RubyEncoding.Binary);
            Dictionary<object, bool> visitedLists = null;
            var worklist = new Stack<object>();
            int current = 0;
            MutableString str;

            Push(worklist, parts);
            while (worklist.Count > 0) {
                object part = worklist.Pop();
                var list = part as IList;
                if (list != null) {
                    if (list.Count == 0) {
                        str = MutableString.FrozenEmpty;
                    } else if (visitedLists != null && visitedLists.ContainsKey(list)) {
                        str = RubyUtils.InfiniteRecursionMarker;
                    } else {
                        if (visitedLists == null) {
                            visitedLists = new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance);
                        }
                        visitedLists.Add(list, true);
                        Push(worklist, list);
                        continue;
                    }
                } else if (part == null) {
                    throw RubyExceptions.CreateTypeConversionError("NilClass", "String");
                } else {
                    str = Protocols.CastToPath(toPath, part);
                }

                if (current > 0) {
                    AppendDirectoryName(result, str);
                } else {
                    result.Append(str);
                }
                current++;
            }

            return result;
        }

        private static void Push(Stack<Object>/*!*/ stack, IList/*!*/ values) {
            for (int i = values.Count - 1; i >= 0; i--) {
                stack.Push(values[i]);
            }
        }

        private static void AppendDirectoryName(MutableString/*!*/ result, MutableString/*!*/ name) {
            int resultLength = result.GetCharCount();

            int i;
            for (i = resultLength - 1; i >= 0; i--) {
                if (!IsDirectorySeparator(result.GetChar(i))) {
                    break;
                }
            }

            if (i == resultLength - 1) {
                if (!IsDirectorySeparator(name.GetFirstChar())) {
                    result.Append(DirectorySeparatorChar);
                }
                result.Append(name);
            } else if (IsDirectorySeparator(name.GetFirstChar())) {
                result.Replace(i + 1, resultLength - i - 1, name);
            } else {
                result.Append(name);
            }
        }

        #endregion

        #region flock, readlink, link, symlink

#if FEATURE_FILESYSTEM
        /// <summary>
        /// IronRuby's "file descriptor" is an index into a per-context table, not
        /// an OS descriptor, so syscalls that need a real fd have to dig the
        /// handle out of the underlying FileStream.
        /// </summary>
        internal static int GetNativeFileDescriptor(RubyIO/*!*/ io) {
            var stream = io.GetStream().BaseStream;
            var fs = stream as FileStream;
            if (fs == null) {
                return -1;
            }
            return (int)fs.SafeFileHandle.DangerousGetHandle();
        }

        [RubyMethod("flock", BuildConfig = "FEATURE_FILESYSTEM")]
        public static object FileLock(RubyFile/*!*/ self, [DefaultProtocol]int operation) {
            self.RequireInitialized();
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("flock() function is unimplemented on this machine");
            }

            int fd = GetNativeFileDescriptor(self);
            if (fd < 0) {
                throw RubyExceptions.CreateEBADF();
            }

            int errno;
            if (Posix.Flock(fd, operation, out errno) != 0) {
                // LOCK_NB on a locked file reports "would block" rather than raising
                if (errno == 11 /*EWOULDBLOCK/EAGAIN*/) {
                    return false;
                }
                throw Posix.Error(errno, self.Path);
            }
            return 0;
        }

        [RubyMethod("readlink", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ Readlink(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("readlink() function is unimplemented on this machine");
            }

            int errno;
            string target = Posix.ReadLink(strPath, out errno);
            if (target == null) {
                throw Posix.Error(errno, strPath);
            }
            return self.Context.EncodePath(target);
        }

        [RubyMethod("link", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int Link(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            string strOld = self.Context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNew = self.Context.DecodePath(Protocols.CastToPath(toPath, newPath));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("link() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.Link(strOld, strNew, out errno) != 0) {
                throw Posix.Error(errno, errno == Posix.EEXIST ? strOld + " or " + strNew : strOld);
            }
            return 0;
        }

        [RubyMethod("symlink", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int SymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object oldPath, object newPath) {
            string strOld = self.Context.DecodePath(Protocols.CastToPath(toPath, oldPath));
            string strNew = self.Context.DecodePath(Protocols.CastToPath(toPath, newPath));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("symlink() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.Symlink(strOld, strNew, out errno) != 0) {
                throw Posix.Error(errno, errno == Posix.EEXIST ? strOld + " or " + strNew : strNew);
            }
            return 0;
        }

        [RubyMethod("mkfifo", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int MakeFifo(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultParameterValue(0666)]int mode) {

            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("mkfifo() function is unimplemented on this machine");
            }

            int errno;
            if (Posix.MkFifo(strPath, mode, out errno) != 0) {
                throw Posix.Error(errno, strPath);
            }
            return 0;
        }

        #region realpath, realdirpath

        /// <summary>
        /// Resolves ".", ".." and every symbolic link in <paramref name="path"/>.
        /// When <paramref name="strict"/> the whole path must exist (realpath);
        /// otherwise only everything but the last component must (realdirpath).
        /// </summary>
        private static string/*!*/ ResolvePath(RubyContext/*!*/ context, string/*!*/ path, string basedir, bool strict) {
            string absolute = RubyUtils.ExpandPath(context.Platform, path, basedir ?? context.Platform.CurrentDirectory, false);

            var components = new List<string>();
            foreach (var part in absolute.Split('/')) {
                if (part.Length == 0 || part == ".") {
                    continue;
                }
                if (part == "..") {
                    if (components.Count > 0) {
                        components.RemoveAt(components.Count - 1);
                    }
                    continue;
                }
                components.Add(part);
            }

            string resolved = "";
            for (int i = 0; i < components.Count; i++) {
                bool last = i == components.Count - 1;
                string candidate = resolved + "/" + components[i];

                int links = 0;
                while (true) {
                    Posix.StatData data;
                    int errno;
                    if (!Posix.TryStat(candidate, false, out data, out errno)) {
                        if (last && !strict && errno == Posix.ENOENT) {
                            break;
                        }
                        throw Posix.Error(errno, candidate);
                    }

                    if (data.FileType != Posix.S_IFLNK) {
                        if (!last && data.FileType != Posix.S_IFDIR) {
                            throw Posix.Error(Posix.ENOTDIR, candidate);
                        }
                        break;
                    }

                    if (++links > 32) {
                        throw Posix.Error(Posix.ELOOP, candidate);
                    }

                    string target = Posix.ReadLink(candidate, out errno);
                    if (target == null) {
                        throw Posix.Error(errno, candidate);
                    }

                    if (target.StartsWith("/", StringComparison.Ordinal)) {
                        candidate = ResolvePath(context, target, null, strict || !last);
                        break;
                    }
                    candidate = ResolvePath(context, target, resolved.Length == 0 ? "/" : resolved, strict || !last);
                    break;
                }

                resolved = candidate;
            }

            return resolved.Length == 0 ? "/" : resolved;
        }

        [RubyMethod("realpath", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ RealPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [Optional]object basedir) {

            return RealPath(toPath, self, path, basedir, true);
        }

        [RubyMethod("realdirpath", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString/*!*/ RealDirPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [Optional]object basedir) {

            return RealPath(toPath, self, path, basedir, false);
        }

        private static MutableString/*!*/ RealPath(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            object basedir, bool strict) {

            string strPath = self.Context.DecodePath(Protocols.CastToPath(toPath, path));
            string strBase = (basedir == Missing.Value || basedir == null)
                ? null
                : self.Context.DecodePath(Protocols.CastToPath(toPath, basedir));

            if (!Posix.IsAvailable) {
                return ExpandPath(toPath, self, path, basedir == Missing.Value ? null : basedir);
            }

            return self.Context.EncodePath(ResolvePath(self.Context, strPath, strBase, strict));
        }

        #endregion
#endif
        #endregion

        #region atime, ctime, mtime, utime
#if FEATURE_FILESYSTEM

        [RubyMethod("atime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime AccessTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.AccessTime(RubyStatOps.Create(self));
        }

        [RubyMethod("atime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime AccessTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.AccessTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("ctime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime CreateTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.CreateTime(RubyStatOps.Create(self));
        }

        [RubyMethod("ctime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime CreateTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.CreateTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("mtime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime ModifiedTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.ModifiedTime(RubyStatOps.Create(self));
        }

        [RubyMethod("mtime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime ModifiedTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.ModifiedTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("utime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int UpdateTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            params object[]/*!*/ paths) {

            return UpdateTimes(toPath, self, accessTime, modifiedTime, paths, true);
        }

        [RubyMethod("lutime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static int UpdateLinkTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            params object[]/*!*/ paths) {

            return UpdateTimes(toPath, self, accessTime, modifiedTime, paths, false);
        }

        private static int UpdateTimes(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object accessTime, object modifiedTime,
            object[]/*!*/ paths, bool followLinks) {

            var context = self.Context;
            RubyTime atime = MakeTime(context, accessTime);
            RubyTime mtime = MakeTime(context, modifiedTime);

            foreach (var path in paths) {
                string strPath = context.DecodePath(Protocols.CastToPath(toPath, path));

                if (Posix.IsAvailable) {
                    long[] times = new long[4];
                    SplitTime(atime, out times[0], out times[1]);
                    SplitTime(mtime, out times[2], out times[3]);

                    int errno;
                    if (Posix.UTimes(strPath, times, followLinks, out errno) != 0) {
                        throw Posix.Error(errno, strPath);
                    }
                    continue;
                }

                FileInfo info = new FileInfo(strPath);
                if (!info.Exists) {
                    throw RubyExceptions.CreateENOENT("No such file or directory - {0}", strPath);
                }
                info.LastAccessTimeUtc = atime.ToUniversalTime();
                info.LastWriteTimeUtc = mtime.ToUniversalTime();
            }

            return paths.Length;
        }

        private static void SplitTime(RubyTime/*!*/ time, out long seconds, out long nanoseconds) {
            long ticks = time.TicksSinceEpoch;
            seconds = ticks / TimeSpan.TicksPerSecond;
            long rest = ticks % TimeSpan.TicksPerSecond;
            if (rest < 0) {
                seconds -= 1;
                rest += TimeSpan.TicksPerSecond;
            }
            nanoseconds = rest * 100;
        }
#endif
        private static RubyTime MakeTime(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return new RubyTime(DateTime.Now);
            } else if (obj is RubyTime) {
                return (RubyTime)obj;
            } else if (obj is int) {
                return new RubyTime(RubyTime.ToLocalTime(RubyTime.Epoch.AddSeconds((int)obj)));
            } else if (obj is double) {
                return new RubyTime(RubyTime.ToLocalTime(RubyTime.Epoch.AddSeconds((double)obj)));
            } else {
                string name = context.GetClassOf(obj).Name;
                throw RubyExceptions.CreateTypeConversionError(name, "time");
            }
        }
        
        #endregion

        #region ftype, stat, inspect, path, to_path

#if FEATURE_FILESYSTEM
        [RubyMethod("ftype", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static MutableString FileType(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            // ftype reports on the link itself, not on its target
            return RubyStatOps.FileType(RubyStatOps.Create(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), false));
        }

        [RubyMethod("stat", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo/*!*/ Stat(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path));
        }

        [RubyMethod("lstat", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo/*!*/ LStat(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.Create(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), false);
        }

        [RubyMethod("lstat", BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("stat", BuildConfig = "FEATURE_FILESYSTEM")]
        public static FileSystemInfo Stat(RubyFile/*!*/ self) {
            return RubyStatOps.Create(self);
        }

        [RubyMethod("birthtime", BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime BirthTime(RubyContext/*!*/ context, RubyFile/*!*/ self) {
            return RubyStatOps.BirthTime(RubyStatOps.Create(self));
        }

        [RubyMethod("birthtime", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static RubyTime BirthTime(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return RubyStatOps.BirthTime(RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("size", BuildConfig = "FEATURE_FILESYSTEM")]
        public static object FileSize(RubyFile/*!*/ self) {
            return RubyStatOps.Size(RubyStatOps.Create(self));
        }
#endif
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyFile/*!*/ self) {
            return MutableString.CreateMutable(self.Context.GetPathEncoding()).
                Append("#<").
                Append(self.Context.GetClassOf(self).GetName(self.Context)).
                Append(':').
                Append(self.Path).
                Append(self.Closed ? " (closed)" : "").
                Append('>');
        }

        [RubyMethod("path")]
        [RubyMethod("to_path")]
        public static MutableString GetPath(RubyFile/*!*/ self) {
            self.RequireInitialized();
            return self.Path != null ? self.Context.EncodePath(self.Path) : null;
        }

        #endregion

        #region File::Stat
#if FEATURE_FILESYSTEM

        /// <summary>
        /// A File::Stat backed by a real statx(2) result. Off Unix (or if libc
        /// is unavailable) File::Stat keeps using bare FileInfo/DirectoryInfo
        /// and the legacy approximations below.
        /// </summary>
        internal sealed class StatInfo : FileSystemInfo {
            internal readonly Posix.StatData Data;

            internal StatInfo(string/*!*/ path, Posix.StatData/*!*/ data) {
                Data = data;
                OriginalPath = path;
                FullPath = path;
            }

            public override bool Exists {
                get { return true; }
            }

            public override string/*!*/ Name {
                get { return System.IO.Path.GetFileName(FullPath); }
            }

            public override void Delete() {
                File.Delete(FullPath);
            }
        }

        /// <summary>
        /// Stat
        /// </summary>
        [RubyClass("Stat", Extends = typeof(FileSystemInfo), Inherits = typeof(object), BuildConfig = "FEATURE_FILESYSTEM"), Includes(typeof(Comparable))]
        public class RubyStatOps {

            internal static FileSystemInfo/*!*/ Create(RubyFile/*!*/ file) {
                file.RequireInitialized();
                if (file.Path != null) {
                    return Create(file.Context, file.Path);
                }

                // a file opened from a descriptor has no path; fstat it
                Posix.StatData data;
                int errno;
                if (Posix.TryFStat(file.GetFileDescriptor(), out data, out errno)) {
                    return new StatInfo("", data);
                }
                throw new NotSupportedException("cannot get file info for files without path");
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, MutableString/*!*/ path) {
                return Create(context, context.DecodePath(path));
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, string/*!*/ path) {
                return Create(context, path, true);
            }

            internal static FileSystemInfo/*!*/ Create(RubyContext/*!*/ context, string/*!*/ path, bool followLinks) {
                FileSystemInfo fsi;
                int errno;
                if (TryCreate(context, path, followLinks, out fsi, out errno)) {
                    return fsi;
                }
                throw Posix.Error(errno == 0 ? Posix.ENOENT : errno, path);
            }

            internal static bool TryCreate(RubyContext/*!*/ context, string/*!*/ path, out FileSystemInfo result) {
                int errno;
                return TryCreate(context, path, true, out result, out errno);
            }

            internal static bool TryCreate(RubyContext/*!*/ context, string/*!*/ path, bool followLinks, out FileSystemInfo result, out int errno) {
                result = null;
                errno = 0;

                if (Posix.IsAvailable) {
                    Posix.StatData data;
                    if (Posix.TryStat(path, followLinks, out data, out errno)) {
                        result = new StatInfo(path, data);
                        return true;
                    }
                    if (errno == 0) {
                        errno = Posix.ENOENT;
                    }
                    return false;
                }

                PlatformAdaptationLayer pal = context.Platform;
                if (pal.FileExists(path)) {
                    result = new FileInfo(path);
                } else if (pal.DirectoryExists(path)) {
                    result = new DirectoryInfo(path);
                } else if (path.ToUpperInvariant().Equals(NUL_VALUE)) {
                    result = new DeviceInfo(NUL_VALUE);
                } else {
                    errno = Posix.ENOENT;
                    return false;
                }
                return true;
            }

            private static Posix.StatData D(FileSystemInfo/*!*/ self) {
                var si = self as StatInfo;
                return si != null ? si.Data : null;
            }

            [RubyConstructor]
            public static FileSystemInfo/*!*/ Create(ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
                return Create(self.Context, Protocols.CastToPath(toPath, path));
            }

            [RubyMethod("<=>")]
            public static int Compare(FileSystemInfo/*!*/ self, [NotNull]FileSystemInfo/*!*/ other) {
                var a = D(self);
                var b = D(other);
                if (a != null && b != null) {
                    int c = a.MTimeSec.CompareTo(b.MTimeSec);
                    return c != 0 ? c : a.MTimeNsec.CompareTo(b.MTimeNsec);
                }
                return self.LastWriteTime.CompareTo(other.LastWriteTime);
            }

            [RubyMethod("<=>")]
            public static object Compare(FileSystemInfo/*!*/ self, object other) {
                Debug.Assert(other as FileSystemInfo == null);
                return null;
            }

            internal static RubyTime/*!*/ MakeTime(long seconds, long nanoseconds) {
                var utc = RubyTime.Epoch.AddSeconds(seconds).AddTicks(nanoseconds / 100);
                return new RubyTime(RubyTime.ToLocalTime(utc));
            }

            [RubyMethod("atime")]
            public static RubyTime/*!*/ AccessTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.ATimeSec, d.ATimeNsec) : new RubyTime(self.LastAccessTime);
            }

            [RubyMethod("mtime")]
            public static RubyTime/*!*/ ModifiedTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.MTimeSec, d.MTimeNsec) : new RubyTime(self.LastWriteTime);
            }

            [RubyMethod("ctime")]
            public static RubyTime/*!*/ CreateTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? MakeTime(d.CTimeSec, d.CTimeNsec) : new RubyTime(self.CreationTime);
            }

            [RubyMethod("birthtime")]
            public static RubyTime/*!*/ BirthTime(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return new RubyTime(self.CreationTime);
                }
                if (!d.HasBirthTime) {
                    throw new IronRuby.Builtins.NotImplementedError("birthtime() function is unimplemented on this filesystem");
                }
                return MakeTime(d.BTimeSec, d.BTimeNsec);
            }

            [RubyMethod("blksize")]
            public static object BlockSize(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.BlockSize : null;
            }

            [RubyMethod("blocks")]
            public static object Blocks(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)Protocols.Normalize(d.Blocks) : null;
            }

            [RubyMethod("blockdev?")]
            public static bool IsBlockDevice(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFBLK;
            }

            [RubyMethod("chardev?")]
            public static bool IsCharDevice(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFCHR;
            }

            [RubyMethod("dev")]
            public static object DeviceId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Dev) : (object)3;
            }

            [RubyMethod("rdev")]
            public static object RDeviceId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Rdev) : (object)3;
            }

            [RubyMethod("dev_major")]
            public static object DeviceIdMajor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.DevMajor : null;
            }

            [RubyMethod("dev_minor")]
            public static object DeviceIdMinor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.DevMinor : null;
            }

            [RubyMethod("rdev_major")]
            public static object RDeviceIdMajor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.RdevMajor : null;
            }

            [RubyMethod("rdev_minor")]
            public static object RDeviceIdMinor(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)d.RdevMinor : null;
            }

            [RubyMethod("directory?")]
            public static bool IsDirectory(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.FileType == Posix.S_IFDIR : (self is DirectoryInfo);
            }

            [RubyMethod("file?")]
            public static bool IsFile(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.FileType == Posix.S_IFREG : (self is FileInfo);
            }

            [RubyMethod("pipe?")]
            public static bool IsPipe(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFIFO;
            }

            [RubyMethod("socket?")]
            public static bool IsSocket(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFSOCK;
            }

            [RubyMethod("symlink?")]
            public static bool IsSymLink(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && d.FileType == Posix.S_IFLNK;
            }

            [RubyMethod("ftype")]
            public static MutableString/*!*/ FileType(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return MutableString.CreateAscii(IsFile(self) ? "file" : "directory");
                }
                switch (d.FileType) {
                    case Posix.S_IFREG: return MutableString.CreateAscii("file");
                    case Posix.S_IFDIR: return MutableString.CreateAscii("directory");
                    case Posix.S_IFCHR: return MutableString.CreateAscii("characterSpecial");
                    case Posix.S_IFBLK: return MutableString.CreateAscii("blockSpecial");
                    case Posix.S_IFIFO: return MutableString.CreateAscii("fifo");
                    case Posix.S_IFLNK: return MutableString.CreateAscii("link");
                    case Posix.S_IFSOCK: return MutableString.CreateAscii("socket");
                    default: return MutableString.CreateAscii("unknown");
                }
            }

            #region permission predicates

            private static bool ModeAccess(Posix.StatData/*!*/ d, int bit, bool real) {
                int uid = real ? Posix.GetUid() : Posix.GetEUid();
                if (uid == 0) {
                    // root bypasses read/write; execute still needs some x bit
                    return bit != Posix.X_OK || (d.Mode & 0111) != 0;
                }

                int shift;
                if (d.Uid == uid) {
                    shift = 6;
                } else if (d.Gid == (real ? Posix.GetGid() : Posix.GetEGid()) || Array.IndexOf(Posix.GetGroups(), d.Gid) >= 0) {
                    shift = 3;
                } else {
                    shift = 0;
                }
                return (d.Mode & (bit << shift)) != 0;
            }

            [RubyMethod("executable?")]
            public static bool IsExecutable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return ModeAccess(d, Posix.X_OK, false);
                }
                if (System.IO.Path.DirectorySeparatorChar == '/') {
                    var mode = self.UnixFileMode;
                    return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                }
                return self.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            }

            [RubyMethod("executable_real?")]
            public static bool IsExecutableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.X_OK, true) : IsExecutable(self);
            }

            [RubyMethod("readable?")]
            public static bool IsReadable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.R_OK, false) : true;
            }

            [RubyMethod("readable_real?")]
            public static bool IsReadableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.R_OK, true) : true;
            }

            [RubyMethod("writable?")]
            public static bool IsWritable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.W_OK, false) : ((self.Attributes & FileAttributes.ReadOnly) == 0);
            }

            [RubyMethod("writable_real?")]
            public static bool IsWritableReal(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? ModeAccess(d, Posix.W_OK, true) : IsWritable(self);
            }

            [RubyMethod("world_readable?")]
            public static object IsWorldReadable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null || (d.Mode & 04) == 0) {
                    return null;
                }
                return d.Mode & 07777;
            }

            [RubyMethod("world_writable?")]
            public static object IsWorldWritable(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null || (d.Mode & 02) == 0) {
                    return null;
                }
                return d.Mode & 07777;
            }

            [RubyMethod("owned?")]
            public static bool IsUserOwned(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Uid == Posix.GetEUid() : true;
            }

            [RubyMethod("grpowned?")]
            public static bool IsGroupOwned(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d == null) {
                    return false;
                }
                return d.Gid == Posix.GetEGid() || Array.IndexOf(Posix.GetGroups(), d.Gid) >= 0;
            }

            [RubyMethod("setgid?")]
            public static bool IsSetGid(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && (d.Mode & Posix.S_ISGID) != 0;
            }

            [RubyMethod("setuid?")]
            public static bool IsSetUid(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null && (d.Mode & Posix.S_ISUID) != 0;
            }

            [RubyMethod("sticky?")]
            public static object IsSticky(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? (object)((d.Mode & Posix.S_ISVTX) != 0) : null;
            }

            #endregion

            [RubyMethod("identical?")]
            public static bool AreIdentical(RubyContext/*!*/ context, FileSystemInfo/*!*/ self, [NotNull]FileSystemInfo/*!*/ other) {
                var a = D(self);
                var b = D(other);
                if (a != null && b != null) {
                    return a.Dev == b.Dev && a.Ino == b.Ino;
                }
                return self.Exists && other.Exists && context.Platform.PathComparer.Compare(self.FullName, other.FullName) == 0;
            }

            [RubyMethod("gid")]
            public static int GroupId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Gid : 0;
            }

            [RubyMethod("uid")]
            public static int UserId(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Uid : 0;
            }

            [RubyMethod("ino")]
            public static object Inode(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? Protocols.Normalize(d.Ino) : (object)0;
            }

            [RubyMethod("nlink")]
            public static int NumberOfLinks(FileSystemInfo/*!*/ self) {
                var d = D(self);
                return d != null ? d.Nlink : 1;
            }

            [RubyMethod("mode")]
            public static int Mode(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.Mode;
                }
                int mode = (self is FileInfo) ? 0x8000 : 0x4000;
                mode |= 0x100; // S_IREAD;
                if ((self.Attributes & FileAttributes.ReadOnly) == 0) {
                    mode |= 0x80; // S_IWRITE;
                }
                return mode;
            }

            [RubyMethod("size")]
            public static object Size(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return Protocols.Normalize(d.Size);
                }
                if (self is DeviceInfo) {
                    return 0;
                }
                FileInfo info = (self as FileInfo);
                return (info == null) ? 0 : (object)Protocols.Normalize(info.Length);
            }

            [RubyMethod("size?")]
            public static object NullableSize(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.Size == 0 ? null : Protocols.Normalize(d.Size);
                }
                if (self is DeviceInfo) {
                    return 0;
                }
                FileInfo info = (self as FileInfo);
                if (info == null) {
                    return null;
                }
                return (info.Length == 0) ? null : (object)(int)info.Length;
            }

            [RubyMethod("zero?")]
            public static bool IsZeroLength(FileSystemInfo/*!*/ self) {
                var d = D(self);
                if (d != null) {
                    return d.FileType != Posix.S_IFDIR && d.Size == 0;
                }
                if (self is DeviceInfo) {
                    return true;
                }
                FileInfo info = (self as FileInfo);
                return (info == null) ? false : info.Length == 0;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, FileSystemInfo/*!*/ self) {
               return MutableString.CreateAscii(String.Format(CultureInfo.InvariantCulture,
                    "#<File::Stat dev={0}, ino={1}, mode={2}, nlink={3}, uid={4}, gid={5}, rdev={6}, size={7}, blksize={8}, blocks={9}, atime={10}, mtime={11}, ctime={12}>",
                    FormatDev(DeviceId(self)),
                    context.Inspect(Inode(self)),
                    FormatMode(Mode(self)),
                    context.Inspect(NumberOfLinks(self)),
                    context.Inspect(UserId(self)),
                    context.Inspect(GroupId(self)),
                    FormatDev(RDeviceId(self)),
                    context.Inspect(Size(self)),
                    context.Inspect(BlockSize(self)),
                    context.Inspect(Blocks(self)),
                    context.Inspect(AccessTime(self)),
                    context.Inspect(ModifiedTime(self)),
                    context.Inspect(CreateTime(self))
                ));
            }

            private static string FormatDev(object dev) {
                return dev is int ? "0x" + ((int)dev).ToString("x") : dev.ToString();
            }

            private static string FormatMode(int mode) {
                return "0" + Convert.ToString(mode, 8);
            }

            internal class DeviceInfo : FileSystemInfo {

                private string/*!*/ _name;

                internal DeviceInfo(string/*!*/ name) {
                    _name = name;
                }

                public override void Delete() {
                    throw new NotImplementedException();
                }

                public override bool Exists {
                    get { return true; }
                }

                public override string Name {
                    get { return _name; }
                }
            }
        }
#endif
        #endregion

        #region FileTest methods (public singletons only)
#if FEATURE_FILESYSTEM
        // TODO: conversion: to_io, to_path, to_str

        [RubyMethod("blockdev?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsBlockDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsBlockDevice(toPath, self, path);
        }

        [RubyMethod("chardev?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsCharDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsCharDevice(toPath, self, path);
        }

        [RubyMethod("directory?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsDirectory(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsDirectory(toPath, self, path);
        }

        [RubyMethod("executable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsExecutable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsExecutable(toPath, self, path);
        }

        [RubyMethod("executable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsExecutableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsExecutableReal(toPath, self, path);
        }

        [RubyMethod("exist?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("exists?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool Exists(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.Exists(toPath, self, path);
        }

        [RubyMethod("file?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsFile(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsFile(toPath, self, path);
        }

        [RubyMethod("grpowned?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsGroupOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsGroupOwned(toPath, self, path);
        }

        [RubyMethod("identical?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool AreIdentical(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path1, object path2) {
            return FileTest.AreIdentical(toPath, self, path1, path2);
        }

        [RubyMethod("owned?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsUserOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsUserOwned(toPath, self, path);
        }

        [RubyMethod("pipe?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsPipe(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsPipe(toPath, self, path);
        }

        [RubyMethod("readable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsReadable(toPath, self, path);
        }

        [RubyMethod("readable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsReadableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsReadableReal(toPath, self, path);
        }

        [RubyMethod("setgid?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSetGid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSetGid(toPath, self, path);
        }

        [RubyMethod("setuid?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSetUid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSetUid(toPath, self, path);
        }

        [RubyMethod("size", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object Size(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.Size(toPath, self, path);
        }

        [RubyMethod("size?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object NullableSize(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.NullableSize(toPath, self, path);
        }

        [RubyMethod("socket?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSocket(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSocket(toPath, self, path);
        }

        [RubyMethod("sticky?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsSticky(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSticky(toPath, self, path);
        }

        [RubyMethod("symlink?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsSymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsSymLink(toPath, self, path);
        }

        [RubyMethod("writable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWritable(toPath, self, path);
        }

        [RubyMethod("writable_real?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsWritableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWritableReal(toPath, self, path);
        }

        [RubyMethod("world_readable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsWorldReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWorldReadable(toPath, self, path);
        }

        [RubyMethod("world_writable?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static object IsWorldWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsWorldWritable(toPath, self, path);
        }

        [RubyMethod("empty?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        [RubyMethod("zero?", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_FILESYSTEM")]
        public static bool IsZeroLength(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return FileTest.IsZeroLength(toPath, self, path);
        }
#endif
        #endregion
    }
}
