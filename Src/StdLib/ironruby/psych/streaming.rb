# frozen_string_literal: true
#
# Vendored from the psych gem (lib/psych/streaming.rb), which is pure Ruby.
# IronRuby's Psych is its own C#-backed implementation and did not ship the
# visitors at all.  Gem::Specification#to_yaml and anything else that builds a
# Psych::Nodes tree from Ruby objects names Psych::Visitors::YAMLTree directly,
# so the constant has to be the real thing.
module Psych
  module Streaming
    module ClassMethods
      ###
      # Create a new streaming emitter.  Emitter will print to +io+.  See
      # Psych::Stream for an example.
      def new io
        emitter      = const_get(:Emitter).new(io)
        class_loader = ClassLoader.new
        ss           = ScalarScanner.new class_loader
        super(emitter, ss, {})
      end
    end

    ###
    # Start streaming using +encoding+
    def start encoding = Nodes::Stream::UTF8
      super.tap { yield self if block_given?  }
    ensure
      finish if block_given?
    end

    private
    def register target, obj
    end
  end
end
