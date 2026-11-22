# frozen_string_literal: true
# ext/-test-/iter in Ruby. Yield#yield_block(name, *args) is rb_block_call_kw with
# rb_yield_block: it calls the method on self, passing the keywords on as given, with a block
# that yields what it receives, keywords as keywords, to yield_block's own block.
module Bug
  module Iter
    module Yield
      def yield_block(name, *args, **kw, &block)
        __send__(name, *args, **kw) { |*values, **kws| block.call(*values, **kws) }
      end
    end
  end
end
