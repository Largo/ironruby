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
        /// Called by the rule CApi.MakeRule builds.
        /// </summary>
        public static object Invoke(CMethod/*!*/ method, object self, object[]/*!*/ args, Proc block) {
            return CApi.InvokeMethod(method, self, args, block);
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
            public object Self;
            public object[] Args;
            public Proc Block;
            public BlockParam Bfc;
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
                    AstUtils.Box(args.TargetExpression),
                    Expression.NewArrayInit(typeof(object), actualArgs.Select(a => AstUtils.Box(a.Expression))),
                    block
                );
            };
        }

        internal static object InvokeMethod(CMethod/*!*/ method, object self, object[]/*!*/ args, Proc block) {
            var frame = new CFrame { Parent = _frame, Method = method, Self = self, Args = args, Block = block };
            if (block != null) {
                frame.Bfc = RubyOps.CreateBfcForLibraryMethod(block);
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

        // TODO: an allocator written in C needs RubyClass to allocate through it (Class#allocate
        // and Class#new both build their own rule); T_DATA objects are the next milestone.
        internal static void rb_define_alloc_func(VALUE klass, IntPtr func) {
            throw RubyExceptions.CreateNotImplementedError("rb_define_alloc_func is not implemented by IronRuby's C API yet");
        }

        internal static void rb_undef_alloc_func(VALUE klass) {
            // nothing to do until rb_define_alloc_func is: a class defined from C allocates as any class does
        }

        #endregion

        #region Calling Ruby

        private static readonly ConcurrentDictionary<(string, bool), CallSite<Func<CallSite, object, Proc, object, object>>> _sites =
            new ConcurrentDictionary<(string, bool), CallSite<Func<CallSite, object, Proc, object, object>>>();

        /// <summary>
        /// Calls a method the way rb_funcall does: private methods too, unless publicOnly.
        /// </summary>
        internal static object Send(object target, string/*!*/ name, Proc block, params object[]/*!*/ args) {
            return SendWith(false, target, name, block, args);
        }

        internal static object SendWith(bool publicOnly, object target, string/*!*/ name, Proc block, object[]/*!*/ args) {
            var site = _sites.GetOrAdd((name, publicOnly), key => CallSite<Func<CallSite, object, Proc, object, object>>.Create(
                RubyCallAction.Make(_context, key.Item1, new RubyCallSignature(0,
                    (key.Item2 ? RubyCallFlags.None : RubyCallFlags.HasImplicitSelf) | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock))));
            return site.Target(site, target, block, new RubyArray(args));
        }

        private static Proc TakePassedBlock() {
            var block = _passedBlock;
            _passedBlock = null;
            return block;
        }

        internal static VALUE rb_funcallv(VALUE recv, VALUE mid, int argc, VALUE* argv) {
            return ToValue(Send(FromValue(recv), IdToString(mid), TakePassedBlock(), FromValues(argc, argv)));
        }

        internal static VALUE rb_funcallv_kw(VALUE recv, VALUE mid, int argc, VALUE* argv, int kw_splat) {
            return rb_funcallv(recv, mid, argc, argv);
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
            var frame = _frame;
            if (frame == null) {
                throw RubyExceptions.CreateRuntimeError("super called outside of method");
            }
            object unbound = Send(frame.Method.Owner, "instance_method", null, _context.EncodeIdentifier(frame.Method.Name));
            object super = Send(unbound, "super_method", null);
            if (super == null) {
                throw RubyExceptions.CreateNameError(String.Format("super: no superclass method '{0}'", frame.Method.Name));
            }
            var args = new List<object> { frame.Self };
            args.AddRange(FromValues(argc, argv));
            return ToValue(Send(super, "bind_call", frame.Block, args.ToArray()));
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
            return 0;
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
        private static Proc/*!*/ MakeBlock(IntPtr fn, VALUE data2) {
            return Proc.Create(_context, 0, new Func<BlockParam, object, object[], RubyArray, object>(
                (selfBlock, self, unused, args) => {
                    int argc = args.Count;
                    VALUE* argv = stackalloc VALUE[Math.Max(argc, 1)];
                    for (int i = 0; i < argc; i++) {
                        argv[i] = ToValue(args[i]);
                    }
                    VALUE yielded = argc > 0 ? argv[0] : Qnil;
                    try {
                        return FromValue(CallBlock(fn, yielded, data2, argc, argv, Qnil));
                    } catch (CIterBreak b) {
                        return selfBlock.Break(b.ReturnValue);
                    }
                }));
        }

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
            throw new CIterBreak(null);
        }

        internal static void rb_iter_break_value(VALUE val) {
            throw new CIterBreak(FromValue(val));
        }

        #endregion
    }
}
