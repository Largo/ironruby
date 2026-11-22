# frozen_string_literal: true
# ext/-test-/rb_call_super_kw in Ruby: RbCallSuperKw#m is rb_call_super_kw with
# RB_PASS_CALLED_KEYWORDS, a super that passes on the arguments and keywords it was called with.
module Bug
  module RbCallSuperKw
    def m(...) = super(...)
  end
end
