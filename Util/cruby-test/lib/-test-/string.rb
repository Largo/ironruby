# frozen_string_literal: true
# CRuby's ext/-test-/string is a C extension that reads a String's internals (its capacity, the
# byte past its end). test_string.rb requires it at the top, so without it none of the file's
# tests load. IronRuby has no such internals: the few tests that call these fail with
# NotImplementedError, and the rest of the file runs.
module Bug
  class String < ::String
    %i[capacity cstr_term_char cstr_term cstr_unterm fstring new_with_capa].each do |name|
      define_singleton_method(name) { |*| raise NotImplementedError, "Bug::String.#{name} reads CRuby's string internals" }
    end
  end
end
