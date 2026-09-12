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
using IronRuby.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    public class RubyFile : RubyIO {
        public string Path { get; set; }

        public RubyFile(RubyContext/*!*/ context)
            : base(context) {
            Path = null;
        }

        public RubyFile(RubyContext/*!*/ context, string/*!*/ path, IOMode mode)
            : base(context, OpenFileStream(context, path, mode), mode) {
            Path = path;
        }

        public RubyFile(RubyContext/*!*/ context, Stream/*!*/ stream, int descriptor, IOMode mode)
            : base(context, stream, descriptor, mode) {
            Path = null;
        }

        public static Stream/*!*/ OpenFileStream(RubyContext/*!*/ context, string/*!*/ path, IOMode mode) {
            ContractUtils.RequiresNotNull(path, "path");
            FileAccess access = mode.ToFileAccess();

            FileMode fileMode;
  
            if ((mode & IOMode.CreateIfNotExists) != 0) {
                if ((mode & IOMode.ErrorIfExists) != 0) {
                    access |= FileAccess.Write;
                    fileMode = FileMode.CreateNew;
                } else {
                    fileMode = FileMode.OpenOrCreate;
                }
            } else {
                fileMode = FileMode.Open;
            }

            // O_RDONLY|O_TRUNC and O_RDONLY|O_APPEND are legal on Unix: the file opens
            // read-only (a later write raises IOError "not opened for writing") and
            // O_TRUNC still empties it.  Truncating needs write access on the handle
            // even though the Ruby-level mode stays read-only.
            bool truncateReadOnly = (mode & IOMode.Truncate) != 0 && (access & FileAccess.Write) == 0;
            if (truncateReadOnly) {
                access |= FileAccess.Write;
            }

            if (String.IsNullOrEmpty(path)) {
                // MRI treats the empty path as a missing file, not a bad argument.
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
            }

            Stream stream;
            if (path == "NUL") {
                stream = Stream.Null;
            } else {
                try {
                    stream = context.DomainManager.Platform.OpenInputFileStream(path, fileMode, access, FileShare.ReadWrite);
                } catch (FileNotFoundException) {
                    throw RubyExceptions.CreateENOENT(String.Format("No such file or directory - {0}", path));
#if FEATURE_FILESYSTEM
                } catch (DirectoryNotFoundException e) {
                    throw RubyExceptions.CreateENOENT(e.Message, e);
                } catch (PathTooLongException e) {
                    throw RubyExceptions.CreateENOENT(e.Message, e);
#endif
                } catch (IOException) {
                    if ((mode & IOMode.ErrorIfExists) != 0) {
                        throw RubyExceptions.CreateEEXIST(path);
                    } else {
                        throw;
                    }
                } catch (UnauthorizedAccessException) {
                    // .NET reports opening a directory as an access violation; MRI
                    // distinguishes it, so only a genuine one stays EACCES (which is
                    // what UnauthorizedAccessException already maps to).
                    if (context.DomainManager.Platform.DirectoryExists(path)) {
                        throw RubyExceptions.CreateEISDIR(path);
                    }
                    throw;
                } catch (ArgumentException e) {
                    throw RubyExceptions.CreateEINVAL(e.Message, e);
                }
            }

            if ((mode & IOMode.Truncate) != 0) {
                stream.SetLength(0);
            }

            return stream;
        }
    }
}