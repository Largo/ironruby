using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IronRuby.Prism {
    /// <summary>
    /// Managed front end over libprism. The primary path is Parse: prism's compact
    /// binary serialization decoded by the generated PrismLoader (same scheme as
    /// JRuby's generated Java loader). ParseToJson remains for debugging.
    /// </summary>
    public static class PrismParser {
        public static PrismParseResult/*!*/ Parse(string/*!*/ source, string path, int startLine, IList<string> outerLocals) {
            return PrismLoader.LoadParse(ParseSerialized(source, BuildOptionsData(path, startLine, outerLocals)));
        }

        public static byte[]/*!*/ ParseSerialized(string/*!*/ source) {
            return ParseSerialized(source, null);
        }

        public static byte[]/*!*/ ParseSerialized(string/*!*/ source, byte[] optionsData) {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            IntPtr buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            GCHandle options = optionsData != null ? GCHandle.Alloc(optionsData, GCHandleType.Pinned) : default;
            try {
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_serialize_parse(buffer, pinned.AddrOfPinnedObject(), (nuint)bytes.Length,
                    optionsData != null ? options.AddrOfPinnedObject() : IntPtr.Zero);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                byte[] result = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), result, 0, length);
                return result;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinned.Free();
                if (optionsData != null) options.Free();
            }
        }

        /// <summary>
        /// Serializes pm_options_t as expected by pm_serialize_parse's data argument
        /// (see prism docs/serialization.md "APIs"). Passing outer locals as a scope
        /// makes prism resolve eval-context locals itself.
        /// </summary>
        internal static byte[]/*!*/ BuildOptionsData(string path, int startLine, IList<string> outerLocals) {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path ?? "");
                writer.Write(pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(startLine);
                writer.Write(0);            // encoding name length (default)
                writer.Write((byte)0);      // frozen string literal
                writer.Write((byte)0);      // command line flags
                writer.Write((byte)0);      // syntax version (latest)
                writer.Write((byte)0);      // encoding locked
                writer.Write((byte)0);      // main script
                writer.Write((byte)0);      // partial script
                writer.Write((byte)0);      // freeze
                if (outerLocals != null) {
                    writer.Write(1);        // one scope
                    writer.Write(outerLocals.Count);
                    writer.Write((byte)0);  // forwarding flags
                    foreach (string local in outerLocals) {
                        byte[] localBytes = Encoding.UTF8.GetBytes(local);
                        writer.Write(localBytes.Length);
                        writer.Write(localBytes);
                    }
                } else {
                    writer.Write(0);        // no scopes
                }
                return stream.ToArray();
            }
        }

        public static string/*!*/ ParseToJson(string/*!*/ source) {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            IntPtr arena = IntPtr.Zero, parser = IntPtr.Zero, buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try {
                arena = PrismNative.pm_arena_new();
                parser = PrismNative.pm_parser_new(arena, pinned.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                IntPtr node = PrismNative.pm_parse(parser);
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_dump_json(buffer, parser, node);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                unsafe {
                    return Encoding.UTF8.GetString((byte*)PrismNative.pm_buffer_value(buffer), length);
                }
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                if (parser != IntPtr.Zero) PrismNative.pm_parser_free(parser);
                if (arena != IntPtr.Zero) PrismNative.pm_arena_free(arena);
                pinned.Free();
            }
        }
    }
}
