# Differential matrix for the lexical lookups of a method that runs without a scope (a light frame,
# MethodDefinition.TransformLightFrame): class variables (`@@x', `@@x = v', `@@x ||= v', `defined?(@@x)'),
# `defined?' of a constant (`defined?(Foo)', `defined?(A::B)', `defined?(::A)', `defined?(expr::A)') and a
# constant qualified by an expression (`self.class::FOO', `obj::X'). Their helpers read the runtime and
# the lexical chain from the scope, and a method scope has no module of its own, so its chain starts at
# the declaring scope: with IR_LIGHT_LEXICAL on, the light frame hands them that scope. Run it under CRuby
# and under ./ir.sh with the shortcut on and off, and diff:
#
#   ruby Util/lexical-light-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/lexical-light-matrix.rb > /tmp/on.txt 2>&1
#   IR_LIGHT_LEXICAL=0 ./ir.sh Util/lexical-light-matrix.rb > /tmp/off.txt 2>&1
#   IR_LIGHT_FRAMES=0 ./ir.sh Util/lexical-light-matrix.rb > /tmp/noframes.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt && diff /tmp/mri.txt /tmp/noframes.txt
#
# Every probe runs N times and reports each distinct answer once, in the order they first appear, so the
# interpreted body, the compiled one and a site whose rule or cache is already bound all meet the case:
# where the method is written (a class body, `class << self', module_eval with a String and with a
# block, a refinement, the top level, a module function), a class variable an ancestor overtakes, and
# constants and class variables defined, changed and removed after the site is bound.
#
#   IR_LIGHT_FRAMES=why ./ir.sh Util/lexical-light-matrix.rb 2>&1 >/dev/null | grep -a '^\[light\]'
#
# says which of the methods below are light frames.

N = 3000

def t(label)
  print label.ljust(58), ' => '
  seen = []
  begin
    N.times do |i|
      v = yield i
      seen << v unless seen.include?(v)
    end
    puts seen.map(&:inspect).join(', ')
  rescue Exception => e
    puts "#{seen.map(&:inspect).join(', ')} then #{e.class}: #{e.message[0, 70]}"
  end
end

def err
  yield
rescue Exception => e
  "#{e.class}: #{e.message[0, 70]}"
end

# ---------------------------------------------------------------- 1. class variables in a class body

class CvBase
  @@x = 1
  @@counter = 0
  def read; @@x; end
  def write(v); @@x = v; end
  def or_assign; @@lazy ||= :made; end
  def op_assign; @@counter += 1; end
  def defined_x; defined?(@@x); end
  def defined_missing; defined?(@@nope); end
  def read_missing; @@nope; end
  def read_in_block; [1, 2].map { |i| @@x + i }; end
  def read_twice; [@@x, @@x]; end
  def self.class_read; @@x; end
  class << self
    def singleton_read; @@x; end
    def singleton_defined; defined?(@@x); end
  end
end

class CvSub < CvBase
  def sub_read; @@x; end
  def sub_write(v); @@x = v; end
end

o = CvBase.new
s = CvSub.new
t('read') { o.read }
t('write then read') { |i| o.write(i % 3); o.read }
o.write(1)
t('||=') { o.or_assign }
t('+= (3000 times)') { |i| o.op_assign; i == N - 1 ? CvBase.class_variable_get(:@@counter) : nil }
t('defined?(@@x)') { o.defined_x }
t('defined?(@@nope)') { o.defined_missing }
t('read missing') { err { o.read_missing } }
t('read in a block') { o.read_in_block }
t('read twice') { o.read_twice }
t('def self.m') { CvBase.class_read }
t('class << self') { CvBase.singleton_read }
t('class << self, defined?') { CvBase.singleton_defined }
t('subclass reads the base\'s') { s.sub_read }
t('subclass writes the base\'s') { |i| s.sub_write(i % 2 + 10); [o.read, CvSub.class_variables.sort] }
o.write(1)

# defined, changed and removed after the site is bound
class CvLate
  def late; @@late; end
  def late_defined; defined?(@@late); end
end
cl = CvLate.new
# (Not removed after it was read: CRuby 4.0.10 then reads a dangling inline cache and crashes.)
t('defined later, changed') do |i|
  CvLate.class_variable_set(:@@late, :here) if i == 1000
  CvLate.class_variable_set(:@@late, :again) if i == 2000
  [err { cl.late }, cl.late_defined]
end

# a class variable an ancestor takes over (MRI: "is overtaken by")
class CvOverBase; end
class CvOver < CvOverBase
  @@ov = :sub
  def ov; @@ov; end
  def ov_defined; defined?(@@ov); end
end
co = CvOver.new
t('overtaken by an ancestor later') do |i|
  CvOverBase.class_variable_set(:@@ov, :base) if i == 1500
  err { co.ov }
end
# Not probed, because the unchanged build differs from CRuby 4.0 there already: `defined?(@@ov)' once an
# ancestor has overtaken @@ov (CRuby: "class variable"; IronRuby raises the RuntimeError), and `@@r' in a
# method of a `refine' block (CRuby: the module around the refine; IronRuby: the refinement).

# a module's class variable through an including class
module CvMod
  @@m = :mod
  def m_read; @@m; end
  def m_write(v); @@m = v; end
  module_function def m_function; @@m; end
end
class CvIncl; include CvMod; end
t('module method via an includer') { CvIncl.new.m_read }
t('module function') { [CvMod.m_function, CvMod.send(:m_function)] }
t('module method via bind_call') { CvMod.instance_method(:m_read).bind_call(Object.new) }
t('module method writes') { |i| CvIncl.new.m_write(i % 2); CvMod.class_variable_get(:@@m) }

# ---------------------------------------------------------------- 2. where the method is written

class CvEval; @@e = :eval_class; end
CvEval.module_eval "def eval_string; @@e; end"
CvEval.module_eval { def eval_block; @@e; end }
CvEval.class_eval "class << self; def eval_singleton; @@e; end; end"
CvEval.send(:define_method, :dm) { @@e } rescue nil
ce = CvEval.new
t('module_eval with a String') { err { ce.eval_string } }
t('module_eval with a block') { err { ce.eval_block } }
t('class << self in a String') { err { CvEval.eval_singleton } }
t('define_method with a block') { err { ce.dm } }

def toplevel_cv; @@top; end
def toplevel_cv_defined; defined?(@@top); end
t('toplevel def') { err { toplevel_cv } }
t('toplevel def, defined?') { err { toplevel_cv_defined } }

obj = Object.new
obj.instance_eval "def ie_cv; defined?(@@x); end"
t('instance_eval String def') { err { obj.ie_cv } }


class CvDeep
  @@d = :outer
  class Inner
    def inner_read; @@d; end
  end
  module Mixin
    def mixin_read; defined?(@@d); end
  end
end
t('nested class does not see the outer\'s') { err { CvDeep::Inner.new.inner_read } }
t('nested module, defined?') { err { Object.new.extend(CvDeep::Mixin).mixin_read } }

# ---------------------------------------------------------------- 3. defined? of a constant

class DcBase; INHERITED = 1; end

module DcOuter
  LEX = 1
  PRIV = 2
  private_constant :PRIV
  module Deep; D = 1; end
  class Probe < DcBase
    def lexical; defined?(LEX); end
    def private_lexical; defined?(PRIV); end
    def private_qualified; defined?(DcOuter::PRIV); end
    def missing; defined?(NoSuchThing); end
    def global; defined?(::String); end
    def global_missing; defined?(::NoSuchThing); end
    def qualified; defined?(DcOuter::Deep::D); end
    def qualified_missing; defined?(DcOuter::Nope::D); end
    def inherited_c; defined?(INHERITED); end
    def late; defined?(DcLate); end
    def late_qualified; defined?(DcOuter::Late2); end
    def class_expr; defined?(self.class::OWN); end
    def obj_expr(x); defined?(x::D); end
    def expr_chain(x); defined?(x::Deep::D); end
    def raising_expr; defined?(boom::D); end
    def boom; raise "boom"; end
    def in_condition; defined?(LEX) ? :yes : :no; end
    def unless_guard; return :none unless defined?(DcLate); :some; end
    class << self
      def s_lexical; defined?(LEX); end
    end
  end
end
class DcSub < DcOuter::Probe; OWN = 3; end

pr = DcOuter::Probe.new
t('defined?(LEX)') { pr.lexical }
t('defined?(PRIV) lexically') { pr.private_lexical }
t('defined?(Mod::PRIV)') { pr.private_qualified }
t('defined?(NoSuchThing)') { pr.missing }
t('defined?(::String)') { pr.global }
t('defined?(::NoSuchThing)') { pr.global_missing }
t('defined?(A::B::C)') { pr.qualified }
t('defined?(A::Nope::C)') { pr.qualified_missing }
t('defined?(inherited)') { pr.inherited_c }
t('defined?(self.class::OWN)') { |i| i.even? ? pr.class_expr : DcSub.new.class_expr }
t('defined?(x::D)') { |i| pr.obj_expr([DcOuter::Deep, DcOuter, 1, nil][i % 4]) }
t('defined?(x::Deep::D)') { |i| pr.expr_chain([DcOuter, DcOuter::Deep, 1][i % 3]) }
t('defined?(raising::D)') { err { pr.raising_expr } }
t('defined? as a condition') { pr.in_condition }
t('class << self, defined?(LEX)') { DcOuter::Probe.s_lexical }
t('defined later, removed') do |i|
  Object.const_set(:DcLate, 1) if i == 1000
  Object.send(:remove_const, :DcLate) if i == 2000
  DcOuter.const_set(:Late2, 1) if i == 1200
  DcOuter.send(:remove_const, :Late2) if i == 2200
  [pr.late, pr.late_qualified, pr.unless_guard]
end
class DcMissing
  def self.const_missing(n); :"missing_#{n}"; end
  def probe; defined?(Whatever); end
end
t('const_missing is not called') { DcMissing.new.probe }

DcOuter.module_eval "def self.dc_string; defined?(LEX); end"
DcOuter.module_eval { def self.dc_block; defined?(LEX); end }
t('module_eval String, defined?(LEX)') { DcOuter.dc_string }
t('module_eval block, defined?(LEX)') { DcOuter.dc_block }
def toplevel_defined_c; defined?(LEX); end
t('toplevel def, defined?(LEX)') { toplevel_defined_c }

# ---------------------------------------------------------------- 4. {expr}::C

class EqBase
  FOO = :base
  def own_foo; self.class::FOO; end
  def via(x); x::FOO; end
  def chain(x); x::Inner::V; end
  def missing(x); x::NOPE; end
end
class EqSub < EqBase; FOO = :sub; end
class EqOther < EqBase; end
module EqHolder
  FOO = :holder
  module Inner; V = :v; end
  SECRET = 1
  private_constant :SECRET
  def self.const_missing(n); :"cm_#{n}"; end
end
class EqProbe
  def secret(x); x::SECRET; end
end

t('self.class::FOO') { |i| [EqBase, EqSub, EqOther][i % 3].new.own_foo }
t('x::FOO, many modules') { |i| EqBase.new.via([EqBase, EqSub, EqHolder, EqOther][i % 4]) }
t('x::FOO, not a module') { |i| err { EqBase.new.via([1, nil, "s"][i % 3]) } }
t('x::Inner::V') { |i| err { EqBase.new.chain([EqHolder, EqBase][i % 2]) } }
t('x::NOPE, const_missing') { |i| err { EqBase.new.missing([EqHolder, EqBase][i % 2]) } }
t('x::SECRET, private') { err { EqProbe.new.secret(EqHolder) } }
t('changed after bound') do |i|
  if i == 1000
    EqSub.send(:remove_const, :FOO)
  elsif i == 2000
    EqSub.const_set(:FOO, :again)
  end
  EqSub.new.own_foo
end

# ---------------------------------------------------------------- 5. what the frame still does afterwards

class After
  @@a = 1
  A = 2
  def then_binding; x = @@a; [x, binding.local_variable_get(:x)]; end
  def then_locals; x = self.class::A; [x, local_variables]; end
  def then_raise; raise ArgumentError, "a=#{@@a} #{defined?(A)}"; end
  def then_caller; [@@a, caller(0, 1)[0][/in .*/]]; end
  def then_method; [defined?(A), __method__]; end
  def recursive(n); n == 0 ? [@@a, self.class::A] : recursive(n - 1); end
end
af = After.new
t('then binding') { af.then_binding }
t('then local_variables') { af.then_locals }
t('then raise') { err { af.then_raise } }
t('then caller') { af.then_caller }
t('then __method__') { af.then_method }
t('recursion') { af.recursive(5) }

calls = []
tp = TracePoint.new(:call) { |tp| calls << tp.method_id if tp.defined_class == CvBase }
t('TracePoint :call') do |i|
  tp.enable if i == 1000
  r = [o.read, o.defined_x]
  tp.disable if i == 2000
  r
end
puts "traced #{calls.size} calls of #{calls.uniq.sort.inspect}"

# ---------------------------------------------------------------- 6. a refinement (last: every method defined after
# `using' keeps its scope, so nothing above may follow it)

module CvRefine
  refine String do
    def cv_refined_defined; defined?(@@nowhere); end
  end
end
using CvRefine
t('refinement body, defined? of a missing one') { "s".cv_refined_defined }
