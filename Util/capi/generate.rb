# Generates the two halves of the C API's function table from Src/Ruby/CApi/functions.txt:
#
#   ruby Util/capi/generate.rb
#
#   Src/Ruby/CApi/native/forwarders.generated.c   one exported C function per prototype; it
#                                                  calls through rb_ir_fns and, if the call
#                                                  raised, unwinds to the innermost Ruby frame
#   Src/Ruby/CApi/CApi.Generated.cs               the [UnmanagedCallersOnly] entry points that
#                                                  table points at, each calling CApi.<name>
#
# functions.txt holds C prototypes as ruby.h declares them, one per line. The C side copies
# them verbatim (the shim includes ruby.h, so the compiler checks them against CRuby's
# declarations); the C# side maps each parameter type with TYPES below. A prototype whose type
# is not in TYPES is an error here rather than a silent guess.
root = File.expand_path('../..', __dir__)
DEF = File.join(root, 'Src/Ruby/CApi/functions.txt')
C_OUT = File.join(root, 'Src/Ruby/CApi/native/forwarders.generated.c')
CS_OUT = File.join(root, 'Src/Ruby/CApi/CApi.Generated.cs')

TYPES = {
  'void' => 'void',
  'VALUE' => 'ulong', 'ID' => 'ulong', 'SIGNED_VALUE' => 'long',
  'int' => 'int', 'unsigned int' => 'uint',
  # C's long is 64-bit on Unix and 32-bit on Windows: CLong/CULong are the CLR's types for it,
  # converted to long/ulong at the call (CApi.*.cs work in long)
  'long' => 'CLong', 'unsigned long' => 'CULong',
  'long long' => 'long', 'unsigned long long' => 'ulong',
  'short' => 'short', 'unsigned short' => 'ushort',
  'size_t' => 'nuint', 'ssize_t' => 'nint', 'st_index_t' => 'ulong', 'st_data_t' => 'ulong',
  'double' => 'double', 'float' => 'float',
  'char' => 'byte', 'bool' => 'byte', '_Bool' => 'byte', 'rb_event_flag_t' => 'uint',
  'time_t' => 'long', 'rb_pid_t' => 'int', 'pid_t' => 'int',
  'uintptr_t' => 'nuint', 'intptr_t' => 'nint', 'uint32_t' => 'uint', 'int32_t' => 'int',
  'uint64_t' => 'ulong', 'int64_t' => 'long',
  # function pointer typedefs
  'rb_block_call_func_t' => 'IntPtr', 'rb_alloc_func_t' => 'IntPtr', 'RUBY_DATA_FUNC' => 'IntPtr',
  'rb_unblock_function_t' => 'IntPtr', 'rb_postponed_job_func_t' => 'IntPtr',
  # structs passed by value (CApi.Time.cs)
  'timeval' => 'TimeVal', 'timespec' => 'TimeSpec',
}
POINTERS = {
  'char' => 'byte*', 'VALUE' => 'ulong*', 'ID' => 'ulong*', 'int' => 'int*', 'long' => 'CLong*',
  'double' => 'double*', 'size_t' => 'nuint*', 'unsigned int' => 'uint*', 'unsigned long' => 'CULong*',
  'timespec' => 'TimeSpec*', 'timeval' => 'TimeVal*',
}

def cs_type(ctype)
  t = ctype.gsub(/\bconst\b|\bvolatile\b|\bstruct\b|\benum\b|\brestrict\b/, ' ').squeeze(' ').strip
  if t.end_with?('*')
    base = t.sub(/\s*\*+\z/, '').strip
    stars = t[/\*+\z/].size
    return 'void*' if stars > 1 && base != 'char'
    return (stars == 1 ? POINTERS.fetch(base, 'void*') : 'byte**')
  end
  TYPES.fetch(t) { raise "functions.txt: no C# type for #{ctype.inspect} (#{$cur_line})" }
end

CS_KEYWORDS = %w[base object string params out ref in event checked fixed lock operator new class default this
                 internal is as decimal delegate implicit explicit namespace override readonly sealed sizeof typeof unchecked virtual volatile]
def cs_name(n) = CS_KEYWORDS.include?(n) ? "@#{n}" : n

Fn = Struct.new(:ret, :name, :params, :text, :line)
Param = Struct.new(:ctype, :name, :cs)

fns = []
File.readlines(DEF).each_with_index do |line, i|
  $cur_line = line.strip
  line = line.sub(/#.*/, '').strip
  next if line.empty?
  m = line.match(/\A(?<ret>.+?)\s*\b(?<name>\w+)\s*\((?<params>.*)\)\s*;\z/) or raise "functions.txt:#{i + 1}: not a prototype: #{line}"
  params = []
  plist = m[:params].strip
  unless plist == 'void' || plist.empty?
    # split on commas not inside parentheses
    depth = 0; cur = +''; parts = []
    plist.each_char do |c|
      depth += 1 if c == '('
      depth -= 1 if c == ')'
      if c == ',' && depth == 0 then parts << cur; cur = +'' else cur << c end
    end
    parts << cur
    parts.each_with_index do |p, pi|
      p = p.strip
      bare = p.gsub(/\bconst\b|\bstruct\b|\benum\b|\*/, ' ').squeeze(' ').strip
      if TYPES.key?(bare) || %w[rb_encoding void].include?(bare)
        # an unnamed parameter, as some prototypes in ruby.h have them
        params << Param.new("#{p} arg#{pi}", "arg#{pi}", cs_type(p))
      elsif (fm = p.match(/\(\s*\*\s*(\w+)\s*\)/))
        params << Param.new(p, fm[1], 'IntPtr')
      else
        pm = p.match(/\A(.*?)(\w+)\s*(\[\s*\])?\z/) or raise "functions.txt:#{i + 1}: bad parameter #{p}"
        ctype = pm[1].strip
        ctype += ' *' if pm[3]
        params << Param.new(p, pm[2], cs_type(ctype))
      end
    end
  end
  fns << Fn.new(m[:ret].strip, m[:name], params, line, i + 1)
end
# A name ruby.h #defines to another function is never called by that name: list the target.
inc = File.join(root, 'Src/StdLib/include')
arch = File.join(inc, RbConfig::CONFIG['arch'])
macros = IO.popen(['gcc', '-dM', '-E', "-I#{inc}", "-I#{arch}", '-x', 'c', '-'], 'r+') { |io|
  io.write("#include \"ruby.h\"\n#include \"ruby/encoding.h\"\n"); io.close_write; io.read }
# Functions declared noreturn: their forwarders always unwind (the managed side always raises).
pre = IO.popen(['gcc', '-E', '-P', "-I#{inc}", "-I#{arch}", '-x', 'c', '-'], 'r+') { |io|
  io.write("#include \"ruby.h\"\n#include \"ruby/encoding.h\"\n"); io.close_write; io.read }
noreturn = pre.split(';').select { |d| d.include?('__noreturn__') }.map { |d| d[/(\w+)\s*\([^()]*(\([^()]*\)[^()]*)*\)\s*\z/, 1] }.compact
aliases = macros.scan(/^#define (\w+) (\w+)$/).to_h
fns.each do |f|
  if (target = aliases[f.name]) && target != f.name
    raise "functions.txt:#{f.line}: #{f.name} is a macro for #{target} in ruby.h; list #{target} instead"
  end
end
dups = fns.group_by(&:name).select { |_, v| v.size > 1 }.keys
raise "functions.txt: duplicates #{dups.join(', ')}" unless dups.empty?

header = "/* Generated by Util/capi/generate.rb from Src/Ruby/CApi/functions.txt - do not edit. */\n"

c = +header
c << "#include \"capi.h\"\n\n"
# function-like macros (rb_intern, rb_str_new...) must not rewrite the definitions below
fns.each { |f| c << "#undef #{f.name}\n" }
c << "\n"
c << "struct rb_ir_function_table {\n"
fns.each do |f|
  plist = f.params.empty? ? 'void' : f.params.map(&:ctype).join(', ')
  c << "    #{f.ret} (*#{f.name})(#{plist});\n"
end
c << "};\n\n"
c << "static struct rb_ir_function_table rb_ir_fns;\n"
c << "const int rb_ir_function_count = #{fns.size};\n\n"
c << "void rb_ir_set_functions(void **table) {\n    memcpy(&rb_ir_fns, table, sizeof(rb_ir_fns));\n}\n\n"
fns.each do |f|
  plist = f.params.empty? ? 'void' : f.params.map(&:ctype).join(', ')
  args = f.params.map(&:name).join(', ')
  c << "#{f.ret}\n(#{f.name})(#{plist})\n{\n"
  if f.ret == 'void'
    c << "    rb_ir_fns.#{f.name}(#{args});\n"
    c << (noreturn.include?(f.name) ? "    rb_ir_throw();\n" : "    RB_IR_CHECK();\n")
  else
    c << "    #{f.ret} rb_ir_result = rb_ir_fns.#{f.name}(#{args});\n    RB_IR_CHECK();\n    return rb_ir_result;\n"
  end
  c << "}\n\n"
end
File.write(C_OUT, c)

cs = +"// Generated by Util/capi/generate.rb from Src/Ruby/CApi/functions.txt - do not edit.\n"
cs << <<~CS
  using System;
  using System.Runtime.CompilerServices;
  using System.Runtime.InteropServices;

  namespace IronRuby.Runtime.CApi {
      internal static unsafe partial class CApi {
          internal const int FunctionCount = #{fns.size};

          internal static void FillFunctionTable(IntPtr* table) {
  CS
fns.each_with_index do |f, i|
  sig = (f.params.map(&:cs) + [cs_type(f.ret)]).join(', ')
  cs << "            table[#{i}] = (IntPtr)(delegate* unmanaged[Cdecl]<#{sig}>)&Entry_#{f.name};\n"
end
cs << "        }\n"
fns.each do |f|
  ret = cs_type(f.ret)
  plist = f.params.map { |p| "#{p.cs} #{cs_name(p.name)}" }.join(', ')
  args = f.params.map { |p|
    case p.cs
    when 'CLong' then "(long)#{cs_name(p.name)}.Value"
    when 'CULong' then "(ulong)#{cs_name(p.name)}.Value"
    else cs_name(p.name)
    end
  }.join(', ')
  call = "#{f.name}(#{args})"
  call = "new CLong(checked((nint)#{call}))" if ret == 'CLong'
  call = "new CULong(checked((nuint)#{call}))" if ret == 'CULong'
  cs << "\n        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n"
  cs << "        private static #{ret} Entry_#{f.name}(#{plist}) {\n"
  cs << "            var frame = EnterManaged();\n"
  cs << "            try {\n"
  if ret == 'void'
    cs << "                #{f.name}(#{args});\n"
  else
    cs << "                return #{call};\n"
  end
  cs << "            } catch (Exception e) {\n                SetPending(e);\n"
  cs << "                return#{ret == 'void' ? '' : ' default'};\n"
  cs << "            } finally {\n                LeaveManaged(frame);\n            }\n        }\n"
end
cs << "    }\n}\n"
File.write(CS_OUT, cs)
puts "#{fns.size} functions -> #{C_OUT}, #{CS_OUT}"
