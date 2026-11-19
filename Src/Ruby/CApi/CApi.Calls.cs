/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using VALUE = System.UInt64;

namespace IronRuby.Runtime.CApi {
    /// <summary>
    /// A method an extension defined: a C function pointer and the arity rb_define_method was
    /// given (-1: argc/argv/self, -2: self and an Array of the arguments, 0..15: positional).
    /// </summary>
    public sealed class CMethod {
        internal readonly IntPtr Function;
        internal readonly int Arity;
        internal readonly string Name;
        internal RubyModule Owner;

        internal CMethod(IntPtr function, int arity, string name) {
            Function = function;
            Arity = arity;
            Name = name;
        }

        /// <summary>
        /// Called by the rule CApi.MakeRule builds. <paramref name="scope"/> is the caller's, whose
        /// $~ and $_ rb_backref_get and rb_lastline_get read, as CRuby's read the caller's frame.
        /// </summary>
        public static object Invoke(CMethod/*!*/ method, RubyScope/*!*/ scope, object self, object[]/*!*/ args, Proc block) {
            return CApi.InvokeMethod(method, scope, self, args, block);
        }

        internal static readonly MethodInfo InvokeMethod = typeof(CMethod).GetMethod("Invoke");
    }

    internal static unsafe partial class CApi {
        /// <summary>
        /// What a method written in C can ask about its own call: rb_block_given_p, rb_yield,
        /// rb_call_super...
        /// </summary>
        private sealed class CFrame {
            public CFrame Parent;
            public CMethod Method;
            // the caller's: its $~ and $_ are the C method's (rb_backref_get, rb_lastline_get)
            public RubyScope Scope;
            public object Self;
            public object[] Args;
            public Proc Block;
            public BlockParam Bfc;
            // called with keywords (rb_keyword_given_p): the last argument was the call's keyword hash
            public bool KeywordGiven;
        }

        [ThreadStatic] private static CFrame _frame;
        // rb_iterate's block, for the next call rb_funcall makes
        [ThreadStatic] private static Proc _passedBlock;

        /// <summary>
        /// A break out of a block yielded to by C code: unwinds the C method and makes the break
        /// value its result, as CRuby's TAG_BREAK does.
        /// </summary>
        private sealed class CBlockBreak : StackUnwinder {
            public readonly BlockParam Bfc;
            public CBlockBreak(BlockParam bfc, object value) : base(value) { Bfc = bfc; }
        }

        #region Methods

        internal static RuleGenerator/*!*/ MakeRule(CMethod/*!*/ method) {
            return delegate(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
                int min = method.Arity >= 0 ? method.Arity : 0;
                int max = method.Arity >= 0 ? method.Arity : Int32.MaxValue;
                var actualArgs = RubyOverloadResolver.NormalizeArguments(metaBuilder, args, min, max);
                if (metaBuilder.Error) {
                    return;
                }
                Expression block = args.Signature.HasBlock ?
                    AstUtils.Convert(args.GetBlockExpression(), typeof(Proc)) :
                    (Expression)AstUtils.Constant(null, typeof(Proc));

                metaBuilder.Result = Expression.Call(
                    CMethod.InvokeMethod,
                    AstUtils.Constant(method),
                    AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope)),
                    AstUtils.Box(args.TargetExpression),
                    Expression.NewArrayInit(typeof(object), actualArgs.Select(a => AstUtils.Box(a.Expression))),
                    block
                );
            };
        }

        internal static object InvokeMethod(CMethod/*!*/ method, RubyScope/*!*/ scope, object self, object[]/*!*/ args, Proc block) {
            var frame = new CFrame { Parent = _frame, Method = method, Scope = scope, Self = self, Args = args, Block = block };
            if (block != null) {
                frame.Bfc = RubyOps.CreateBfcForLibraryMethod(block);
            }
            if (args.Length > 0 && RubyOps.IsKeywordArgumentsHash(args[args.Length - 1])) {
                // the C method sees a plain Hash: passed on positionally it is not keywords again
                frame.KeywordGiven = true;
                RubyOps.ClearKeywordArguments(args[args.Length - 1]);
            }
            _frame = frame;
            try {
                VALUE selfValue = ToValue(self);
                VALUE result;
                if (method.Arity == -2) {
                    VALUE array = ToValue(new RubyArray(args));
                    result = CallMethod(method.Function, -2, selfValue, 1, &array);
                } else {
                    int argc = args.Length;
                    VALUE* argv = stackalloc VALUE[Math.Max(argc, 1)];
                    for (int i = 0; i < argc; i++) {
                        argv[i] = ToValue(args[i]);
                    }
                    result = CallMethod(method.Function, method.Arity, selfValue, argc, argv);
                }
                return FromValue(result);
            } catch (CBlockBreak b) when (b.Bfc == frame.Bfc && frame.Bfc != null) {
                return b.ReturnValue;
            } catch (MethodUnwinder unwinder) when (frame.Bfc != null && RubyOps.IsProcConverterTarget(frame.Bfc, unwinder)) {
                return unwinder.ReturnValue;
            } finally {
                _frame = frame.Parent;
                if (frame.Bfc != null) {
                    RubyOps.LeaveProcConverter(frame.Bfc);
                }
            }
        }

        private static void DefineMethod(RubyModule/*!*/ module, string/*!*/ name, IntPtr func, int arity, RubyMemberFlags visibility) {
            if (arity < -2 || arity > 15) {
                throw RubyExceptions.CreateArgumentError(String.Format("arity out of range: {0} for -2..15", arity));
            }
            // MRI's rb_define_method makes these private whatever it is asked (vm_method.c)
            if (name == "initialize" || name == "initialize_copy" || name == "initialize_clone" ||
                name == "initialize_dup" || name == "respond_to_missing?") {
                visibility = RubyMemberFlags.Private;
            }
            var method = new CMethod(func, arity, name) { Owner = module };
            module.AddMethod(_context, name, new RubyCustomMethodInfo(MakeRule(method), visibility, module, arity >= 0 ? arity : -1));
        }

        private static RubyModule/*!*/ ToModule(VALUE value) {
            var module = FromValue(value) as RubyModule;
            if (module == null) {
                throw RubyExceptions.CreateTypeError(String.Format("{0} is not a class/module", Inspect(FromValue(value))));
            }
            return module;
        }

        internal static void rb_define_method(VALUE klass, byte* mid, IntPtr func, int arity) {
            DefineMethod(ToModule(klass), FromCString(mid), func, arity, RubyMemberFlags.Public);
        }

        internal static void rb_define_private_method(VALUE klass, byte* mid, IntPtr func, int arity) {
            DefineMethod(ToModule(klass), FromCString(mid), func, arity, RubyMemberFlags.Private);
        }

        internal static void rb_define_protected_method(VALUE klass, byte* mid, IntPtr func, int arity) {
            DefineMethod(ToModule(klass), FromCString(mid), func, arity, RubyMemberFlags.Protected);
        }

        internal static void rb_define_singleton_method(VALUE obj, byte* mid, IntPtr func, int arity) {
            DefineMethod(_context.GetOrCreateSingletonClass(FromValue(obj)), FromCString(mid), func, arity, RubyMemberFlags.Public);
        }

        internal static void rb_define_module_function(VALUE klass, byte* mid, IntPtr func, int arity) {
            var module = ToModule(klass);
            string name = FromCString(mid);
            DefineMethod(module, name, func, arity, RubyMemberFlags.Private);
            DefineMethod(module.GetOrCreateSingletonClass(), name, func, arity, RubyMemberFlags.Public);
        }

        internal static void rb_define_global_function(byte* mid, IntPtr func, int arity) {
            rb_define_module_function(ToValue(_context.KernelModule), mid, func, arity);
        }

        internal static void rb_undef_method(VALUE klass, byte* name) {
            ToModule(klass).UndefineMethod(FromCString(name));
        }

        internal static void rb_define_alias(VALUE klass, byte* dst, byte* src) {
            Send(ToModule(klass), "alias_method", null, _context.EncodeIdentifier(FromCString(dst)), _context.EncodeIdentifier(FromCString(src)));
        }

        internal static void rb_define_attr(VALUE klass, byte* name, int read, int write) {
            var module = ToModule(klass);
            var sym = _context.EncodeIdentifier(FromCString(name));
            if (read != 0) {
                Send(module, "attr_reader", null, sym);
            }
            if (write != 0) {
                Send(module, "attr_writer", null, sym);
            }
        }

        // the C function behind each class's allocator, for rb_get_alloc_func
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RubyClass, StrongBox<IntPtr>> _allocFuncs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<RubyClass, StrongBox<IntPtr>>();

        internal static void rb_define_alloc_func(VALUE klass, IntPtr func) {
            var cls = FromValue(klass) as RubyClass;
            if (cls == null) {
                throw RubyExceptions.CreateTypeError("allocator for a module");
            }
            _allocFuncs.AddOrUpdate(cls, new StrongBox<IntPtr>(func));
            cls.SetCAllocator(c => {
                object obj = FromValue(CallFunc1(func, ToValue(c)));
                if (obj == null || !ReferenceEquals(_context.GetClassOf(obj).GetNonSingletonClass(), c) && !(obj is DataObject)) {
                    throw RubyExceptions.CreateTypeError(String.Format("wrong instance allocation"));
                }
                return obj;
            });
        }

        internal static void rb_undef_alloc_func(VALUE klass) {
            var cls = FromValue(klass) as RubyClass;
            if (cls != null) {
                _allocFuncs.AddOrUpdate(cls, new StrongBox<IntPtr>(IntPtr.Zero));
                cls.SetCAllocator(c => {
                    throw RubyExceptions.CreateTypeError(String.Format("allocator undefined for {0}", c.Name));
                });
            }
        }

        internal static IntPtr GetAllocFunc(RubyClass/*!*/ cls) {
            for (RubyClass c = cls; c != null; c = c.SuperClass) {
                StrongBox<IntPtr> f;
                if (_allocFuncs.TryGetValue(c, out f)) {
                    return f.Value;
                }
            }
            // CRuby: every class has one; this one allocates as the class does by itself
            return _defaultAllocator;
        }

        private static IntPtr _defaultAllocator;

        internal static VALUE rb_ir_allocate_builtin(VALUE klass) {
            var cls = FromValue(klass) as RubyClass;
            if (cls == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type (expected Class)");
            }
            return ToValue(cls.AllocateBuiltin());
        }

        /// <summary>
        /// A new T_DATA object: rb_data_object_wrap and rb_data_typed_object_wrap (native/capi.c)
        /// fill its slot in.
        /// </summary>
        internal static VALUE rb_ir_data_new(VALUE klass, int typed) {
            var cls = klass == 0 ? _context.ObjectClass : FromValue(klass) as RubyClass;
            if (cls == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type (expected Class)");
            }
            var obj = new DataObject(cls.GetNonSingletonClass(), typed != 0);
            VALUE value = ToValue(obj);
            if (klass == 0) {
                rb_obj_hide(value);
            }
            return value;
        }

        #endregion

        #region Calling Ruby

        private static readonly ConcurrentDictionary<(string, bool), CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>> _sites =
            new ConcurrentDictionary<(string, bool), CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>>();

        /// <summary>
        /// Calls a method the way rb_funcall does: private methods too, unless publicOnly.
        /// </summary>
        internal static object Send(object target, string/*!*/ name, Proc block, params object[]/*!*/ args) {
            return SendWith(false, target, name, block, args);
        }

        internal static object SendWith(bool publicOnly, object target, string/*!*/ name, Proc block, object[]/*!*/ args) {
            var site = _sites.GetOrAdd((name, publicOnly), key => CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>.Create(
                RubyCallAction.Make(_context, key.Item1, new RubyCallSignature(0,
                    (key.Item2 ? RubyCallFlags.None : RubyCallFlags.HasImplicitSelf) | RubyCallFlags.HasScope |
                    RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock))));
            // A C method has no Ruby frame of its own: what the method called reads or sets in its
            // caller's frame - $~ by Regexp#match, $_ by gets - is the Ruby frame that called the C
            // method, as in CRuby (the svar of the nearest Ruby-level frame).
            RubyScope scope = (_frame != null ? _frame.Scope : null) ?? _context.EmptyScope;
            return site.Target(site, scope, target, block, new RubyArray(args));
        }

        private static Proc TakePassedBlock() {
            var block = _passedBlock;
            _passedBlock = null;
            return block;
        }

        internal static VALUE rb_funcallv(VALUE recv, VALUE mid, int argc, VALUE* argv) {
            return ToValue(Send(FromValue(recv), IdToString(mid), TakePassedBlock(), FromValues(argc, argv)));
        }

        internal static VALUE rb_apply(VALUE recv, VALUE mid, VALUE args) {
            return ToValue(Send(FromValue(recv), IdToString(mid), TakePassedBlock(), ToRubyArray(args).ToArray()));
        }

        internal static VALUE rb_funcallv_kw(VALUE recv, VALUE mid, int argc, VALUE* argv, int kw_splat) {
            return ToValue(Send(FromValue(recv), IdToString(mid), TakePassedBlock(), KeywordArgs(argc, argv, kw_splat)));
        }

        private const int RB_PASS_KEYWORDS = 1, RB_PASS_CALLED_KEYWORDS = 3;

        /// <summary>
        /// The arguments of a *_kw call: with RB_PASS_KEYWORDS the last one is the keywords, a
        /// Hash (or #to_hash of one) the callee receives as such - IronRuby's keyword call passes
        /// a copy marked Hash.IsKeywordArguments - and none at all when it is empty.
        /// RB_PASS_CALLED_KEYWORDS does whatever the calling C method was given.
        /// </summary>
        private static object[]/*!*/ KeywordArgs(int argc, VALUE* argv, int kw_splat) {
            return KeywordArgs(FromValues(argc, argv), kw_splat);
        }

        private static object[]/*!*/ KeywordArgs(object[]/*!*/ args, int kw_splat) {
            if (kw_splat == RB_PASS_CALLED_KEYWORDS) {
                kw_splat = rb_keyword_given_p();
            }
            if (kw_splat != RB_PASS_KEYWORDS || args.Length == 0) {
                return args;
            }
            object last = args[args.Length - 1];
            var hash = last as IDictionary<object, object>;
            if (hash == null && RespondTo(last, "to_hash")) {
                object converted = Send(last, "to_hash", null);
                hash = converted as IDictionary<object, object>;
                if (hash == null) {
                    throw RubyExceptions.CreateTypeError(String.Format("can't convert {0} into Hash ({0}#to_hash gives {1})",
                        _context.GetClassDisplayName(last), _context.GetClassDisplayName(converted)));
                }
            } else if (hash == null) {
                throw RubyExceptions.CreateTypeError(String.Format("no implicit conversion of {0} into Hash", _context.GetClassDisplayName(last)));
            }
            if (hash.Count == 0) {
                Array.Resize(ref args, args.Length - 1);
                return args;
            }
            var keywords = new Hash(hash, _context.EqualityComparer);
            keywords.IsKeywordArguments = true;
            args[args.Length - 1] = keywords;
            return args;
        }

        internal static VALUE rb_funcallv_public(VALUE recv, VALUE mid, int argc, VALUE* argv) {
            return ToValue(SendWith(true, FromValue(recv), IdToString(mid), TakePassedBlock(), FromValues(argc, argv)));
        }

        internal static VALUE rb_funcall_passing_block(VALUE recv, VALUE mid, int argc, VALUE* argv) {
            return ToValue(SendWith(true, FromValue(recv), IdToString(mid), _frame?.Block, FromValues(argc, argv)));
        }

        internal static VALUE rb_funcall_with_block(VALUE recv, VALUE mid, int argc, VALUE* argv, VALUE procval) {
            var block = FromValue(procval) as Proc;
            return ToValue(SendWith(true, FromValue(recv), IdToString(mid), block, FromValues(argc, argv)));
        }

        internal static VALUE rb_call_super(int argc, VALUE* argv) {
            return CallSuper(FromValues(argc, argv));
        }

        internal static VALUE rb_call_super_kw(int argc, VALUE* argv, int kw_splat) {
            return CallSuper(KeywordArgs(argc, argv, kw_splat));
        }

        private static VALUE CallSuper(object[]/*!*/ args) {
            var frame = _frame;
            if (frame == null) {
                throw RubyExceptions.CreateRuntimeError("super called outside of method");
            }
            object unbound = Send(frame.Method.Owner, "instance_method", null, _context.EncodeIdentifier(frame.Method.Name));
            object super = Send(unbound, "super_method", null);
            if (super == null) {
                // as a super with nothing above it: #method_missing, whose default raises NoMethodError
                var missing = new List<object> { _context.EncodeIdentifier(frame.Method.Name) };
                missing.AddRange(args);
                return ToValue(Send(frame.Self, "method_missing", frame.Block, missing.ToArray()));
            }
            var call = new List<object> { frame.Self };
            call.AddRange(args);
            return ToValue(Send(super, "bind_call", frame.Block, call.ToArray()));
        }

        internal static void rb_obj_call_init(VALUE obj, int argc, VALUE* argv) {
            Send(FromValue(obj), "initialize", _frame?.Block, FromValues(argc, argv));
        }

        internal static VALUE rb_class_new_instance(int argc, VALUE* argv, VALUE klass) {
            return ToValue(Send(FromValue(klass), "new", null, FromValues(argc, argv)));
        }

        internal static VALUE rb_obj_alloc(VALUE klass) {
            return ToValue(Send(FromValue(klass), "allocate", null));
        }

        internal static VALUE rb_eval_string(byte* str) {
            return ToValue(Send(_context.KernelModule, "eval", null, MutableString.CreateBinary(CBytes(str), RubyEncoding.UTF8)));
        }

        internal static VALUE rb_obj_instance_eval(int argc, VALUE* argv, VALUE recv) {
            return ToValue(Send(FromValue(recv), "instance_eval", _frame?.Block, FromValues(argc, argv)));
        }

        internal static VALUE rb_require(byte* feature) {
            return ToValue(Send(_context.KernelModule, "require", null, MutableString.CreateBinary(CBytes(feature), RubyEncoding.UTF8)));
        }

        internal static VALUE rb_each(VALUE obj) {
            return rb_funcallv(obj, Intern("each"), 0, null);
        }

        #endregion

        #region Blocks

        internal static int rb_block_given_p() {
            return _frame?.Block != null ? 1 : 0;
        }

        internal static int rb_keyword_given_p() {
            return _frame != null && _frame.KeywordGiven ? 1 : 0;
        }

        internal static void rb_need_block() {
            if (_frame?.Block == null) {
                throw RubyExceptions.NoBlockGiven();
            }
        }

        internal static VALUE rb_block_proc() {
            var block = _frame?.Block;
            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }
            return ToValue(block);
        }

        private static VALUE Yield(object[]/*!*/ args) {
            var bfc = _frame?.Bfc;
            if (bfc == null) {
                throw RubyExceptions.NoBlockGiven();
            }
            object result;
            if (bfc.Yield(args, out result)) {
                throw new CBlockBreak(bfc, result);
            }
            return ToValue(result);
        }

        internal static VALUE rb_yield(VALUE val) {
            return Yield(val == Qundef ? Array.Empty<object>() : new[] { FromValue(val) });
        }

        internal static VALUE rb_yield_values2(int n, VALUE* argv) {
            return Yield(FromValues(n, argv));
        }

        // A C function as a block (rb_block_call): VALUE f(yielded_arg, data2, argc, argv, blockarg).
        /// <summary>
        /// A block whose body is a rb_block_call_func: |*args, &amp;blockarg|, arity -1, as CRuby's
        /// ifunc blocks are.
        /// </summary>
        private static Proc/*!*/ MakeBlock(IntPtr fn, VALUE data2) {
            var dispatcher = new BlockDispatcherUnsplatProcN(0,
                BlockDispatcher.MakeAttributes(BlockSignatureAttributes.HasUnsplatParameter | BlockSignatureAttributes.HasProcParameter, -1),
                null, 0);
            dispatcher.SetMethod(new Func<BlockParam, object, object[], RubyArray, Proc, object>(
                (selfBlock, self, unused, args, blockArg) => {
                    int argc = args.Count;
                    VALUE* argv = stackalloc VALUE[Math.Max(argc, 1)];
                    for (int i = 0; i < argc; i++) {
                        argv[i] = ToValue(args[i]);
                    }
                    VALUE yielded = argc > 0 ? argv[0] : Qnil;
                    _cBlockDepth++;
                    try {
                        return FromValue(CallBlock(fn, yielded, data2, argc, argv, ToValue(blockArg)));
                    } catch (CIterBreak b) {
                        return selfBlock.Break(b.ReturnValue);
                    } finally {
                        _cBlockDepth--;
                    }
                }));
            return new Proc(ProcKind.Block, null, _context.EmptyScope, dispatcher);
        }

        // blocks written in C running on this thread: what rb_iter_break can break out of
        [ThreadStatic] private static int _cBlockDepth;

        private sealed class CIterBreak : StackUnwinder {
            public CIterBreak(object value) : base(value) { }
        }

        internal static VALUE rb_block_call(VALUE obj, VALUE mid, int argc, VALUE* argv, IntPtr proc, VALUE data2) {
            Proc block = proc != IntPtr.Zero ? MakeBlock(proc, data2) : _frame?.Block;
            return ToValue(Send(FromValue(obj), IdToString(mid), block, FromValues(argc, argv)));
        }

        internal static VALUE rb_iterate(IntPtr func1, VALUE data1, IntPtr proc, VALUE data2) {
            var saved = _passedBlock;
            _passedBlock = proc != IntPtr.Zero ? MakeBlock(proc, data2) : _frame?.Block;
            try {
                return CallFunc1(func1, data1);
            } finally {
                _passedBlock = saved;
            }
        }

        internal static void rb_iter_break() {
            rb_iter_break_value(Qnil);
        }

        internal static void rb_iter_break_value(VALUE val) {
            if (_cBlockDepth == 0) {
                // a break out of a Ruby block from C called inside it: IronRuby's break needs the
                // block's own frame, which a C function is not given
                throw RubyExceptions.CreateNotImplementedError("rb_iter_break outside a block written in C is not implemented by IronRuby's C API yet");
            }
            throw new CIterBreak(FromValue(val));
        }

        #endregion
    }
}
