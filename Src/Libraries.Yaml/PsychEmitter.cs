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
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using YamlEmitter = IronRuby.StandardLibrary.Yaml.Emitter;

namespace IronRuby.StandardLibrary.Yaml {

    // The back end of Psych::Emitter (Src/StdLib/ironruby/psych.rb): it hands the events a
    // Psych handler receives to the engine's emitter, which is the algorithm libyaml uses,
    // so a node tree built in Ruby (Psych::Visitors::YAMLTree, or by hand) comes out as the
    // YAML text Psych would write. Psych.dump does not go this way - it represents the
    // object graph in C# and serializes that - so nothing here changes what dump emits.
    public static partial class RubyYaml {

        // Event kinds. Keep in sync with Psych::Emitter in psych.rb.
        private const int StreamStartKind = 0;
        private const int StreamEndKind = 1;
        private const int DocumentStartKind = 2;
        private const int DocumentEndKind = 3;
        private const int AliasKind = 4;
        private const int ScalarKind = 5;
        private const int SequenceStartKind = 6;
        private const int SequenceEndKind = 7;
        private const int MappingStartKind = 8;
        private const int MappingEndKind = 9;

        [RubyMethod("__emit_events", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ EmitEvents(RubyModule/*!*/ self, [NotNull]IList/*!*/ events,
            [DefaultProtocol]int indent, [DefaultProtocol]int lineWidth, object canonical) {

            YamlOptions options = new YamlOptions();
            // Psych writes YAML 1.1 tag handles: "!!null", "!!omap", "!ruby/object:Foo".
            options.Version = new Version(1, 1);
            if (indent >= 2 && indent < 10) {
                options.Indent = indent;
            }
            if (lineWidth > 0) {
                options.BestWidth = lineWidth;
            }
            options.Canonical = Protocols.IsTrue(canonical);

            MutableStringWriter writer = new MutableStringWriter(MutableString.CreateMutable(RubyEncoding.UTF8));
            YamlEmitter emitter = new YamlEmitter(writer, options);

            foreach (object e in events) {
                IList ev = e as IList;
                if (ev == null || ev.Count == 0) {
                    throw new PsychException("malformed emitter event");
                }
                emitter.Emit(MakeEvent(ev));
            }
            writer.Flush();
            return writer.String;
        }

        private static YamlEvent/*!*/ MakeEvent(IList/*!*/ ev) {
            switch (ToInt(ev[0])) {
                case StreamStartKind:
                    return StreamStartEvent.Instance;

                case StreamEndKind:
                    return StreamEndEvent.Instance;

                case DocumentStartKind:
                    // version, implicit
                    return new DocumentStartEvent(!IsTrue(ev[2]), ToVersion(ev[1]), null);

                case DocumentEndKind:
                    return IsTrue(ev[1]) ? DocumentEndEvent.ExplicitInstance : DocumentEndEvent.ImplicitInstance;

                case AliasKind:
                    return new AliasEvent(ToClrString(ev[1]));

                case ScalarKind: {
                    // value, anchor, tag, plain, quoted, style
                    bool plain = IsTrue(ev[4]);
                    bool quoted = IsTrue(ev[5]);
                    // The tag is written only when the value does not imply it, which is what
                    // Psych's "plain" and "quoted" flags say: nil carries the null tag and is
                    // still emitted as an empty plain scalar.
                    string tag = (plain || quoted) ? null : ToClrString(ev[3]);
                    // Type only matters for an untagged scalar: "Other" makes the emitter add a
                    // "!" when it has to quote, which is what a scalar that is not implicit when
                    // quoted asks for.
                    ScalarValueType type = (tag == null && !quoted) ? ScalarValueType.Other : ScalarValueType.String;
                    ScalarQuotingStyle style = ToQuotingStyle(ev[6]);
                    string value = ToClrString(ev[1]) ?? String.Empty;
                    // An empty plain scalar is written as nothing at all; the engine's emitter
                    // spells that a null value (which is how it represents nil).
                    return new ScalarEvent(ToClrString(ev[2]), tag, type,
                        (value.Length == 0 && style == ScalarQuotingStyle.None) ? null : value, style);
                }

                case SequenceStartKind:
                    // anchor, tag, implicit, style
                    return new SequenceStartEvent(ToClrString(ev[1]), IsTrue(ev[3]) ? null : ToClrString(ev[2]), ToFlowStyle(ev[4]));

                case SequenceEndKind:
                    return SequenceEndEvent.Instance;

                case MappingStartKind:
                    return new MappingStartEvent(ToClrString(ev[1]), IsTrue(ev[3]) ? null : ToClrString(ev[2]), ToFlowStyle(ev[4]));

                case MappingEndKind:
                    return MappingEndEvent.Instance;

                default:
                    throw new PsychException("unknown emitter event");
            }
        }

        private static bool IsTrue(object obj) {
            return Protocols.IsTrue(obj);
        }

        private static int ToInt(object obj) {
            return (obj is int) ? (int)obj : 0;
        }

        private static string ToClrString(object obj) {
            MutableString str = obj as MutableString;
            return (str != null) ? str.ConvertToString() : null;
        }

        private static Version ToVersion(object obj) {
            IList version = obj as IList;
            if (version == null || version.Count < 2) {
                return null;
            }
            return new Version(ToInt(version[0]), ToInt(version[1]));
        }

        // Psych::Nodes::Scalar's styles, in its own order.
        private static ScalarQuotingStyle ToQuotingStyle(object obj) {
            switch (ToInt(obj)) {
                case 2: return ScalarQuotingStyle.Single;
                case 3: return ScalarQuotingStyle.Double;
                case 4: return ScalarQuotingStyle.Literal;
                case 5: return ScalarQuotingStyle.Folded;
                default: return ScalarQuotingStyle.None;
            }
        }

        // Psych::Nodes::Sequence::FLOW and Mapping::FLOW are both 2.
        private static FlowStyle ToFlowStyle(object obj) {
            return (ToInt(obj) == 2) ? FlowStyle.Inline : FlowStyle.Block;
        }
    }
}
