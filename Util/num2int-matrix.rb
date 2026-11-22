# Differential matrix for the C integer conversions core methods read their arguments with:
# MRI's NUM2INT (format's "*" width and precision, %c, Integer#round's digits, #to_s's radix,
# Array#flatten's level...) and NUM2LONG (Array#first, #sample, #rotate, #cycle, String#[]...).
#
#   ruby    Util/num2int-matrix.rb > /tmp/n2i.mri
#   ./ir.sh Util/num2int-matrix.rb > /tmp/n2i.ir
#   diff /tmp/n2i.mri /tmp/n2i.ir
#
# What the two have to agree on is the error and its message: a Float out of range is printed
# with "%-.10g" ("float 1e+20 out of range of integer", "float Inf ..."), an Integer that is
# still a long but no int is named ("integer 2147483648 too big to convert to 'int'"), one that
# is no long is "bignum too big to convert into 'long'", whether it was given or a #to_int gave
# it, and true and false are named by value ("no implicit conversion of true into Integer").
#
# IronRuby's int-typed parameters cannot take what CRuby reads as a long, so a method that
# takes a long in MRI still raises where MRI succeeds (Array#first(2**40)); LONG_ONLY leaves
# those numbers out for those methods. And the wording ruby/spec's raise_consistent_error pins
# for every engine but CRuby < 4.1 is IronRuby's (see canonical), as CLAUDE.md explains.

def canonical(message)
  message.sub(/\Ano implicit conversion from nil to integer\z/, "no implicit conversion of nil into Integer")
         .sub(/\Acan't convert (\S+) to (Integer|String) \(.*\)\z/, "can't convert \\1 into \\2")
end

def try(label)
  v = yield
  v = v.size if v.is_a?(Array) || v.is_a?(String) && v.size > 20
  puts "#{label}\t#{v.inspect}"
rescue Exception => e
  puts "#{label}\t#{e.class}: #{canonical(e.message)}"
end

def obj(name, result)
  o = Object.new
  o.define_singleton_method(:to_int) { result }
  o.define_singleton_method(:inspect) { name }
  o
end

VALUES = {
  "0" => 0, "3" => 3, "-3" => -3,
  "2**31-1" => 2**31 - 1, "2**31" => 2**31, "-2**31" => -2**31, "-2**31-1" => -2**31 - 1,
  "2**40" => 2**40, "-2**40" => -2**40, "2**63-1" => 2**63 - 1, "2**63" => 2**63,
  "-2**63" => -2**63, "-2**63-1" => -2**63 - 1, "2**70" => 2**70, "-2**70" => -2**70,
  "2.5" => 2.5, "-2.5" => -2.5, "2.0**31" => 2.0**31, "-2.0**31-1" => -2.0**31 - 1,
  "2.0**40" => 2.0**40, "2.0**62" => 2.0**62, "2.0**63" => 2.0**63, "-2.0**63" => -2.0**63,
  "-2.0**64" => -2.0**64, "1e19" => 1e19, "1.5e19" => 1.5e19, "1e20" => 1e20, "-1e20" => -1e20,
  "1.23456789012e30" => 1.23456789012e30, "NaN" => Float::NAN, "Inf" => Float::INFINITY,
  "-Inf" => -Float::INFINITY, "nil" => nil, "true" => true, "false" => false, ":a" => :a,
  "'1'" => "1", "[1]" => [1], "3r" => 3r, "Complex(3,0)" => Complex(3, 0),
  "to_int 3" => obj("to_int 3", 3), "to_int 2**40" => obj("to_int 2**40", 2**40),
  "to_int -2**40" => obj("to_int -2**40", -2**40), "to_int 2**70" => obj("to_int 2**70", 2**70),
  "to_int 2.5" => obj("to_int 2.5", 2.5), "to_int nil" => obj("to_int nil", nil),
  "to_int '1'" => obj("to_int '1'", "1"),
}

# the numbers MRI reads as a long that an int cannot hold (and the Floats that truncate to one)
LONG_ONLY = ["2**31", "-2**31-1", "2**40", "-2**40", "2**63-1", "-2**63", "2.0**31", "-2.0**31-1",
             "2.0**40", "2.0**62", "-2.0**63", "to_int 2**40", "to_int -2**40"]

NUM2INT = {
  "format %*d" => ->(v) { "%*d|" % [v, 1] },
  "format %.*f" => ->(v) { "%.*f" % [v, 1.0] },
  "format %c" => ->(v) { "%c" % [v] },
  "Integer#round" => ->(v) { 15.round(v) },
  "Integer#floor" => ->(v) { 15.floor(v) },
  "Float#round" => ->(v) { 1.5.round(v) },
  "Float#ceil" => ->(v) { 1.5.ceil(v) },
  "Integer#to_s" => ->(v) { 255.to_s(v) },
  "Bignum#to_s" => ->(v) { (2**70).to_s(v) },
  "String#to_i" => ->(v) { "11".to_i(v) },
  "Array#flatten" => ->(v) { [[[1]]].flatten(v) },
}

NUM2LONG = {
  "Array#first" => ->(v) { [1, 2, 3].first(v) },
  "Array#last" => ->(v) { [1, 2, 3].last(v) },
  "Array#[]" => ->(v) { [1, 2, 3][v] },
  "Array#fetch" => ->(v) { [1, 2, 3].fetch(v, :none) },
  "Array#sample" => ->(v) { [1, 2, 3].sample(v) },
  "Array#rotate" => ->(v) { [1, 2, 3].rotate(v) },
  "Array#rotate!" => ->(v) { [1, 2, 3].rotate!(v) },
  "Array#cycle" => ->(v) { r = []; [1].cycle(v) { |x| r << x; break if r.size > 3 }; r },
  "Array#combination" => ->(v) { [1, 2].combination(v).to_a },
  "String#[]" => ->(v) { "abc"[v] },
}

# what CRuby could allocate for a count it takes as a long: these are only run with the numbers
# that fail before any allocation
ALLOCATING = {
  "Array#*" => ->(v) { [1] * v },
  "String#*" => ->(v) { "a" * v },
  "Array.new" => ->(v) { Array.new(v) },
  "String#center" => ->(v) { "a".center(v) },
  "String#ljust" => ->(v) { "a".ljust(v) },
}
FITS_A_LONG = ->(v) {
  n = v.respond_to?(:to_int) && !v.is_a?(Float) && !v.is_a?(String) ? v.to_int : v
  (n.is_a?(Integer) && n.between?(-2**63, 2**63 - 1) && n > 3) ||
    (n.is_a?(Float) && n.finite? && n.abs < 2.0**63 && n > 3)
}

# a width or precision of 2**31-1 is a two gigabyte string, in MRI as here
HUGE_FORMAT = ["2**31-1", "-2**31"]

puts "== NUM2INT =="
NUM2INT.each do |name, f|
  VALUES.each do |vname, v|
    next if name.start_with?("format %") && name != "format %c" && HUGE_FORMAT.include?(vname)
    try("#{name} #{vname}") { f.(v) }
  end
end

puts "== NUM2LONG =="
NUM2LONG.each do |name, f|
  VALUES.each do |vname, v|
    next if LONG_ONLY.include?(vname)
    try("#{name} #{vname}") { f.(v) }
  end
end
ALLOCATING.each do |name, f|
  VALUES.each do |vname, v|
    next if LONG_ONLY.include?(vname) || FITS_A_LONG.(v)
    try("#{name} #{vname}") { f.(v) }
  end
end

puts "== the argument's name: true and false by value =="
S = Struct.new(:a)
FILE = File.open(__FILE__)
{
  "Range#first" => ->(v) { (1..3).first(v) },
  "warn uplevel" => ->(v) { warn("x", uplevel: v) },
  "SystemCallError.new" => ->(v) { SystemCallError.new("x", v) },
  "Enumerator#with_index" => ->(v) { [1].each.with_index(v).to_a },
  "Enumerator.new" => ->(v) { Enumerator.new(v) { } },
  "Math.ldexp" => ->(v) { Math.ldexp(1.0, v) },
  "Process.setrlimit" => ->(v) { Process.setrlimit(:CORE, v) },
  "Random.new" => ->(v) { Random.new(v) },
  "Kernel#rand" => ->(v) { rand(v) },
  "Random.rand" => ->(v) { Random.rand(v) },
  "Random#rand" => ->(v) { Random.new(1).rand(v) },
  "IO#advise" => ->(v) { FILE.advise(:normal, v) },
  "IO#pread" => ->(v) { FILE.pread(1, v) },
  "Struct#dig" => ->(v) { S.new(1).dig(v) },
  "Struct#values_at" => ->(v) { S.new(1).values_at(v) },
}.each do |name, f|
  [true, false].each { |v| try("#{name} #{v}") { f.(v) } }
end
