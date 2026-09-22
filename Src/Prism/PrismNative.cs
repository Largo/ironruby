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

        /// <summary>
        /// The file name the prism build produces on this platform. `make shared` names it
        /// after RbConfig's SOEXT, so it is libprism.so on Linux, libprism.dll under the
        /// Windows DevKit (MinGW keeps the lib prefix) and libprism.dylib on macOS. The
        /// plain Windows spelling prism.dll is accepted too, for a hand-built or vendored
        /// copy.
        /// </summary>
        private static readonly string[] FileNames =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "libprism.dll", "prism.dll" } :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new[] { "libprism.dylib", "prism.dylib" } :
            new[] { "libprism.so", "prism.so" };

        static PrismNative() {
            NativeLibrary.SetDllImportResolver(typeof(PrismNative).Assembly, (name, assembly, path) => {
                if (name == Lib) {
                    foreach (string fileName in FileNames) {
                        string local = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
                        if (System.IO.File.Exists(local)) {
                            return NativeLibrary.Load(local);
                        }
                    }
                    // Not beside the host: let the OS loader look on its own search path
                    // (LD_LIBRARY_PATH, PATH, the app directory) before giving up.
                    foreach (string fileName in FileNames) {
                        if (NativeLibrary.TryLoad(fileName, out IntPtr handle)) {
                            return handle;
                        }
                    }
                }
                return IntPtr.Zero;
            });
        }

        /// <summary>
        /// Forces the DllImport resolver above to be installed. Other types in this assembly
        /// P/Invoke "prism" as well (PrismLex), and a static constructor only runs when its own
        /// type is first touched - so the resolver has to be armed before any of them is called.
        /// </summary>
        internal static void EnsureResolver() {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PrismNative).TypeHandle);
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
        [DllImport(Lib)] internal static extern void pm_serialize_lex(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] internal static extern void pm_serialize_parse_comments(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] internal static extern void pm_serialize_parse_lex(IntPtr buffer, IntPtr source, nuint size, IntPtr data);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool pm_serialize_parse_success_p(IntPtr source, nuint size, IntPtr data);

        // Formats the parse errors into the buffer and answers their level: -1 none,
        // 0 syntax, 1 argument, 2 load.  format_type is pm_errors_format_type_t:
        // 1 plain, 2 bold, 3 color.
        [DllImport(Lib)] internal static extern sbyte pm_serialize_parse_errors_format(IntPtr buffer, IntPtr source, nuint size, IntPtr data, int formatType);

        // The prism library's own version string ("1.9.0"), which is what the Ruby
        // library reports as Prism::VERSION.
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern IntPtr pm_version();

        // Whether a string would lex as a local variable name / a constant name / a
        // method name.  Returns pm_string_query_t: -1 error, 0 false, 1 true.
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_local(IntPtr source, nuint length, string encodingName);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_constant(IntPtr source, nuint length, string encodingName);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern int pm_string_query_method_name(IntPtr source, nuint length, string encodingName);

        // diagnostics
        [DllImport(Lib)] internal static extern int pm_parser_start_line(IntPtr parser);
        [DllImport(Lib, CharSet = CharSet.Ansi)] internal static extern IntPtr pm_parser_encoding_name(IntPtr parser);
    }
}
