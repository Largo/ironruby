# Differential matrix for the identity of the literals an eval'd text makes - a regexp literal, `/#{x}/o`, an
# interpolated regexp, an Integer range literal - when the text's compiled code is shared by every eval of it
# (EvalCodeCache; IR_EVAL_LITERALS lets texts with such literals in, the literals' caches kept per eval). CRuby makes
# a new object per eval of the same text, and the same one for every pass and every call of a block within one eval;
# a Proc that escapes keeps its eval's object. Run it under CRuby and under ./ir.sh, and diff:
#
#   ruby Util/eval-literals-matrix.rb > /tmp/mri.txt 2>&1
#   ./ir.sh Util/eval-literals-matrix.rb > /tmp/on.txt 2>&1
#   IR_EVAL_LITERALS=0 ./ir.sh Util/eval-literals-matrix.rb > /tmp/off.txt 2>&1
#   diff /tmp/mri.txt /tmp/on.txt && diff /tmp/mri.txt /tmp/off.txt

N = 1000

def t(label)
  print label.ljust(56), ' => '
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

B = binding
def distinct(list) = list.map(&:object_id).uniq.size

# ---------------------------------------------------------------- 1. across evals and within one

t('top level: 3 evals') { distinct(3.times.map { eval("/a\\w/", B) }) }
t('top level: a while loop in one eval') { distinct(eval("r = []; i = 0; while i < 4; r << /a\\w/; i += 1; end; r", B)) }
t('a block, 4 calls in one eval') { distinct(eval("4.times.map { /a\\w/ }", B)) }
t('nested blocks') { distinct(eval("2.times.flat_map { [1, 2].map { /n\\w/ } }", B)) }
t('two sites with the same text') { r = eval("[/s/, /s/]", B); r[0].equal?(r[1]) }
t('a block, across evals') { distinct(3.times.flat_map { eval("2.times.map { /a\\w/ }", B) }) }
t('the top level and a block of one eval') { eval("x = /t/; [x, 2.times.map { /t/ }]", B).flatten.then { |a| distinct(a) } }
t('a lambda and a proc of one eval') { eval("l = -> { /l/ }; pr = proc { /l/ }; [l.(), l.(), pr.(), pr.()]", B).then { |a| [a[0].equal?(a[1]), a[2].equal?(a[3]), a[0].equal?(a[2])] } }
t('frozen, source, options') { r = eval("/a\\w/mi", B); [r.frozen?, r.source, r.options] }

# ---------------------------------------------------------------- 2. Procs that escape

t('an escaped Proc after another eval') do
  pr = eval("proc { /esc/ }", B)
  first = pr.call
  other = eval("proc { /esc/ }", B).call
  [pr.call.equal?(first), first.equal?(other)]
end
t('two Procs of one eval, kept') { ps = eval("[proc { /k/ }, proc { /k/ }]", B); [ps[0].().equal?(ps[0].()), ps[0].().equal?(ps[1].())] }
t('a Proc made into a method') do
  o = Object.new
  o.define_singleton_method(:m, &eval("proc { /dm/ }", B))
  [o.m.equal?(o.m), o.m.source]
end
t('copies of the Proc: dup, curry') { pr = eval("proc { /lc/ }", B); [pr.dup.().equal?(pr.()), pr.curry.().equal?(pr.())] }
t('Method#to_proc and a block passed on') { eval("def_pr = proc { /bp/ }; [1, 2].map(&def_pr)", B).then { |a| a[0].equal?(a[1]) } }

# ---------------------------------------------------------------- 3. /o, interpolation, ranges, $~

v = "z"
t('/#{v}/o: 3 evals, and a loop of one') { [distinct(3.times.map { eval('/#{v}/o', binding) }), distinct(eval('3.times.map { /#{v}/o }', binding))] }
t('/#{v}/: a loop of one eval') { distinct(eval('3.times.map { /#{v}/ }', binding)) }
t('/o keeps the first value within an eval') { w = "a"; eval('2.times.map { |i| w = "b" if i == 1; /#{w}/o.source }', binding) }
t('an Integer range literal: frozen, per eval, a loop') { [eval("(1..2)", B).frozen?, distinct(3.times.map { eval("(1..2)", B) }), distinct(eval("3.times.map { (1...4) }", B))] }
t('a range of variables') { a = 1; distinct(eval("2.times.map { (a..3) }", binding)) }
t('$~ of a match in the text, in the binding\'s frame') { $~ = nil; eval('"xab" =~ /a(b)/', binding); [$~ && $~[1], $~ && $~.pre_match] }
t('$~ in a block of the text') { eval('["q1", "q2"].map { |s| s =~ /q(\\d)/; $1 }', B) }
t('a regexp as a condition (the last line)') { $_ = "hay"; quiet = $VERBOSE; $VERBOSE = nil; r = eval('/a/ ? 1 : 0', binding); $VERBOSE = quiet; r }

# ---------------------------------------------------------------- 4. threads and templates

t('four threads, one text') do |i|
  next nil unless i % 200 == 0
  rs = 4.times.map { Thread.new { 50.times.map { eval("2.times.map { /th/ }", B) } } }.map(&:value).flatten(1)
  [rs.all? { |pair| pair[0].equal?(pair[1]) }, distinct(rs.map(&:first)) == rs.size]
end
require 'erb'
T = ERB.new("<%= items.map { |s| s.sub(/^x/, 'y') }.join(',') %>|<%= (1..2).to_a.size %>")
t('ERB with a regexp and a range') { |i| T.result_with_hash(items: ["x#{i % 3}", "zx"]) }
