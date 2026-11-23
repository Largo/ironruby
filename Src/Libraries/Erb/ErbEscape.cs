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

using System;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Erb {
    using AskStorage = CallSiteStorage<Func<CallSite, object, object, object, object>>;


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
            public static MutableString/*!*/ HtmlEscape(UnaryOpStorage/*!*/ storage, BinaryOpStorage/*!*/ cgiStorage,
                BinaryOpStorage/*!*/ respondTo1Storage, AskStorage/*!*/ respondTo2Storage, AskStorage/*!*/ respondToMissingStorage,
                object self, object value) {
                MutableString str = value as MutableString ??
                    ConvertToS(storage, respondTo1Storage, respondTo2Storage, respondToMissingStorage, value);
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
            /// and so is a #to_s that answers anything but a String. Whether there is a #to_s is
            /// rb_check_funcall's question (vm_eval.c), asked as it asks it:
            ///   - a respond_to? of the object's own is asked first (check_funcall_respond_to), with
            ///     include_private true unless it takes one argument, and a false is the TypeError;
            ///   - a #to_s of any visibility is called;
            ///   - otherwise (check_funcall_missing) a respond_to_missing? of the object's own is asked
            ///     (:to_s, true) and a false is the TypeError; then a method_missing of its own is called -
            ///     BasicObject's is the TypeError - and a NoMethodError out of it is the TypeError too,
            ///     unless respond_to? or respond_to_missing? had said yes (check_funcall_failed).
            /// </summary>
            private static MutableString/*!*/ ConvertToS(UnaryOpStorage/*!*/ storage, BinaryOpStorage/*!*/ respondTo1Storage,
                AskStorage/*!*/ respondTo2Storage, AskStorage/*!*/ respondToMissingStorage, object value) {
                var context = storage.Context;
                RubyClass cls = context.GetImmediateClassOf(value);
                RubySymbol name = context.CreateAsciiSymbol("to_s");

                bool saidYes = false;
                var respondTo = cls.ResolveMethod("respond_to?", VisibilityContext.AllVisible);
                if (respondTo.Found && !(respondTo.Info.DeclaringModule == context.KernelModule && respondTo.Info is RubyLibraryMethodInfo)) {
                    object answer;
                    if (respondTo.Info.GetArity() == 1) {
                        var site = respondTo1Storage.GetCallSite("respond_to?", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                        answer = site.Target(site, value, name);
                    } else {
                        var site = respondTo2Storage.GetCallSite("respond_to?", new RubyCallSignature(2, RubyCallFlags.HasImplicitSelf));
                        answer = site.Target(site, value, name, ScriptingRuntimeHelpers.True);
                    }
                    if (!RubyOps.IsTrue(answer)) {
                        throw CannotConvert(context, value);
                    }
                    saidYes = true;
                }

                if (!cls.ResolveMethod("to_s", VisibilityContext.AllVisible).Found) {
                    var respondToMissing = cls.ResolveMethod("respond_to_missing?", VisibilityContext.AllVisible);
                    if (respondToMissing.Found && !(respondToMissing.Info.DeclaringModule == context.KernelModule && respondToMissing.Info is RubyLibraryMethodInfo)) {
                        var site = respondToMissingStorage.GetCallSite("respond_to_missing?", new RubyCallSignature(2, RubyCallFlags.HasImplicitSelf));
                        if (!RubyOps.IsTrue(site.Target(site, value, name, ScriptingRuntimeHelpers.True))) {
                            throw CannotConvert(context, value);
                        }
                        saidYes = true;
                    }
                    var methodMissing = cls.ResolveMethod("method_missing", VisibilityContext.AllVisible);
                    if (!methodMissing.Found ||
                        methodMissing.Info.DeclaringModule == context.BasicObjectClass && methodMissing.Info is RubyLibraryMethodInfo) {
                        throw CannotConvert(context, value);
                    }
                    try {
                        return CheckResult(context, value, CallToS(storage, value));
                    } catch (MissingMethodException) when (!saidYes) {
                        throw CannotConvert(context, value);
                    }
                }
                return CheckResult(context, value, CallToS(storage, value));
            }

            private static object CallToS(UnaryOpStorage/*!*/ storage, object value) {
                var site = storage.GetCallSite("to_s", new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf));
                return site.Target(site, value);
            }

            private static MutableString/*!*/ CheckResult(RubyContext/*!*/ context, object value, object result) {
                if (result is MutableString str) {
                    return str;
                }
                string className = RubyExceptions.MessageTypeName(context.GetClassName(value));
                throw RubyExceptions.CreateTypeError("can't convert {0} to String ({0}#to_s gives {1})",
                    className, RubyExceptions.MessageTypeName(context.GetClassName(result)));
            }

            private static Exception/*!*/ CannotConvert(RubyContext/*!*/ context, object value) {
                return RubyExceptions.CreateTypeError("can't convert {0} into String",
                    RubyExceptions.MessageTypeName(context.GetClassName(value)));
            }
        }
    }
}
