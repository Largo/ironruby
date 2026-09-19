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
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Runtime.Calls;

namespace IronRuby.StandardLibrary.Yaml {
    public static partial class RubyYaml {
        [RubyModule("Syck")]
        public static class Syck {
            [RubyClass("Emitter", Extends = typeof(RubyRepresenter), Inherits = typeof(Object), Restrictions = ModuleRestrictions.NoUnderlyingType)]
            public static class RepresenterOps {
                [RubyMethod("level")]
                public static int GetLevel(RubyRepresenter/*!*/ self) {
                    return self.Level;
                }

                [RubyMethod("level=")]
                public static int SetLevel(RubyRepresenter/*!*/ self, [DefaultProtocol]int level) {
                    return self.Level = level;
                }
            }

            [RubyClass("Out", Restrictions = ModuleRestrictions.NoUnderlyingType)]
            public sealed class Out {
                private RubyRepresenter/*!*/ _representer;

                internal Out(RubyRepresenter/*!*/ representer) {
                    Assert.NotNull(representer);
                    _representer = representer;
                }

                [RubyMethod("emitter")]
                public static RubyRepresenter/*!*/ GetEmitter(Out/*!*/ self) {
                    return self._representer;
                }

                [RubyMethod("emitter=")]
                public static RubyRepresenter/*!*/ SetEmitter(Out/*!*/ self, [NotNull]RubyRepresenter/*!*/ emitter) {
                    return self._representer = emitter;
                }

                [RubyMethod("map")]
                public static object CreateMap([NotNull]BlockParam/*!*/ block, Out/*!*/ self, [DefaultProtocol]MutableString taguri, object yamlStyle) {
                    var rep = self._representer;
                    var map = new MappingNode(rep.ToTag(taguri), new Dictionary<Node, Node>(), RubyYaml.ToYamlFlowStyle(yamlStyle));
                    
                    object blockResult;
                    if (block.Yield(map, out blockResult)) {
                        return blockResult;
                    }

                    return map;
                }

                [RubyMethod("seq")]
                public static object CreateSequence([NotNull]BlockParam/*!*/ block, Out/*!*/ self, [DefaultProtocol]MutableString taguri, object yamlStyle) {
                    var rep = self._representer;
                    var seq = new SequenceNode(rep.ToTag(taguri), new List<Node>(), RubyYaml.ToYamlFlowStyle(yamlStyle));

                    object blockResult;
                    if (block.Yield(seq, out blockResult)) {
                        return blockResult;
                    }

                    return seq;
                }

                // TODO: [RubyMethod("scalar")]
            }

            [RubyClass("Node", Extends = typeof(Node), Inherits = typeof(Object), Restrictions = ModuleRestrictions.NoUnderlyingType)]
            [Includes(typeof(BaseNode))]
            public sealed class NodeOps {
                // TODO: which of these we need to implement?
                // "resolver", "emitter=", "resolver=", "type_id", "kind", "type_id=", "emitter"

                [RubyMethod("transform")]
                public static object Transform(RubyScope/*!*/ scope, Node/*!*/ self) {
                    return new RubyConstructor(scope.GlobalScope, new SimpleNodeProvider(self, RubyYaml.GetEncoding(scope.RubyContext))).GetData();
                }

                // The resolved tag ("tag:yaml.org,2002:int", "tag:ruby.yaml.org,2002:object:Foo", ...),
                // which psych.rb reads to check a safe load against its permitted classes.
                [RubyMethod("tag")]
                public static MutableString GetTag(Node/*!*/ self) {
                    return self.Tag != null ? MutableString.Create(self.Tag, RubyEncoding.UTF8) : null;
                }
            }

            [RubyClass("Map", Extends = typeof(MappingNode), Inherits = typeof(Node), Restrictions = ModuleRestrictions.NoUnderlyingType)]
            public sealed class MapOps {
                [RubyMethod("style=")]
                public static object SetStyle(MappingNode/*!*/ self, object value) {
                    self.FlowStyle = RubyYaml.ToYamlFlowStyle(value);
                    return value;
                }

                [RubyMethod("value")]
                public static object GetValue(MappingNode/*!*/ self) {
                    return self.Nodes;
                }

                [RubyMethod("add")]
                public static void Add(YamlCallSiteStorage/*!*/ siteStorage, MappingNode/*!*/ self, object key, object value) {
                    RubyRepresenter rep = new RubyRepresenter(siteStorage);
                    self.Nodes.Add(rep.RepresentItem(key), rep.RepresentItem(value));
                }

                // Keys and values interleaved, as Psych::Nodes::Mapping#children has them.
                [RubyMethod("children")]
                public static RubyArray/*!*/ GetChildren(MappingNode/*!*/ self) {
                    var result = new RubyArray(self.Nodes.Count * 2);
                    foreach (var entry in self.Nodes) {
                        result.Add(entry.Key);
                        result.Add(entry.Value);
                    }
                    return result;
                }

                [RubyMethod("flow?")]
                public static bool IsFlow(MappingNode/*!*/ self) {
                    return self.FlowStyle == FlowStyle.Inline;
                }
            }

            [RubyClass("Seq", Extends = typeof(SequenceNode), Inherits = typeof(Node), Restrictions = ModuleRestrictions.NoUnderlyingType)]
            public sealed class SeqOps {
                // TODO: value=

                [RubyMethod("style=")]
                public static object SetStyle(SequenceNode/*!*/ self, object value) {
                    self.FlowStyle = RubyYaml.ToYamlFlowStyle(value);
                    return value;
                }

                [RubyMethod("value")]
                public static object GetValue(MappingNode/*!*/ self) {
                    return self.Nodes;
                }

                [RubyMethod("add")]
                public static void Add(YamlCallSiteStorage/*!*/ siteStorage, SequenceNode/*!*/ self, object value) {
                    RubyRepresenter rep = new RubyRepresenter(siteStorage);
                    self.Nodes.Add(rep.RepresentItem(value));
                }

                [RubyMethod("children")]
                public static RubyArray/*!*/ GetChildren(SequenceNode/*!*/ self) {
                    var result = new RubyArray(self.Nodes.Count);
                    foreach (var node in self.Nodes) {
                        result.Add(node);
                    }
                    return result;
                }

                [RubyMethod("flow?")]
                public static bool IsFlow(SequenceNode/*!*/ self) {
                    return self.FlowStyle == FlowStyle.Inline;
                }
            }

            [RubyClass("Scalar", Extends = typeof(ScalarNode), Inherits = typeof(Node), Restrictions = ModuleRestrictions.NoUnderlyingType)]
            public sealed class ScalarOps {
                // TODO: value=

                [RubyMethod("style=")]
                public static object SetStyle(RubyContext/*!*/ context, ScalarNode/*!*/ self, object value) {
                    self.Style = RubyYaml.ToYamlStyle(context, value);
                    return value;
                }

                [RubyMethod("value")]
                public static object GetValue(ScalarNode/*!*/ self) {
                    // An empty (null) scalar reads as "", as in Psych; the text is not always ASCII.
                    return MutableString.Create(self.Value ?? String.Empty, RubyEncoding.UTF8);
                }

                // Psych::Nodes::Scalar's style constants: PLAIN = 1, SINGLE_QUOTED = 2,
                // DOUBLE_QUOTED = 3, LITERAL = 4, FOLDED = 5.
                [RubyMethod("style")]
                public static int GetStyle(ScalarNode/*!*/ self) {
                    switch (self.Style) {
                        case ScalarQuotingStyle.Single: return 2;
                        case ScalarQuotingStyle.Double: return 3;
                        case ScalarQuotingStyle.Literal: return 4;
                        case ScalarQuotingStyle.Folded: return 5;
                        default: return 1;
                    }
                }
            }
        }
    }
}
