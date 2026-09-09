using System;
using System.Runtime.InteropServices;

namespace IronRuby.Prism {
    /// <summary>
    /// P/Invoke bindings for libprism (the CRuby parser, github.com/ruby/prism).
    /// All prism handles are opaque pointers; memory is owned by prism and released
    /// via the matching *_free functions.
    /// </summary>
    internal static class PrismNative {
        private const string Lib = "prism";

        static PrismNative() {
            NativeLibrary.SetDllImportResolver(typeof(PrismNative).Assembly, (name, assembly, path) => {
                if (name == Lib) {
                    string local = System.IO.Path.Combine(AppContext.BaseDirectory, "libprism.so");
                    if (System.IO.File.Exists(local)) {
                        return NativeLibrary.Load(local);
                    }
                }
                return IntPtr.Zero;
            });
        }

        // arena + parser + node lifecycle
        [DllImport(Lib)] internal static extern IntPtr pm_arena_new();
        [DllImport(Lib)] internal static extern void pm_arena_free(IntPtr arena);
        [DllImport(Lib)] internal static extern IntPtr pm_parser_new(IntPtr arena, IntPtr source, nuint size, IntPtr options);
        [DllImport(Lib)] internal static extern void pm_parser_free(IntPtr parser);
        [DllImport(Lib)] internal static extern IntPtr pm_parse(IntPtr parser);

        // buffers (opaque)
        [DllImport(Lib)] internal static extern IntPtr pm_buffer_new();
        [DllImport(Lib)] internal static extern void pm_buffer_free(IntPtr buffer);
        [DllImport(Lib)] internal static extern IntPtr pm_buffer_value(IntPtr buffer);
        [DllImport(Lib)] internal static extern nuint pm_buffer_length(IntPtr buffer);

        // AST dumps
        [DllImport(Lib)] internal static extern void pm_dump_json(IntPtr buffer, IntPtr parser, IntPtr node);
        [DllImport(Lib)] internal static extern void pm_serialize_parse(IntPtr buffer, IntPtr source, nuint size, IntPtr data);

        // diagnostics
        [DllImport(Lib)] internal static extern int pm_parser_start_line(IntPtr parser);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern IntPtr pm_parser_encoding_name(IntPtr parser);
    }
}
