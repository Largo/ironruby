# frozen_string_literal: true
#
# Vendored from the psych gem (lib/psych/visitors/visitor.rb), which is pure Ruby.
# IronRuby's Psych is its own C#-backed implementation and did not ship the
# node-to-Ruby visitor at all, so a program that walks a Psych::Nodes tree
# itself - RuboCop reads every config through Psych::Visitors::ToRuby - had no
# constant to name.  These files are that visitor and what it needs; nothing
# about them is implementation-specific, and Psych::Nodes here answers the same
# API they were written against.
module Psych
  module Visitors
    class Visitor
      def accept target
        visit target
      end

      private

      # @api private
      def self.dispatch_cache
        Hash.new do |hash, klass|
          hash[klass] = :"visit_#{klass.name.gsub('::', '_')}"
        end.compare_by_identity
      end

      if defined?(Ractor)
        def dispatch
          @dispatch_cache ||= (Ractor.current[:Psych_Visitors_Visitor] ||= Visitor.dispatch_cache)
        end
      else
        DISPATCH = dispatch_cache
        def dispatch
          DISPATCH
        end
      end

      def visit target
        send dispatch[target.class], target
      end
    end
  end
end
