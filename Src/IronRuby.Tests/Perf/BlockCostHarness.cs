/* ****************************************************************************
 *
 * Stage 0 of Docs/block-calling-convention.md: where the time of a call with a literal block goes.
 *
 *   dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -c Release -p:IronRubyTargetFrameworks=net10.0
 *   Src/IronRuby.Tests/bin/Release/net10.0/IronRuby.Tests /blockcost [N]
 *
 * Not a test: the driver runs it only for /blockcost (Driver.Main), never in the suite. It hosts a runtime, takes
 * real objects from Ruby code (a literal's block dispatcher, compiled; a method scope; the declaring scope and
 * module), and times the runtime calls the compiled code of `def nyl(a) = ny { 1 }' and `def e1(a); a.each { |x| };
 * 1; end' makes per call - each step alone in a loop, then the steps together - with a Stopwatch over N iterations
 * after a warm-up, best of five. Then the same calls end to end, through Ruby, for the sum to be compared with.
 *
 * What it cannot time: the call sites and the light frames' prologues and epilogues themselves (compiled Ruby code,
 * not runtime calls): those are what end to end minus the steps leaves, against the same end to end of a call
 * without a block (nb0).
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;

namespace IronRuby.Tests.Perf {
    internal static class BlockCostHarness {
        internal static long ProtoNew;
        private const string Source = @"
PE = proc { |x| }
PY = proc { 1 }
class BcO
  def nb; 1; end
  def ny; yield; end
  def nb0(a) = nb
  def nyl(a) = ny { 1 }
  def e1(a); a.each { |x| }; 1; end
  def e0(a); 1; end
  def ep(a); a.each(&PE); 1; end
  def ept(a); return a.each { |y| } if a.nil?; a.each(&PE); 1; end
  def nyp(a) = ny(&PY)
  def grab(&b) = b
  def lit1 = grab { 1 }
  def litx = grab { |x| }
  def sc(a) = binding
end
def bc_run_nb0(o, a, n); i = 0; while i < n; o.nb0(a); i += 1; end; end
def bc_run_nyl(o, a, n); i = 0; while i < n; o.nyl(a); i += 1; end; end
def bc_run_e1(o, a, n); i = 0; while i < n; o.e1(a); i += 1; end; end
def bc_run_e0(o, a, n); i = 0; while i < n; o.e0(a); i += 1; end; end
def bc_run_ep(o, a, n); i = 0; while i < n; o.ep(a); i += 1; end; end
def bc_run_ept(o, a, n); i = 0; while i < n; o.ept(a); i += 1; end; end
def bc_run_nyp(o, a, n); i = 0; while i < n; o.nyp(a); i += 1; end; end
";

        internal static void Run(string[] args) {
            int n = (args.Length > 1) ? Int32.Parse(args[1]) : 5_000_000;
            var engine = IronRuby.Ruby.CreateEngine();
            engine.Execute(Source);

            object o = (object)engine.Execute("BcO.new");      // typed: a dynamic would send every call below through the binder
            var lit1 = (Proc)engine.Execute("BcO.new.lit1");      // `{ 1 }': its dispatcher is the literal's code
            var litx = (Proc)engine.Execute("BcO.new.litx");      // `{ |x| }'
            var binding = (Binding)engine.Execute("BcO.new.sc(1)");
            RubyScope bscope = binding.LocalScope;
            while (!(bscope is RubyMethodScope)) { bscope = bscope._parent; }   // a Binding's scope is a copy over the method's
            var methodScope = (RubyMethodScope)bscope;
            RubyScope declaringScope = methodScope._parent;
            RubyModule declaringModule = methodScope.DeclaringModule;
            var shape = new MethodFrameShape(new[] { "a" }, 1, "nyl");
            var array = new RubyArray { 1 };

            Console.WriteLine("block-cost harness: N = {0:N0}, best of 5 after a warm-up, ns per operation", n);
            Console.WriteLine();

            // ---- the steps, one at a time
            Report("1a locals tuple (MakeLocalsStorage)", n, k => {
                object last = null;
                for (int i = 0; i < k; i++) { last = new MutableTuple<object>(); }
                return last;
            });

            Report("1b scope made (CreateMethodScopeLazy)", n, k => {
                object last = null;
                var rfc = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    last = RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, new MutableTuple<object>(), o, null, null, rfc, null);
                }
                return last;
            });

            Report("2a Proc made (new Proc)", n, k => {
                object last = null;
                for (int i = 0; i < k; i++) { last = new Proc(ProcKind.Block, o, methodScope, lit1.Dispatcher); }
                return last;
            });

            // the parked cycle of a light frame with one literal: take, reuse the scope, make the Proc again, stage, park
            var slot = new ParkedScopeSlot("nyl") { ProcCount = 1 };
            Report("1+2+9 parked cycle (Take/Reuse/Instantiate/Begin/Stage/EndPark)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    var parked = RubyOps.TakeParkedScope(slot, declaringScope, declaringModule);
                    RubyMethodScope scope = (parked != null)
                        ? RubyOps.ReuseParkedScopeLazy(parked, locals, o, null, null, rfc0, null)
                        : RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, locals, o, null, null, rfc0, null);
                    var proc = RubyOps.InstantiateParkedBlock(null, scope, o, lit1.Dispatcher, parked, 0);
                    // what the light callee's entry and exit leave: a reusable, inactive Proc (EnterLightMethod, LeaveLightMethodFrame)
                    var rfc = RubyOps.EnterLightMethod(proc);
                    RubyOps.LeaveLightMethodFrame(rfc, null);
                    RubyOps.LeaveLightMethodFrame(rfc0, scope);
                    if (parked != null) { Taken++; }
                    var parking = RubyOps.BeginPark(slot, scope, parked);
                    if (parking != null) {
                        RubyOps.EndPark(slot, parking, scope, RubyOps.StageParkedProc(parking, 0, proc));
                    }
                    last = proc;
                }
                return last;
            });

            // the parked cycle in parts: the take and the park alone (no reuse, no Proc), then each part added
            var slotP = new ParkedScopeSlot("p") { ProcCount = 1 };
            {
                var rfc0 = RubyOps.EnterLightMethod(null);
                var s0 = RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, new MutableTuple<object>(), o, null, null, rfc0, null);
                RubyOps.LeaveLightMethodFrame(rfc0, s0);
                var p0 = RubyOps.BeginPark(slotP, s0, null);
                RubyOps.EndPark(slotP, p0, s0, RubyOps.StageParkedProc(p0, 0, null));
            }
            Report("  P1 take + park, nothing between (TakeParkedScope, BeginPark, EndPark)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var parked = RubyOps.TakeParkedScope(slotP, declaringScope, declaringModule);
                    var scope = parked.Scope;
                    var parking = RubyOps.BeginPark(slotP, scope, parked);
                    RubyOps.EndPark(slotP, parking, scope, RubyOps.StageParkedProc(parking, 0, null));
                    last = parking;
                }
                return last;
            });
            Report("  P2 = P1 + the scope reinitialized (ReuseParkedScopeLazy)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                var locals = new MutableTuple<object>();
                for (int i = 0; i < k; i++) {
                    var parked = RubyOps.TakeParkedScope(slotP, declaringScope, declaringModule);
                    var scope = RubyOps.ReuseParkedScopeLazy(parked, locals, o, null, null, rfc0, null);
                    RubyOps.LeaveLightMethodFrame(rfc0, scope);
                    var parking = RubyOps.BeginPark(slotP, scope, parked);
                    RubyOps.EndPark(slotP, parking, scope, RubyOps.StageParkedProc(parking, 0, null));
                    last = parking;
                }
                return last;
            });
            Report("  P3 = P2 + the Proc made again and staged (InstantiateParkedBlock, StageParkedProc)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                var locals = new MutableTuple<object>();
                for (int i = 0; i < k; i++) {
                    var parked = RubyOps.TakeParkedScope(slotP, declaringScope, declaringModule);
                    var scope = RubyOps.ReuseParkedScopeLazy(parked, locals, o, null, null, rfc0, null);
                    var proc = RubyOps.InstantiateParkedBlock(null, scope, o, lit1.Dispatcher, parked, 0);
                    RubyOps.LeaveLightMethodFrame(RubyOps.EnterLightMethod(proc), null);   // the callee's conversion (1 ns)
                    RubyOps.LeaveLightMethodFrame(rfc0, scope);
                    var parking = RubyOps.BeginPark(slotP, scope, parked);
                    RubyOps.EndPark(slotP, parking, scope, RubyOps.StageParkedProc(parking, 0, proc));
                    last = parking;
                }
                return last;
            });
            var scopeR = RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, new MutableTuple<object>(), o, null, null,
                RubyOps.EnterLightMethod(null), null);
            Report("  R  the scope's fields cleared and set again alone (ClearCallState, Reinitialize)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                var locals = new MutableTuple<object>();
                for (int i = 0; i < k; i++) {
                    scopeR.ClearCallState();
                    scopeR.Reinitialize(locals, o, null, null, rfc0, null);
                    last = scopeR;
                }
                return last;
            });

            // what one atomic costs here, against a plain release write: the park's take and its publication are one each
            var cell = new object[1];
            var token = new object();
            Report("  A1 one Interlocked.CompareExchange (take, or the park's publication)", n, k => {
                for (int i = 0; i < k; i++) {
                    System.Threading.Interlocked.CompareExchange(ref cell[0], token, null);
                    cell[0] = null;
                }
                return cell;
            });
            Report("  A2 one Volatile.Write instead", n, k => {
                for (int i = 0; i < k; i++) {
                    System.Threading.Volatile.Write(ref cell[0], token);
                    cell[0] = null;
                }
                return cell;
            });

            var procB = new Proc(ProcKind.Block, o, methodScope, lit1.Dispatcher);
            Report("4+8 callee entry and exit (EnterLightMethod, LeaveLightMethodFrame)", n, k => {
                object last = null;
                for (int i = 0; i < k; i++) {
                    procB.TryReuse();
                    var rfc = RubyOps.EnterLightMethod(procB);
                    RubyOps.LeaveLightMethodFrame(rfc, null);
                    last = rfc;
                }
                return last;
            });

            var procY = new Proc(ProcKind.Block, o, methodScope, lit1.Dispatcher);
            var rfcY = RubyOps.EnterLightMethod(procY);
            Report("5+6+7 yield to { 1 } (ReuseBfcForYield, Yield0, block, LightMethodYield)", n, k => {
                object last = null;
                BlockParam bfc = null;
                for (int i = 0; i < k; i++) {
                    bfc = RubyOps.ReuseBfcForYield(bfc, procY);
                    last = RubyOps.Yield0(null, procY.Self, bfc);
                    RubyOps.LightMethodYield(rfcY, null, bfc, last);
                }
                return last;
            });

            var procE = new Proc(ProcKind.Block, o, methodScope, litx.Dispatcher);
            Report("each: CreateBfcForBlockNotKept + one Yield to { |x| } + LeaveProcConverter", n, k => {
                object last = null;
                for (int i = 0; i < k; i++) {
                    procE.TryReuse();
                    var bfc = RubyOps.CreateBfcForBlockNotKept(procE);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);     // the [BlockNotKept] rule's finally
                    last = r;
                }
                return last;
            });

            // ---- the steps together, as one call of nyl and of e1 makes them (parked)
            var slotN = new ParkedScopeSlot("nyl") { ProcCount = 1 };
            Report("nyl: all steps (parked)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    var parked = RubyOps.TakeParkedScope(slotN, declaringScope, declaringModule);
                    RubyMethodScope scope = (parked != null)
                        ? RubyOps.ReuseParkedScopeLazy(parked, locals, o, null, null, rfc0, null)
                        : RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, locals, o, null, null, rfc0, null);
                    var proc = RubyOps.InstantiateParkedBlock(null, scope, o, lit1.Dispatcher, parked, 0);
                    var rfc = RubyOps.EnterLightMethod(proc);
                    var bfc = RubyOps.ReuseBfcForYield(null, proc);
                    last = RubyOps.Yield0(null, proc.Self, bfc);
                    RubyOps.LightMethodYield(rfc, null, bfc, last);
                    RubyOps.LeaveLightMethodFrame(rfc, null);
                    RubyOps.LeaveLightMethodFrame(rfc0, scope);
                    if (parked != null) { Taken++; }
                    var parking = RubyOps.BeginPark(slotN, scope, parked);
                    if (parking != null) {
                        RubyOps.EndPark(slotN, parking, scope, RubyOps.StageParkedProc(parking, 0, proc));
                    }
                }
                return last;
            });

            var slotE = new ParkedScopeSlot("e1") { ProcCount = 1 };
            Report("e1: all steps (parked)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    var parked = RubyOps.TakeParkedScope(slotE, declaringScope, declaringModule);
                    RubyMethodScope scope = (parked != null)
                        ? RubyOps.ReuseParkedScopeLazy(parked, locals, o, null, null, rfc0, null)
                        : RubyOps.CreateMethodScopeLazy(shape, declaringScope, declaringModule, locals, o, null, null, rfc0, null);
                    var proc = RubyOps.InstantiateParkedBlock(null, scope, o, litx.Dispatcher, parked, 0);
                    var bfc = RubyOps.CreateBfcForBlockNotKept(proc);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);     // the [BlockNotKept] rule's finally: the literal inactive again
                    RubyOps.LeaveLightMethodFrame(rfc0, scope);
                    if (parked != null) { Taken++; }
                    var parking = RubyOps.BeginPark(slotE, scope, parked);
                    if (parking != null) {
                        RubyOps.EndPark(slotE, parking, scope, RubyOps.StageParkedProc(parking, 0, proc));
                    }
                    last = r;
                }
                return last;
            });

            // ---- stage A, simulated: the literal as a descriptor - a Proc whose scope is made only on demand, taken from
            // the slot by the thread that owns it with plain reads and writes, its frame header filled, given back at exit
            var hdr = new ProtoHeader();
            var protoSlot = new ProtoSlot { Owner = Environment.CurrentManagedThreadId, Item = procE };
            Report("e1: all steps (descriptor, simulated)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    Proc proc = null;
                    if (protoSlot.Owner == Environment.CurrentManagedThreadId) { proc = protoSlot.Item; protoSlot.Item = null; }
                    if (proc == null || !proc.TryReuse()) { ProtoNew++; proc = new Proc(ProcKind.Block, o, methodScope, litx.Dispatcher); }
                    proc.ReuseParked(o, false);
                    hdr.Shape = shape; hdr.DeclaringScope = declaringScope; hdr.DeclaringModule = declaringModule; hdr.Locals = locals;
                    hdr.Self = o; hdr.Block = null; hdr.Rfc = rfc0; hdr.Callee = null;
                    var bfc = RubyOps.CreateBfcForBlockNotKept(proc);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);
                    RubyOps.LeaveLightMethodFrame(rfc0, null);
                    if (proc.IsReusableAndInactive) {
                        hdr.DeclaringScope = null; hdr.DeclaringModule = null; hdr.Locals = null; hdr.Self = null; hdr.Rfc = null;
                        proc.ClearForPark();
                        if (protoSlot.Owner == Environment.CurrentManagedThreadId) { protoSlot.Item = proc; }
                    }
                    last = r;
                }
                return last;
            });

            Report("e1: all steps (descriptor, no header)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    Proc proc = null;
                    if (protoSlot.Owner == Environment.CurrentManagedThreadId) { proc = protoSlot.Item; protoSlot.Item = null; }
                    if (proc == null || !proc.TryReuse()) { ProtoNew++; proc = new Proc(ProcKind.Block, o, methodScope, litx.Dispatcher); }
                    proc.ReuseParked(o, false);
                    
                    
                    var bfc = RubyOps.CreateBfcForBlockNotKept(proc);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);
                    RubyOps.LeaveLightMethodFrame(rfc0, null);
                    if (proc.IsReusableAndInactive) {
                        
                        proc.ClearForPark();
                        if (protoSlot.Owner == Environment.CurrentManagedThreadId) { protoSlot.Item = proc; }
                    }
                    last = r;
                }
                return last;
            });

            Report("e1: all steps (descriptor, header: changed only)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    var locals = new MutableTuple<object>();
                    Proc proc = null;
                    if (protoSlot.Owner == Environment.CurrentManagedThreadId) { proc = protoSlot.Item; protoSlot.Item = null; }
                    if (proc == null || !proc.TryReuse()) { ProtoNew++; proc = new Proc(ProcKind.Block, o, methodScope, litx.Dispatcher); }
                    proc.ReuseParked(o, false);
                    if (hdr.Shape != shape) hdr.Shape = shape; if (hdr.DeclaringScope != declaringScope) hdr.DeclaringScope = declaringScope; if (hdr.DeclaringModule != declaringModule) hdr.DeclaringModule = declaringModule; hdr.Locals = locals;
                    hdr.Rfc = rfc0;
                    var bfc = RubyOps.CreateBfcForBlockNotKept(proc);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);
                    RubyOps.LeaveLightMethodFrame(rfc0, null);
                    if (proc.IsReusableAndInactive) {
                        hdr.Locals = null; hdr.Rfc = null;
                        proc.ClearForPark();
                        if (protoSlot.Owner == Environment.CurrentManagedThreadId) { protoSlot.Item = proc; }
                    }
                    last = r;
                }
                return last;
            });

            Report("e1: all steps (descriptor, no header, no tuple)", n, k => {
                object last = null;
                var rfc0 = RubyOps.EnterLightMethod(null);
                for (int i = 0; i < k; i++) {
                    
                    Proc proc = null;
                    if (protoSlot.Owner == Environment.CurrentManagedThreadId) { proc = protoSlot.Item; protoSlot.Item = null; }
                    if (proc == null || !proc.TryReuse()) { ProtoNew++; proc = new Proc(ProcKind.Block, o, methodScope, litx.Dispatcher); }
                    proc.ReuseParked(o, false);
                    
                    
                    var bfc = RubyOps.CreateBfcForBlockNotKept(proc);
                    object r;
                    bfc.Yield(array[0], out r);
                    RubyOps.LeaveProcConverter(bfc);
                    RubyOps.LeaveLightMethodFrame(rfc0, null);
                    if (proc.IsReusableAndInactive) {
                        
                        proc.ClearForPark();
                        if (protoSlot.Owner == Environment.CurrentManagedThreadId) { protoSlot.Item = proc; }
                    }
                    last = r;
                }
                return last;
            });

            Console.WriteLine("  (descriptor: {0} Procs made)", ProtoNew);
            // ---- end to end, through Ruby
            Console.WriteLine();
            foreach (var name in new[] { "nb0", "nyp", "nyl", "e0", "ep", "ept", "e1" }) {
                ReportRuby(engine, name, n);
            }
        }

        // calls of the measured rounds that took a parked scope (a cycle that never parks times the allocating path)
        private static long Taken;

        private static void Report(string label, int n, Func<int, object> body) {
            body(n / 10);                       // warm-up: tiering, the block's compilation
            body(n / 10);
            double best = Double.MaxValue;
            Taken = 0;
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 5; round++) {
                var sw = Stopwatch.StartNew();
                GC.KeepAlive(body(n));
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / n);
            }
            Console.WriteLine("  {0,-80} {1,7:F1} ns {3,4} B{2}", label, best, Taken > 0 ? String.Format("  (parked: {0:P0})", Taken / (5.0 * n)) : "",
                (GC.GetAllocatedBytesForCurrentThread() - bytes0) / (5 * n));
        }

        private static void ReportRuby(ScriptEngine engine, string name, int n) {
            engine.Execute(String.Format("bc_run_{0}(BcO.new, [1], {1})", name, n / 10));
            engine.Execute(String.Format("bc_run_{0}(BcO.new, [1], {1})", name, n / 10));
            double best = Double.MaxValue;
            string code = String.Format("bc_run_{0}(BcO.new, [1], {1})", name, n);
            for (int round = 0; round < 5; round++) {
                var sw = Stopwatch.StartNew();
                engine.Execute(code);
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / n);
            }
            Console.WriteLine("  {0,-80} {1,7:F1} ns", "end to end: " + name + " (the caller's while loop included)", best);
        }
    }
    // perf/stagea's simulation of a block descriptor (Docs/block-calling-convention.md, "Stage A, simulated"): what the
    // scope would be made from on demand, and the slot its Proc is taken from by the thread that owns it.
    internal sealed class ProtoHeader {
        internal object Shape, DeclaringScope, DeclaringModule, Locals, Self, Block, Rfc, Callee;
    }
    internal sealed class ProtoSlot {
        internal int Owner;
        internal Proc Item;
    }
}
