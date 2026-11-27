# Bytes allocated per call of argument-forwarding wrappers: a rest parameter splatted on (`*args`, `*args, &b`),
# `...`, into a callee with and without a rest parameter of its own (Src/Ruby/Compiler/Ast/Definitions/ForwardedRest.cs,
# RubyCallFlags.HasFreshSplat; IR_FORWARD_REST=0 turns the forwarding of a dead rest off). IronRuby only.
#   IR_CONFIG=Release ./ir.sh Util/bench/forward-bytes.rb
class W
  def raw(a = 0) = a
  def rest_t(*r) = r.size
  def w_pass(a) = raw(a)
  def w_splat(*args) = raw(*args)
  def w_splat_rest(*args) = rest_t(*args)
  def w_sb(*args, &b) = raw(*args, &b)
  def w_sbr(*args, &b) = rest_t(*args, &b)
  def w_fwd(...) = raw(...)
  def w_fwd_rest(...) = rest_t(...)
end
o = W.new
N = 200_000
def bytes = System::GC.GetAllocatedBytesForCurrentThread
def m(name, n)
  yield; yield
  b = bytes; i = 0
  while i < n; yield; i += 1; end
  printf "%-40s %6.1f B per call\n", name, (bytes - b).to_f / n
end
m("rest_t(1)", N)                        { o.rest_t(1) }
m("w_pass(1) -> raw(a)", N)              { o.w_pass(1) }
m("w_splat(1) -> raw(*args)", N)         { o.w_splat(1) }
m("w_splat_rest(1) -> rest_t(*args)", N) { o.w_splat_rest(1) }
m("w_sb(1) -> raw(*args, &b)", N)        { o.w_sb(1) }
m("w_sbr(1) -> rest_t(*args, &b)", N)    { o.w_sbr(1) }
m("w_sb(1) { } -> raw(*args, &b)", N)    { o.w_sb(1) { } }
m("w_fwd(1) -> raw(...)", N)             { o.w_fwd(1) }
m("w_fwd_rest(1) -> rest_t(...)", N)     { o.w_fwd_rest(1) }
