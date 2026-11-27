# A raise rescued through frames whose rescue clause does not match, with and without an ensure frame below them: a
# constant class list (core or not) is decided in the CLR's first-pass filter (RubyOps.RescuePassMayTest,
# RescueSiteCannotMatch) unless the exception has already passed an ensure (PendingPassedEnsures), when the frame
# catches, completes the backtrace and throws again. us per raise, best of five.
#   IR_CONFIG=Release ./ir.sh Util/bench/rescue-above-ensure-shapes.rb; ruby Util/bench/rescue-above-ensure-shapes.rb
N = 3000
def run(name)
  best = nil
  5.times { t = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); N.times { begin; yield; rescue RuntimeError; end }; d = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - t; best = d if !best || d < best }
  printf "%-52s %8.2f us\n", name, best * 1e6 / N
end
class MyErr < StandardError; end
def ens; begin; raise "x"; ensure; @e = 1; end; end
def ens_call; begin; raise "x"; ensure; fin; end; end
def fin = @f = 1
def r_core(&b); begin; yield; rescue ArgumentError; end; end
def r_user(&b); begin; yield; rescue MyErr; end; end
def r_user3(&b); r_user { r_user { r_user { yield } } }; end
run("bare")                                          { raise "x" }
run("ensure frame only")                             { ens }
run("ensure (calls a method) only")                  { ens_call }
run("no ensure, rescue MyErr above")                 { r_user { raise "x" } }
run("ensure, rescue ArgumentError above")            { r_core { ens } }
run("ensure, rescue MyErr above")                    { r_user { ens } }
run("ensure (call), rescue MyErr above")             { r_user { ens_call } }
run("ensure, 3 x rescue MyErr above")                { r_user3 { ens } }
run("no ensure, 3 x rescue MyErr above")             { r_user3 { raise "x" } }
