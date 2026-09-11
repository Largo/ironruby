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
#if FEATURE_FILESYSTEM


using System;
using System.IO;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    // TODO: conversion: to_io, to_path, to_str

    [RubyModule("FileTest", BuildConfig = "FEATURE_FILESYSTEM")]
    public static class FileTest {
        /// <summary>
        /// Every one of these predicates answers false (or nil) for a path that
        /// does not exist -- none of them raises. Ruby only raises from the
        /// non-predicate queries (File.size, File.ftype, ...).
        /// </summary>
        private static bool Query(RubyContext/*!*/ context, MutableString/*!*/ path, bool followLinks, Func<FileSystemInfo, bool>/*!*/ predicate) {
            FileSystemInfo fsi;
            int errno;
            if (!RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(path), followLinks, out fsi, out errno)) {
                return false;
            }
            return predicate(fsi);
        }

        private static object NullableQuery(RubyContext/*!*/ context, MutableString/*!*/ path, Func<FileSystemInfo, object>/*!*/ query) {
            FileSystemInfo fsi;
            int errno;
            if (!RubyFileOps.RubyStatOps.TryCreate(context, context.DecodePath(path), true, out fsi, out errno)) {
                return null;
            }
            return query(fsi);
        }

        [RubyMethod("blockdev?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("blockdev?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsBlockDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsBlockDevice);
        }

        [RubyMethod("chardev?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("chardev?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsCharDevice(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsCharDevice);
        }

        [RubyMethod("directory?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("directory?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsDirectory(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsDirectory);
        }

        [RubyMethod("executable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("executable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsExecutable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsExecutable);
        }

        [RubyMethod("executable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("executable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsExecutableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsExecutableReal);
        }

        [RubyMethod("exist?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("exist?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("exists?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("exists?", RubyMethodAttributes.PrivateInstance)]
        public static bool Exists(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, (fsi) => true);
        }

        [RubyMethod("file?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("file?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsFile(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsFile);
        }

        [RubyMethod("grpowned?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("grpowned?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsGroupOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsGroupOwned);
        }

        [RubyMethod("identical?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("identical?", RubyMethodAttributes.PrivateInstance)]
        public static bool AreIdentical(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path1, object path2) {
            FileSystemInfo info1, info2;
            int errno;

            return RubyFileOps.RubyStatOps.TryCreate(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path1)), true, out info1, out errno)
                && RubyFileOps.RubyStatOps.TryCreate(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path2)), true, out info2, out errno)
                && RubyFileOps.RubyStatOps.AreIdentical(self.Context, info1, info2);
        }

        [RubyMethod("owned?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("owned?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsUserOwned(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsUserOwned);
        }

        [RubyMethod("pipe?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("pipe?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsPipe(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsPipe);
        }

        [RubyMethod("readable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("readable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsReadable);
        }

        [RubyMethod("readable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("readable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsReadableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsReadableReal);
        }

        [RubyMethod("setgid?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("setgid?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSetGid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsSetGid);
        }

        [RubyMethod("setuid?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("setuid?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSetUid(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsSetUid);
        }

        [RubyMethod("size", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("size", RubyMethodAttributes.PrivateInstance)]
        public static object Size(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return RubyFileOps.RubyStatOps.Size(RubyFileOps.RubyStatOps.Create(self.Context, Protocols.CastToPath(toPath, path)));
        }

        [RubyMethod("size?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("size?", RubyMethodAttributes.PrivateInstance)]
        public static object NullableSize(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(self.Context, Protocols.CastToPath(toPath, path), RubyFileOps.RubyStatOps.NullableSize);
        }

        [RubyMethod("socket?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("socket?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSocket(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsSocket);
        }

        [RubyMethod("sticky?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("sticky?", RubyMethodAttributes.PrivateInstance)]
        public static object IsSticky(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true,
                (fsi) => RubyFileOps.RubyStatOps.IsSticky(fsi) as bool? ?? false);
        }

        [RubyMethod("symlink?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("symlink?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsSymLink(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), false, RubyFileOps.RubyStatOps.IsSymLink);
        }

        [RubyMethod("writable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("writable?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsWritable);
        }

        [RubyMethod("writable_real?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("writable_real?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsWritableReal(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return Query(self.Context, Protocols.CastToPath(toPath, path), true, RubyFileOps.RubyStatOps.IsWritableReal);
        }

        [RubyMethod("world_readable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("world_readable?", RubyMethodAttributes.PrivateInstance)]
        public static object IsWorldReadable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(self.Context, Protocols.CastToPath(toPath, path), RubyFileOps.RubyStatOps.IsWorldReadable);
        }

        [RubyMethod("world_writable?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("world_writable?", RubyMethodAttributes.PrivateInstance)]
        public static object IsWorldWritable(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            return NullableQuery(self.Context, Protocols.CastToPath(toPath, path), RubyFileOps.RubyStatOps.IsWorldWritable);
        }

        [RubyMethod("empty?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("empty?", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("zero?", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("zero?", RubyMethodAttributes.PrivateInstance)]
        public static bool IsZeroLength(ConversionStorage<MutableString>/*!*/ toPath, RubyModule/*!*/ self, object path) {
            var p = Protocols.CastToPath(toPath, path);
            string strPath = self.Context.DecodePath(p);

            // NUL/nul is a special-cased filename on Windows
            if (!Posix.IsAvailable && strPath.ToUpperInvariant() == "NUL") {
                return RubyFileOps.RubyStatOps.IsZeroLength(RubyFileOps.RubyStatOps.Create(self.Context, strPath));
            }

            return Query(self.Context, p, true, RubyFileOps.RubyStatOps.IsZeroLength);
        }
    }
}
#endif
