# frozen_string_literal: true
#
# Vendored from the psych gem (lib/psych/class_loader.rb), which is pure Ruby.
# IronRuby's Psych is its own C#-backed implementation and did not ship the
# node-to-Ruby visitor at all, so a program that walks a Psych::Nodes tree
# itself - RuboCop reads every config through Psych::Visitors::ToRuby - had no
# constant to name.  These files are that visitor and what it needs; nothing
# about them is implementation-specific, and Psych::Nodes here answers the same
# API they were written against.

module Psych
  class ClassLoader # :nodoc:
    BIG_DECIMAL = 'BigDecimal'
    COMPLEX     = 'Complex'
    DATA        = 'Data' unless RUBY_VERSION < "3.2"
    DATE        = 'Date'
    DATE_TIME   = 'DateTime'
    EXCEPTION   = 'Exception'
    OBJECT      = 'Object'
    PSYCH_OMAP  = 'Psych::Omap'
    PSYCH_SET   = 'Psych::Set'
    RANGE       = 'Range'
    RATIONAL    = 'Rational'
    REGEXP      = 'Regexp'
    STRUCT      = 'Struct'
    SYMBOL      = 'Symbol'

    def initialize
      @cache = CACHE.dup
    end

    def load klassname
      return nil if !klassname || klassname.empty?

      find klassname
    end

    def symbolize sym
      symbol
      sym.to_sym
    end

    constants.each do |const|
      konst = const_get const
      class_eval <<~RUBY, __FILE__, __LINE__ + 1
        def #{const.to_s.downcase}
          load #{konst.inspect}
        end
      RUBY
    end

    private

    def find klassname
      @cache[klassname] ||= resolve(klassname)
    end

    def resolve klassname
      name    = klassname
      retried = false

      begin
        path2class(name)
      rescue ArgumentError, NameError => ex
        unless retried
          name    = "Struct::#{name}"
          retried = ex
          retry
        end
        raise retried
      end
    end

    CACHE = Hash[constants.map { |const|
      val = const_get const
      begin
        [val, ::Object.const_get(val)]
      rescue
        nil
      end
    }.compact].freeze

    class Restricted < ClassLoader
      def initialize classes, symbols
        @classes = classes
        @symbols = symbols
        super()
      end

      def symbolize sym
        return super if @symbols.empty?

        if @symbols.include? sym
          super
        else
          raise DisallowedClass.new('load', 'Symbol')
        end
      end

      private

      def find klassname
        if @classes.include? klassname
          super
        else
          raise DisallowedClass.new('load', klassname)
        end
      end
    end
  end
end
