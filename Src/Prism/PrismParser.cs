using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IronRuby.Prism {
    /// <summary>
    /// Managed front end over libprism. Two entry points:
    /// ParseToJson - full AST as JSON (prototype/bridge path),
    /// ParseSerialized - prism's compact binary serialization (the fast path;
    /// same format JRuby/TruffleRuby load, see prism's docs/serialization.md).
    /// </summary>
    public static class PrismParser {
        public static string ParseToJson(string source) {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            IntPtr arena = IntPtr.Zero, parser = IntPtr.Zero, buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try {
                arena = PrismNative.pm_arena_new();
                parser = PrismNative.pm_parser_new(arena, pinned.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                IntPtr node = PrismNative.pm_parse(parser);
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_dump_json(buffer, parser, node);
                return ReadBuffer(buffer);
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                if (parser != IntPtr.Zero) PrismNative.pm_parser_free(parser);
                if (arena != IntPtr.Zero) PrismNative.pm_arena_free(arena);
                pinned.Free();
            }
        }

        public static byte[] ParseSerialized(string source) {
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            IntPtr buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try {
                buffer = PrismNative.pm_buffer_new();
                PrismNative.pm_serialize_parse(buffer, pinned.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                byte[] result = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), result, 0, length);
                return result;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinned.Free();
            }
        }

        private static string ReadBuffer(IntPtr buffer) {
            int length = checked((int)PrismNative.pm_buffer_length(buffer));
            unsafe {
                return Encoding.UTF8.GetString((byte*)PrismNative.pm_buffer_value(buffer), length);
            }
        }
    }
}
