# Lists the C API's function prototypes as the shipped headers declare them.
#
#   ruby Util/capi/prototypes.rb rb_str_new rb_ary_push ...   # those prototypes, for functions.txt
#   ruby Util/capi/prototypes.rb --names                      # every function the headers declare
#
# It runs the headers through the C preprocessor and reads the declarations back, so a
# prototype comes out as one line with attributes and macros expanded away. The shim's build
# (Util/build-capi.sh) uses --names to give every declared function it does not implement a
# stub that raises NotImplementedError.
root = File.expand_path('../..', __dir__)
inc = File.join(root, 'Src/StdLib/include')
arch = File.join(inc, RbConfig::CONFIG['arch'])
abort "no #{arch}/ruby/config.h" unless File.file?(File.join(arch, 'ruby/config.h'))
headers = %w[ruby.h ruby/encoding.h ruby/io.h ruby/thread.h ruby/util.h ruby/st.h ruby/re.h ruby/version.h
             ruby/debug.h ruby/memory_view.h ruby/ractor.h ruby/random.h ruby/fiber/scheduler.h ruby/io/buffer.h
             ruby/vm.h ruby/thread_native.h]
src = headers.map { |h| "#include \"#{h}\"\n" }.join
cc = ENV['CC'] || 'gcc'
pre = IO.popen([cc, '-E', '-P', "-I#{inc}", "-I#{arch}", '-x', 'c', '-'], 'r+') { |io| io.write(src); io.close_write; io.read }
pre = pre.gsub(/^\s*#pragma.*$/, "").gsub("[[maybe_unused]]", "").gsub("[[noreturn]]", "").gsub("[[nodiscard]]", "")
abort 'preprocessor failed' unless $?.success?

def strip_attributes(s)
  out = +''
  i = 0
  while i < s.size
    if s[i..] =~ /\A(__attribute__|__asm__|__asm|__declspec)\s*\(/
      i += $&.size
      depth = 1
      while depth > 0 && i < s.size
        depth += 1 if s[i] == '('
        depth -= 1 if s[i] == ')'
        i += 1
      end
    else
      out << s[i]
      i += 1
    end
  end
  out
end

# Top-level statements: skip bodies of inline functions, structs and enums.
decls = []
cur = +''
depth = 0
pre.each_char do |ch|
  if ch == '{'
    depth += 1
    next
  elsif ch == '}'
    depth -= 1
    if depth == 0
      cur = +'' if cur =~ /\)\s*\z/  # a function body ends the definition
    end
    next
  end
  next if depth > 0
  if ch == ';'
    decls << cur
    cur = +''
  else
    cur << ch
  end
end

protos = {}
decls.each do |d|
  d = strip_attributes(d).gsub(/\s+/, ' ').strip
  d = d.sub(/\A(extern |__extension__ )+/, '')
  next if d.start_with?('typedef', 'static')
  m = d.match(/\A(?<ret>[\w\s\*]+?)\s*\b(?<name>\w+)\s*\((?<params>.*)\)\z/) or next
  next if m[:ret].empty?
  protos[m[:name]] ||= "#{m[:ret].strip} #{m[:name]}(#{m[:params].strip});".gsub(' *', ' *').gsub('* ', '*').gsub(/(\w)\*/, '\1 *')
end

if ARGV == ['--names']
  puts protos.keys.sort
else
  ARGV.each do |n|
    if protos[n]
      puts protos[n]
    else
      warn "no declaration of #{n}"
    end
  end
end
