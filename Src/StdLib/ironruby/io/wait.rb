# io/wait is a C extension in MRI. Blocking IO here is synchronous, so the
# predicates answer "ready" and the waits are no-ops that return self.
class IO
  READABLE = 1 unless const_defined?(:READABLE)
  WRITABLE = 2 unless const_defined?(:WRITABLE)
  PRIORITY = 4 unless const_defined?(:PRIORITY)

  def wait(*_args); self; end unless method_defined?(:wait)
  def wait_readable(*_args); self; end unless method_defined?(:wait_readable)
  def wait_writable(*_args); self; end unless method_defined?(:wait_writable)
  def nread; 0; end unless method_defined?(:nread)
end
