/* ****************************************************************************
 *
 * IronRuby's implementation of erb's C extension, erb/escape.
 *
 * The erb gem is pure Ruby apart from ERB::Escape.html_escape (ext/erb/escape/escape.c),
 * which ERB::Util.html_escape and its alias h are. Without the extension erb/util.rb falls
 * back to CGI.escapeHTML(s.to_s), a gsub with a Hash: a dozen times slower than the C
 * function, and different from it in what it answers - the C function hands back the
 * String it was given when nothing in it needs escaping, a subclass's instance included,
 * and calls #to_s the way rb_convert_type does. Src/StdLib/ironruby/erb/escape.rb answers
 * `require "erb/escape"` with this.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Erb {

    [RubyClass("ERB")]
    public static class ErbOps {

        [RubyModule("Escape")]
        public static class EscapeOps {

            /// <summary>
            /// escape.c's erb_escape_html: a String in an ASCII-compatible encoding is escaped here
            /// (optimized_escape_html), any other String goes to CGI.escapeHTML, and anything else is
            /// converted with #to_s first (rb_convert_type).
            /// </summary>
            [RubyMethod("html_escape", RubyMethodAttributes.PrivateInstance)]
            [RubyMethod("html_escape", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HtmlEscape(UnaryOpStorage/*!*/ storage, BinaryOpStorage/*!*/ cgiStorage, object self,
                object value) {
                MutableString str = value as MutableString ?? ConvertToS(storage, value);
                if (!str.Encoding.IsAsciiIdentity) {
                    return EscapeWithCgi(cgiStorage, str);
                }
                return Escape(str);
            }

            /// <summary>
            /// optimized_escape_html: the bytes &amp; &lt; &gt; &quot; ' become entities. None of them can be a
            /// byte of a multibyte character in an ASCII-compatible encoding, so the bytes are scanned as
            /// they are. The String itself is the answer when it has none of them; otherwise a new String in
            /// its encoding.
            /// </summary>
            private static MutableString/*!*/ Escape(MutableString/*!*/ str) {
                byte[] bytes = str.ToByteArray();
                int first = 0;
                while (first < bytes.Length && EntityOf(bytes[first]) == null) {
                    first++;
                }
                if (first == bytes.Length) {
                    return str;
                }

                var result = MutableString.CreateMutable(bytes.Length + 16, str.Encoding);
                result.Append(bytes, 0, first);
                int start = first;
                for (int i = first; i < bytes.Length; i++) {
                    string entity = EntityOf(bytes[i]);
                    if (entity != null) {
                        result.Append(bytes, start, i - start);
                        result.Append(entity);
                        start = i + 1;
                    }
                }
                result.Append(bytes, start, bytes.Length - start);
                return result;
            }

            private static string EntityOf(byte b) {
                switch (b) {
                    case (byte)'\'': return "&#39;";
                    case (byte)'&': return "&amp;";
                    case (byte)'"': return "&quot;";
                    case (byte)'<': return "&lt;";
                    case (byte)'>': return "&gt;";
                    default: return null;
                }
            }

            private static MutableString/*!*/ EscapeWithCgi(BinaryOpStorage/*!*/ storage, MutableString/*!*/ str) {
                var context = storage.Context;
                object cgi;
                if (!context.ObjectClass.TryGetConstant(null, "CGI", out cgi)) {
                    throw RubyExceptions.CreateNameError("uninitialized constant CGI");
                }
                var site = storage.GetCallSite("escapeHTML", 1);
                return (MutableString)site.Target(site, cgi, str);
            }

            /// <summary>
            /// rb_convert_type(value, T_STRING, "String", "to_s"): a value without #to_s is a TypeError,
            /// and so is a #to_s that answers anything but a String.
            /// </summary>
            private static MutableString/*!*/ ConvertToS(UnaryOpStorage/*!*/ storage, object value) {
                var context = storage.Context;
                // rb_check_funcall: a method_missing that a respond_to_missing? owns up to counts as #to_s
                RubyClass cls = context.GetImmediateClassOf(value);
                if (!cls.ResolveMethod("to_s", VisibilityContext.AllVisible).Found &&
                    !(cls.ResolveMethod("respond_to?", VisibilityContext.AllVisible).Found && context.RespondTo(value, "to_s"))) {
                    throw RubyExceptions.CreateTypeError("can't convert {0} into String",
                        RubyExceptions.MessageTypeName(context.GetClassName(value)));
                }
                var site = storage.GetCallSite("to_s", new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf));
                object result = site.Target(site, value);
                if (result is MutableString str) {
                    return str;
                }
                string className = RubyExceptions.MessageTypeName(context.GetClassName(value));
                throw RubyExceptions.CreateTypeError("can't convert {0} to String ({0}#to_s gives {1})",
                    className, RubyExceptions.MessageTypeName(context.GetClassName(result)));
            }
        }
    }
}
