/* ****************************************************************************
 *
 * The census: what the method JIT and the light frames make of every method of a set of
 * files, found without running any of them (IronRuby.jit_census, Util/perf/census.rb).
 *
 * Each file is parsed and each `def' in it goes through the decisions it would go through
 * the first time it was called - the body's transformation, which says whether it runs
 * without a scope (MethodDefinition.TransformBody), and the JIT's static screen
 * (JitStub.Wrap) - and then through the one decision that normally waits for a profile:
 * the typed body is compiled for parameters that are all Integers, all Floats and all
 * objects, with nothing known of the receiver (JitCompiler.TryCompile).
 *
 * So a method is one of three things to the JIT:
 *   never     the parameter list or the screen refuses it: no profile can make it compile
 *   compiles  a typed body exists for one of the three typings, whatever the receiver is
 *   maybe     it passes the screen, and whether it compiles is the profile's to say - the
 *             receiver's class and instance variables, the constants, the callees
 * `never' and `compiles' are bounds; they say nothing of how often a method is called, or
 * of the deopts a typed body takes once it runs.
 *
 * What the census cannot see is the class a method ends up in. A call the screen asks
 * about ("may the typed body inline it?") is answered from the methods the files define
 * in the same class or module body, then from what Object has in this process, and a
 * name neither knows is taken as JitStub.IsLeafMethod takes a method not defined yet.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Jit {
    using MSA = System.Linq.Expressions;
    using MethodDeclaration = IronRuby.Compiler.Ast.MethodDefinition;

    public sealed class JitCensus {
        /// <summary>
        /// The census this thread is taking, if it is: the compiler keeps its reasons for it as it does for
        /// IR_JIT_VERBOSE (JitCompiler.Bail, JitScreen.No).
        /// </summary>
        [ThreadStatic] internal static JitCensus Current;

        // Why the typed body was not compiled (JitCompiler.TryCompile).
        internal string BailReason;
        // Why the body being transformed is not a light frame; null if it is one (MethodDefinition.TransformBody).
        internal string NotLightFrame;
        // The blocks of the body being transformed, and those of them that run without a scope (BlockDefinition).
        private int _blocks, _lightBlocks;

        internal void CountBlock(bool withoutScope) {
            _blocks++;
            if (withoutScope) { _lightBlocks++; }
        }

        private sealed class Entry {
            internal MethodDeclaration/*!*/ Ast;
            internal string/*!*/ File;
            // the class or module body the `def' is written in, "Object" at the top level
            internal string/*!*/ Module;
            // `def self.x', `def o.x', or a `def' in a `class << self' body
            internal bool Singleton;
            internal RubyEncoding/*!*/ Encoding;
            internal MSA.SymbolDocumentInfo Document;
        }

        private readonly RubyContext/*!*/ _context;
        private readonly List<Entry>/*!*/ _entries = new List<Entry>();
        // "A::B#name", "A::B.name" - the last definition, as the class would have it
        private readonly Dictionary<string, MethodDeclaration>/*!*/ _defined = new Dictionary<string, MethodDeclaration>();
        private readonly Dictionary<string, List<Entry>>/*!*/ _byName = new Dictionary<string, List<Entry>>();

        private JitCensus(RubyContext/*!*/ context) {
            _context = context;
        }

        private static string/*!*/ Key(string/*!*/ module, bool singleton, string/*!*/ name) {
            return module + (singleton ? "." : "#") + name;
        }

        /// <summary>
        /// One Hash for each method defined in the files - :file, :line, :lines, :module, :name, :singleton, :light and
        /// :light_why, :blocks and :light_blocks, :jit ("never", "maybe", "compiles") with :jit_stage ("parameters",
        /// "screen", "emit"), :jit_kind, :jit_why and :jit_types - and one with :file and :error for each file that
        /// could not be parsed. Nothing in the files is run.
        /// </summary>
        public static RubyArray/*!*/ Run(RubyContext/*!*/ context, IEnumerable<string>/*!*/ paths) {
            var census = new JitCensus(context);
            var result = new RubyArray();
            foreach (string path in paths) {
                string error = census.Collect(path);
                if (error != null) {
                    var failed = new Hash(context);
                    failed[context.CreateAsciiSymbol("file")] = Str(path);
                    failed[context.CreateAsciiSymbol("error")] = Str(error);
                    result.Add(failed);
                }
            }

            JitCensus outer = Current;
            Current = census;
            try {
                foreach (var entry in census._entries) {
                    result.Add(census.Examine(entry));
                }
            } finally {
                Current = outer;
            }
            return result;
        }

        private static MutableString/*!*/ Str(string/*!*/ value) {
            return MutableString.Create(value, RubyEncoding.UTF8);
        }

        #region Parsing

        // The methods of one file; what went wrong, if it could not be read or parsed.
        private string Collect(string/*!*/ path) {
            try {
                var unit = _context.CreateFileUnit(path, RubyEncoding.Ascii.Encoding, SourceCodeKind.File);
                var errors = new ErrorCounter();
                var tree = RubyContext.EffectiveParser(unit, new RubyCompilerOptions { FactoryKind = TopScopeFactoryKind.File }, errors);
                if (tree == null || errors.AnyError) {
                    return "syntax error";
                }
                var collector = new Collector(this, path, tree.Encoding, unit.Document);
                foreach (var statement in tree.Statements) {
                    collector.Walk(statement);
                }
                return null;
            } catch (Exception e) {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        private void Add(Entry/*!*/ entry) {
            _entries.Add(entry);
            _defined[Key(entry.Module, entry.Singleton, entry.Ast.Name)] = entry.Ast;
            List<Entry> named;
            if (!_byName.TryGetValue(entry.Ast.Name, out named)) {
                _byName.Add(entry.Ast.Name, named = new List<Entry>());
            }
            named.Add(entry);
        }

        private sealed class Collector : Walker {
            private readonly JitCensus/*!*/ _census;
            private readonly string/*!*/ _file;
            private readonly RubyEncoding/*!*/ _encoding;
            private readonly MSA.SymbolDocumentInfo _document;

            // the class and module bodies the walk is in: the name each was opened by, and whether it is a `class << self'
            private readonly List<string>/*!*/ _modules = new List<string>();
            private readonly List<bool>/*!*/ _singletons = new List<bool>();

            internal Collector(JitCensus/*!*/ census, string/*!*/ file, RubyEncoding/*!*/ encoding, MSA.SymbolDocumentInfo document) {
                _census = census;
                _file = file;
                _encoding = encoding;
                _document = document;
            }

            private string/*!*/ Module {
                get { return (_modules.Count == 0) ? "Object" : String.Join("::", _modules); }
            }

            private bool InSingleton {
                get { return _singletons.Count > 0 && _singletons[_singletons.Count - 1]; }
            }

            // `A::B' as it is written; a qualifier that is not a constant (`obj::B') is "?"
            private static string/*!*/ QualifiedName(ConstantVariable/*!*/ constant) {
                var qualifier = constant.Qualifier as ConstantVariable;
                if (qualifier != null) { return QualifiedName(qualifier) + "::" + constant.Name; }
                return (constant.Qualifier != null) ? "?::" + constant.Name : constant.Name;
            }

            private bool Open(ModuleDefinition/*!*/ node) {
                var singleton = node as SingletonDefinition;
                if (singleton == null) {
                    _modules.Add(QualifiedName(node.QualifiedName));
                    _singletons.Add(false);
                } else {
                    // `class << self' is the enclosing module's singleton class; `class << o' is some object's
                    if (!(singleton.Singleton is SelfReference)) { _modules.Add("?"); }
                    _singletons.Add(true);
                }
                return true;
            }

            private void Close(ModuleDefinition/*!*/ node) {
                var singleton = node as SingletonDefinition;
                if (singleton == null || !(singleton.Singleton is SelfReference)) { _modules.RemoveAt(_modules.Count - 1); }
                _singletons.RemoveAt(_singletons.Count - 1);
            }

            public override bool Enter(ModuleDefinition/*!*/ node) { return Open(node); }
            public override void Exit(ModuleDefinition/*!*/ node) { Close(node); }
            public override bool Enter(ClassDefinition/*!*/ node) { return Open(node); }
            public override void Exit(ClassDefinition/*!*/ node) { Close(node); }
            public override bool Enter(SingletonDefinition/*!*/ node) { return Open(node); }
            public override void Exit(SingletonDefinition/*!*/ node) { Close(node); }

            public override bool Enter(MethodDeclaration/*!*/ node) {
                string module = Module;
                bool singleton = InSingleton;
                if (node.Target != null) {
                    // `def self.x' is the enclosing module's; `def o.x' is some object's
                    if (!(node.Target is SelfReference)) { module = "?"; }
                    singleton = true;
                }
                _census.Add(new Entry {
                    Ast = node, File = _file, Module = module, Singleton = singleton, Encoding = _encoding, Document = _document
                });
                // a `def' in the body defines a method of the same class
                return true;
            }
        }

        #endregion

        #region The decisions

        private Hash/*!*/ Examine(Entry/*!*/ entry) {
            var ast = entry.Ast;
            var hash = new Hash(_context);
            Set(hash, "file", Str(entry.File));
            Set(hash, "line", ast.Location.Start.Line);
            Set(hash, "lines", ast.Location.End.Line - ast.Location.Start.Line + 1);
            Set(hash, "module", Str(entry.Module));
            Set(hash, "name", Str(ast.Name));
            Set(hash, "singleton", entry.Singleton);

            // The light frame: the transformation the first call would make, which decides it (and builds a body that
            // is defined on demand, before the screen looks at it). Its scope and module are only constants of the code.
            NotLightFrame = null;
            _blocks = _lightBlocks = 0;
            try {
                var gen = new AstGenerator(_context, new RubyCompilerOptions(), entry.Document, entry.Encoding, false);
                gen.Census = this;
                ast.TransformBody(gen, _context.EmptyScope, _context.ObjectClass, null);
            } catch (Exception e) {
                NotLightFrame = "not transformed: " + e.GetType().Name;
            }
            Set(hash, "light", NotLightFrame == null);
            Set(hash, "light_why", (NotLightFrame != null) ? Str(NotLightFrame) : null);
            Set(hash, "blocks", _blocks);
            Set(hash, "light_blocks", _lightBlocks);

            string verdict, stage, kind, why, types = null;
            try {
                Decide(entry, out verdict, out stage, out kind, out why, out types);
            } catch (Exception e) {
                verdict = "never";
                stage = "screen";
                kind = why = "census failed: " + e.GetType().Name;
            }
            Set(hash, "jit", Str(verdict));
            Set(hash, "jit_stage", (stage != null) ? Str(stage) : null);
            Set(hash, "jit_kind", (kind != null) ? Str(kind) : null);
            Set(hash, "jit_why", (why != null) ? Str(why) : null);
            Set(hash, "jit_types", (types != null) ? Str(types) : null);
            return hash;
        }

        private void Set(Hash/*!*/ hash, string/*!*/ key, object value) {
            hash[_context.CreateAsciiSymbol(key)] = value;
        }

        private static readonly JT[] ProbeTypes = new[] { JT.Int, JT.Dbl, JT.Obj };

        // JitStub.Wrap's decision, then JitStub.TrySpecialize's for each of the probe's typings.
        private void Decide(Entry/*!*/ entry, out string/*!*/ verdict, out string stage, out string kind, out string why, out string types) {
            var ast = entry.Ast;
            types = null;

            string shape = JitStub.ParameterShape(ast);
            if (shape != null) {
                verdict = "never";
                stage = "parameters";
                kind = why = shape;
                return;
            }

            JitKeywords keywords;
            int typedCount, perCountMandatory;
            string rejectedBy;
            JitScreen.LastRejectionDetail = null;
            if (!JitStub.CensusScreen(ast, _context, Callee(entry), out keywords, out typedCount, out perCountMandatory, out rejectedBy)) {
                verdict = "never";
                if (rejectedBy != null) {
                    stage = "screen";
                    kind = rejectedBy;
                    why = JitScreen.LastRejectionDetail ?? rejectedBy;
                } else {
                    stage = "parameters";
                    kind = why = "optional or keyword parameters";
                }
                return;
            }

            string firstReason = null;
            foreach (JT type in ProbeTypes) {
                BailReason = null;
                bool compiled;
                try {
                    compiled = Probe(ast, keywords, type, typedCount, perCountMandatory);
                } catch (Exception e) {
                    compiled = false;
                    BailReason = "probe failed: " + e.GetType().Name;
                }
                if (compiled) {
                    verdict = "compiles";
                    stage = kind = why = null;
                    types = (typedCount == 0) ? "" : type.ToString();
                    return;
                }
                // (no bailout: the body was typed and then declined - a deopt after an effect, a return type that did not settle)
                if (firstReason == null) { firstReason = BailReason ?? "declined once typed"; }
                // without typed parameters the three typings are one
                if (typedCount == 0) { break; }
            }

            verdict = "maybe";
            stage = "emit";
            why = firstReason;
            // "call: m/2", "node: YieldCall", "to_i on Obj": the kind is what comes before the name or the types
            int colon = firstReason.IndexOf(':');
            kind = (colon > 0 && !firstReason.StartsWith("node:", StringComparison.Ordinal) && !firstReason.StartsWith("literal:", StringComparison.Ordinal))
                ? firstReason.Substring(0, colon) : firstReason;
        }

        // Whether a typed body exists for parameters that are all of one type, the receiver unknown.
        private bool Probe(MethodDeclaration/*!*/ ast, JitKeywords keywords, JT type, int typedCount, int perCountMandatory) {
            if (perCountMandatory < 0) {
                return JitCompiler.TryCompile(ast, _context, Typing(type, typedCount), keywords, RefinementActivation.Empty) != null;
            }
            // a body for each number of optional arguments a call passes (JitStub.TrySpecializePerCount): one of them will do
            for (int k = 0; perCountMandatory + k <= typedCount; k++) {
                if (JitCompiler.TryCompile(ast, _context, Typing(type, perCountMandatory + k), null, RefinementActivation.Empty, k) != null) {
                    return true;
                }
            }
            return false;
        }

        private static JT[]/*!*/ Typing(JT type, int count) {
            var result = new JT[count];
            for (int i = 0; i < count; i++) { result[i] = type; }
            return result;
        }

        /// <summary>
        /// JitStub.IsLeafMethod without the declaring module: whether a call of a name in the entry's body is of a method
        /// its typed body may inline.
        /// </summary>
        private Func<string, bool, bool>/*!*/ Callee(Entry/*!*/ entry) {
            return (name, onSelf) => {
                // the method the class or module body the entry is written in defines
                MethodDeclaration defined;
                if (_defined.TryGetValue(Key(entry.Module, entry.Singleton, name), out defined)) {
                    return JitStub.IsLeafShape(defined, onSelf ? null : Nested(entry));
                }

                // what every class has from Object - or, for a singleton method, every class from Class - in this process
                RubyModule root = entry.Singleton ? (RubyModule)_context.ObjectClass.ImmediateClass : _context.ObjectClass;
                var resolved = root.ResolveMethod(name, VisibilityContext.AllVisible);
                if (resolved.Found) {
                    return JitStub.IsLeafMethod(resolved.Info as RubyMethodInfo, !onSelf);
                }

                // a superclass's or a mixin's, or another class's (`key.compare_to(k)'): any method of the name the files define
                List<Entry> named;
                if (_byName.TryGetValue(name, out named)) {
                    foreach (var other in named) {
                        if (JitStub.IsLeafShape(other.Ast, onSelf ? null : Nested(other))) { return true; }
                    }
                    return false;
                }
                return onSelf || JitStub.IsLeafMethodOfSomeClass(_context, name);
            };
        }

        // One level of nesting (JitStub.IsLeafMethod): a call on self in the callee, of a leaf its own class body defines.
        private Func<string, bool>/*!*/ Nested(Entry/*!*/ owner) {
            return name => {
                MethodDeclaration defined;
                return _defined.TryGetValue(Key(owner.Module, owner.Singleton, name), out defined) && JitStub.IsLeafShape(defined, null);
            };
        }

        #endregion
    }
}
