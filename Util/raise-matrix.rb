# Differential matrix for Kernel#raise argument handling and Exception#cause.
# Run under CRuby and under ./ir.sh and diff the output.
#
#   ruby    Util/raise-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/raise-matrix.rb > /tmp/ir.txt
#   diff -u /tmp/cruby.txt /tmp/ir.txt

def show(label)
  r = yield
  puts "#{label} => OK #{r.inspect}"
rescue Exception => e
  c = (e.cause rescue :NO_CAUSE)
  puts "#{label} => #{e.class}: #{e.message.inspect} cause=#{c.inspect}"
end

Weird = Class.new(StandardError) do
  attr_reader :data
  def initialize(data = nil); @data = data; end
end
NoArg = Class.new(Exception) do
  def initialize; end
end

show("bare")                { raise }
show("string")              { raise "a bad thing" }
show("class")               { raise ArgumentError }
show("class,msg")           { raise ArgumentError, "message" }
show("class,msg,bt")        { raise ArgumentError, "message", ["l1", "l2"] }
show("noarg-ctor class")    { raise NoArg }
show("instance")            { raise RuntimeError.new("inst") }
show("nil")                 { raise nil }
show("true")                { raise true }
show("false")               { raise false }
show("Object.new")          { raise Object.new }
show("Object.new,msg")      { raise Object.new, "message" }
show("Object.new,msg,bt")   { raise Object.new, "message", [] }
show("4 args")              { raise ArgumentError, "m", [], 1 }
show("hash message")        { raise Weird, {data: 42} }
show("kw message")          { raise Weird, data: 42 }
show("string + hash arg")   { raise "message", {cause: RuntimeError.new} }
show("to_hash exc")         { raise(Class.new(StandardError) { def to_hash; {}; end }.new) }

e = Object.new
def e.exception = StandardError.new("from #exception")
show("respond exception/0") { raise e }

e1 = Object.new
def e1.exception(msg) = StandardError.new(msg)
show("respond exception/1")     { raise e1, "foo" }
show("respond exception/1 arity") { raise e1 }

e2 = Object.new
def e2.exception; Array; end
show("exception returns class") { raise e2 }

# ---- cause ----------------------------------------------------------------
show("cause: default no $!")     { raise "no cause" }
show("cause: explicit")          { raise "x", cause: StandardError.new("c") }
show("cause: nil")               { raise "x", cause: nil }
show("cause: only")              { raise(cause: StandardError.new("c")) }
show("cause: only nil")          { raise(cause: nil) }
show("cause: not exception")     { raise "x", cause: Object.new }
show("cause: class+msg+cause")   { raise ArgumentError, "m", cause: StandardError.new("c") }
show("cause: class+msg+bt+cause") { raise ArgumentError, "m", ["l1"], cause: StandardError.new("c") }

show("cause: auto chain") do
  begin
    raise StandardError, "first"
  rescue
    raise "second"
  end
end

show("cause: self") do
  c = StandardError.new("cc")
  raise c, cause: c
end

show("cause: circular") do
  begin
    raise "Error 1"
  rescue => error1
    begin
      raise "Error 2"
    rescue
      begin
        raise "Error 3"
      rescue => error3
        raise error1, cause: error3
      end
    end
  end
end

# cause is only assigned once
show("cause: reraise keeps first") do
  err = nil
  begin
    begin
      raise "inner"
    rescue
      raise StandardError, "outer"
    end
  rescue => err
  end
  begin
    raise err
  rescue => e2
    "reraised cause=#{e2.cause.inspect}"
  end
end

# ---- Exception#cause presence ---------------------------------------------
puts "Exception#cause defined? #{Exception.method_defined?(:cause)}"
puts "no-raise cause: #{StandardError.new('x').cause.inspect}"

# ---- a rescue above a passed ensure (Docs/rescue-above-ensure.md) -----------
# The class list of a rescue is evaluated after the ensure clauses of the frames the exception left have run,
# and those clauses can change what it answers; CLR finally blocks between (a Mutex's unlock, File.open's
# close) run before an ensure outside them. Each row raises below an ensure and rescues above it.
$pe_log = []
def pe_log(x) = ($pe_log << x; x)
def pe_inner
  begin
    raise ArgumentError, "inner"
  ensure
    pe_log(:inner_ensure)
  end
end
def pe_classes = (pe_log(:class_list); ArgumentError)
show("pe: ensure runs before the class list") { $pe_log.clear; begin; pe_inner; rescue pe_classes => e; pe_log(:rescued); end; $pe_log }
show("pe: no match, ensure ran once") { $pe_log.clear; begin; begin; pe_inner; rescue TypeError; end; rescue ArgumentError; end; $pe_log }
module PeK; end
PeK.const_set(:Target, TypeError)
def pe_reassign
  begin
    raise ArgumentError, "re"
  ensure
    PeK.send(:remove_const, :Target); PeK.const_set(:Target, ArgumentError)
  end
end
show("pe: constant reassigned in the ensure") { PeK.send(:remove_const, :Target); PeK.const_set(:Target, TypeError); begin; pe_reassign; rescue PeK::Target => e; "rescued #{e.message}"; end }
PeCls = Class.new(StandardError)
def pe_eqq
  begin
    raise ArgumentError, "eqq"
  ensure
    def PeCls.===(o) = true
  end
end
show("pe: === defined in the ensure") { begin; pe_eqq; rescue PeCls => e; "rescued #{e.message}"; ensure; PeCls.singleton_class.send(:remove_method, :===) rescue nil; end }
PeMod = Module.new
def pe_extend
  begin
    raise ArgumentError, "ext"
  ensure
    $!.extend(PeMod)
  end
end
show("pe: exception extended in the ensure") { begin; pe_extend; rescue PeMod => e; "rescued #{e.message}"; end }
def pe_bang
  begin
    raise ArgumentError, "bang"
  ensure
    pe_log([:bang_in_ensure, $!&.message])
  end
end
show("pe: $! in the ensure and after") { $pe_log.clear; begin; pe_bang; rescue TypeError; rescue ArgumentError; end; [$pe_log, $!] }
def pe_ensure_raises
  begin
    raise ArgumentError, "first"
  ensure
    raise TypeError, "second"
  end
end
show("pe: the ensure raises") { begin; begin; pe_ensure_raises; rescue ArgumentError; :wrong; end; rescue TypeError => e; [e.message, e.cause&.message]; end }
def pe_return
  begin
    raise ArgumentError, "ret"
  ensure
    return :returned
  end
end
show("pe: return from the ensure") { begin; pe_return; rescue ArgumentError; :rescued; end }
show("pe: break from the ensure") { begin; [1].each { begin; raise ArgumentError, "brk"; ensure; break :broke; end }; rescue ArgumentError; :rescued; end }
show("pe: next from the ensure") { begin; [1].map { begin; raise ArgumentError, "nxt"; ensure; next :nexted; end }; rescue ArgumentError; :rescued; end }
show("pe: throw from the ensure") { catch(:pe) { begin; begin; raise ArgumentError, "thr"; ensure; throw :pe, :thrown; end; rescue ArgumentError; :rescued; end } }
show("pe: retry above") { n = 0; begin; n += 1; begin; raise ArgumentError, "try#{n}" if n < 3; ensure; pe_log(n); end; rescue TypeError; rescue ArgumentError; retry; end; n }
def pe_mutex(m)
  begin
    m.synchronize { raise ArgumentError, "mx" }
  ensure
    pe_log([:locked, m.locked?])
  end
end
show("pe: a Mutex unlocked before the outer ensure") { $pe_log.clear; begin; pe_mutex(Mutex.new); rescue TypeError; rescue ArgumentError; end; $pe_log }
def pe_file(path)
  f = nil
  begin
    File.open(path) { |io| f = io; raise ArgumentError, "fo" }
  ensure
    pe_log([:closed, f.closed?])
  end
end
show("pe: a File closed before the outer ensure") { $pe_log.clear; begin; pe_file(__FILE__); rescue TypeError; rescue ArgumentError; end; $pe_log }
show("pe: backtrace has the inner frame") { begin; begin; pe_inner; rescue TypeError; end; rescue ArgumentError => e; e.backtrace.any? { |l| l.include?("pe_inner") }; end }
show("pe: Thread#raise during the ensure") do
  q = Queue.new
  th = Thread.new do
    begin
      begin
        raise ArgumentError, "orig"
      ensure
        q.pop
      end
    rescue TypeError => e
      [:replaced, e.message, e.cause&.message]
    rescue ArgumentError
      :orig_rescued
    end
  end
  Thread.pass until th.status == "sleep"
  th.raise(TypeError, "async")
  th.value
end
show("pe: Thread#kill during the ensure") do
  q = Queue.new; log = Queue.new
  th = Thread.new do
    begin
      begin
        raise ArgumentError, "orig"
      ensure
        log << :ensure_started
        q.pop
        log << :not_reached
      end
    rescue ArgumentError
      log << :rescued
    end
  end
  Thread.pass until th.status == "sleep"
  th.kill; th.join
  r = []; r << log.pop until log.empty?; r
end
