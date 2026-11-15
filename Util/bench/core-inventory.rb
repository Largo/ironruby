# Where each core class's methods live: C# ([RubyMethod] in Src/Libraries) or Ruby (a def
# in the prelude, Src/StdLib/ironruby/ruby4.rb), and which prelude definitions replace a C#
# one (the prelude loads later, so its definition wins).
#
#   ruby Util/bench/core-inventory.rb [Class ...]      # CRuby 4.0: it parses the prelude with prism
#
# A prelude `def m ... end unless method_defined?(:m)' counts as Ruby only where C# has no m.
# Static on purpose: at run time a replaced C# method is no longer there to count. The C# side
# is read with regular expressions (a [RubyMethod] belongs to the nearest [RubyClass] or
# [RubyModule] above it, plus what `Includes(..., Copy = true)` copies in), so it is a count
# for a table, not a model of the generator.
require 'prism'
require 'set'

ROOT = File.expand_path('../..', __dir__)
PRELUDE = File.join(ROOT, 'Src/StdLib/ironruby/ruby4.rb')

# --- C# -------------------------------------------------------------------------------
# name of the Ruby class or, for an extension of a CLR type, "<typeof name>"
csharp = Hash.new { |h, k| h[k] = { inst: Set.new, sing: Set.new, copies: [] } }
type_to_ruby = {}  # C# ops class or extended CLR type -> key in csharp

Dir[File.join(ROOT, 'Src/Libraries/{Builtins,Extensions}/*.cs')].sort.each do |file|
  current = nil
  pending = nil
  File.foreach(file, encoding: 'bom|utf-8') do |line|
    if line =~ /\[Ruby(?:Class|Module)\((.*)/
      args = $1
      name = args[/\A"([^"]+)"/, 1]
      ext = args[/Extends\s*=\s*typeof\(([^)]+)\)/, 1]
      pending = { name: name, ext: ext, copies: [] }
    end
    if pending && line =~ /Includes\((.*)Copy\s*=\s*true/
      pending[:copies].concat($1.scan(/typeof\(([^)]+)\)/).flatten)
    end
    if pending && line =~ /\bclass\s+(\w+)/
      cls = $1
      key = pending[:name] || "<#{pending[:ext] || cls}>"
      current = csharp[key]
      current[:copies].concat(pending[:copies])
      type_to_ruby[cls] = key
      type_to_ruby[pending[:ext]] = key if pending[:ext]
      pending = nil
    end
    next unless current
    line.scan(/\[RubyMethod\("([^"]+)"(?:\s*,\s*([^)\]]+))?\)/) do |mname, attrs|
      (attrs.to_s.include?('Singleton') ? current[:sing] : current[:inst]) << mname
      # a module function is both
      current[:inst] << mname if attrs.to_s.include?('ModuleFunction')
    end
  end
end

csharp.each_value do |c|
  c[:copies].each do |t|
    src = csharp[type_to_ruby[t]] or next
    c[:inst].merge(src[:inst])
    c[:sing].merge(src[:sing])
  end
end

# --- the prelude ----------------------------------------------------------------------
prelude = Hash.new { |h, k| h[k] = { inst: Set.new, sing: Set.new, iter: Set.new, guarded: Set.new, guarded_iter: Set.new } }

class Walker < Prism::Visitor
  def initialize(out)
    @out = out
    @nest = []
    @sing = 0
    @guards = []
    @module_function = []
    super()
  end

  def name_of(node)
    case node
    when Prism::ConstantReadNode then node.name.to_s
    when Prism::ConstantPathNode then [node.parent && name_of(node.parent), node.name.to_s].compact.join('::')
    end
  end

  def visit_class_node(node) = scoped(name_of(node.constant_path)) { super }
  def visit_module_node(node) = scoped(name_of(node.constant_path)) { super }

  # `class << self' keeps the owner; `class << Dir' at the top level makes Dir the owner of what
  # it defines, as singleton methods
  def visit_singleton_class_node(node)
    owner = node.expression.is_a?(Prism::SelfNode) ? nil : name_of(node.expression)
    @nest.push(owner) if owner
    @sing += 1
    super
  ensure
    @sing -= 1
    @nest.pop if owner
  end

  # `module_function :a, :b' makes Kernel#a also Kernel.a; a bare `module_function' does so for
  # every def after it in the same module body
  def visit_call_node(node)
    if node.name == :module_function && node.receiver.nil? && @sing == 0
      entry = @out[@nest.empty? ? 'Object' : @nest.join('::')]
      args = node.arguments&.arguments || []
      if args.empty?
        @module_function[-1] = true unless @module_function.empty?
      else
        args.each { |a| entry[:sing] << a.unescaped if a.is_a?(Prism::SymbolNode) }
      end
    end
    super
  end

  def scoped(name)
    @nest.push(name)
    @module_function.push(false)
    yield
  ensure
    @nest.pop
    @module_function.pop
  end

  # `def m ... end unless method_defined?(:m)': the prelude's definition only where C# has none
  def visit_unless_node(node)
    guard = node.predicate
    name = guard.is_a?(Prism::CallNode) && guard.name.to_s.end_with?('method_defined?') &&
      guard.arguments&.arguments&.first.then { |a| a.is_a?(Prism::SymbolNode) ? a.unescaped : nil }
    return super unless name
    @guards.push(name)
    super
  ensure
    @guards.pop if name
  end

  def visit_def_node(node)
    owner = @nest.join('::')
    owner = 'Object' if owner.empty?
    entry = @out[owner]
    name = node.name.to_s
    if @guards.include?(name) && !node.receiver && @sing == 0
      entry[:guarded] << name
      entry[:guarded_iter] << name if node.slice =~ /\byield\b|\bblock_given\?|&\w+/
      return
    end
    (node.receiver || @sing > 0 ? entry[:sing] : entry[:inst]) << name
    entry[:sing] << name if !node.receiver && @sing == 0 && @module_function.last
    # does the body yield or call a block parameter? (a block-taking iterator)
    src = node.slice
    entry[:iter] << name if src =~ /\byield\b|\bblock_given\?|&\w+/
    # no descent: a def inside a def is not a core method of the class
  end
end

Prism.parse_file(PRELUDE).value.accept(Walker.new(prelude))

# a guarded definition is the method only where C# does not define it
prelude.each do |k, r|
  cs = csharp.key?(k) ? csharp[k][:inst] : Set.new
  r[:guarded].each do |m|
    next if cs.include?(m)
    r[:inst] << m
    r[:iter] << m if r[:guarded_iter].include?(m)
  end
end

# --- report ---------------------------------------------------------------------------
wanted = ARGV.empty? ? %w[Array Hash String Integer Float Enumerable Kernel Range Struct Comparable Symbol Proc Module Numeric Enumerator] : ARGV

printf("%-12s %8s %8s %10s %10s\n", 'class', 'C# only', 'Ruby', 'replaces C#', 'Ruby w/ block')
tot = [0, 0, 0, 0]
wanted.each do |k|
  c = csharp.key?(k) ? csharp[k] : { inst: Set.new, sing: Set.new }
  r = prelude.key?(k) ? prelude[k] : { inst: Set.new, sing: Set.new, iter: Set.new, guarded: Set.new }
  cs = c[:inst].map { |m| "##{m}" } + c[:sing].map { |m| ".#{m}" }
  rb = r[:inst].map { |m| "##{m}" } + r[:sing].map { |m| ".#{m}" }
  # helpers (__ir_x__) are not core methods of the class
  rb_public = rb.reject { |m| m =~ /\A[#.]__/ }
  shadow = rb_public & cs
  row = [(cs - rb).size, rb_public.size, shadow.size, (r[:iter].reject { |m| m.start_with?('__') }).size]
  row.each_with_index { |v, i| tot[i] += v }
  printf("%-12s %8d %8d %10d %10d\n", k, *row)
  if ENV['VERBOSE']
    puts "  replaces C#: #{shadow.sort.join(' ')}"
    puts "  Ruby only:   #{(rb_public - cs).sort.join(' ')}" if ENV['VERBOSE'] == '2'
  end
end
printf("%-12s %8d %8d %10d %10d\n", 'total', *tot)

all_cs = csharp.values.sum { |c| c[:inst].size + c[:sing].size }
all_rb = prelude.values.sum { |c| (c[:inst].to_a + c[:sing].to_a).count { |m| m !~ /\A__/ } }
helpers = prelude.values.sum { |c| (c[:inst].to_a + c[:sing].to_a).count { |m| m =~ /\A__/ } }
puts
puts "whole tree: #{all_cs} [RubyMethod] names in Builtins+Extensions, #{all_rb} prelude defs, #{helpers} prelude __helpers__"

# WHERE='Array#sum Enumerable#inject' says for each which side defines it.
if ENV['WHERE']
  puts
  ENV['WHERE'].split.each do |spec|
    k, m = spec.split('#', 2)
    in_cs = csharp.key?(k) && csharp[k][:inst].include?(m)
    in_rb = prelude.key?(k) && prelude[k][:inst].include?(m)
    puts "#{spec}: #{in_rb ? (in_cs ? 'Ruby (replaces C#)' : 'Ruby') : in_cs ? 'C#' : '?'}"
  end
end
