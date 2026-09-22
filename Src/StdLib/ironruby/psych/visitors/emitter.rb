# frozen_string_literal: true
#
# Vendored from the psych gem (lib/psych/visitors/emitter.rb), unchanged.  It walks a
# Psych::Nodes tree and replays it as events into a Psych::Emitter, which is what writes the
# YAML text.  RubyGems emits a gemspec this way (Gem::Specification#to_yaml builds the tree
# with Gem::NoAliasYAMLTree and hands it to this visitor), and so does Nodes::Node#yaml.
#
# Upstream Psych::Emitter is libyaml; here it is the handler in psych.rb that feeds
# IronRuby's own YAML emitter.  It takes the same event calls, so this file needs no change.
module Psych
  module Visitors
    class Emitter < Psych::Visitors::Visitor
      def initialize io, options = {}
        opts = [:indentation, :canonical, :line_width].find_all { |opt|
          options.key?(opt)
        }

        if opts.empty?
          @handler = Psych::Emitter.new io
        else
          du = Handler::DumperOptions.new
          opts.each { |option| du.send :"#{option}=", options[option] }
          @handler = Psych::Emitter.new io, du
        end
      end

      def visit_Psych_Nodes_Stream o
        @handler.start_stream o.encoding
        o.children.each { |c| accept c }
        @handler.end_stream
      end

      def visit_Psych_Nodes_Document o
        @handler.start_document o.version, o.tag_directives, o.implicit
        o.children.each { |c| accept c }
        @handler.end_document o.implicit_end
      end

      def visit_Psych_Nodes_Scalar o
        @handler.scalar o.value, o.anchor, o.tag, o.plain, o.quoted, o.style
      end

      def visit_Psych_Nodes_Sequence o
        @handler.start_sequence o.anchor, o.tag, o.implicit, o.style
        o.children.each { |c| accept c }
        @handler.end_sequence
      end

      def visit_Psych_Nodes_Mapping o
        @handler.start_mapping o.anchor, o.tag, o.implicit, o.style
        o.children.each { |c| accept c }
        @handler.end_mapping
      end

      def visit_Psych_Nodes_Alias o
        @handler.alias o.anchor
      end
    end
  end
end
