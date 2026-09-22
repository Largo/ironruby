# ****************************************************************************
#
# Copyright (c) Microsoft Corporation.
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A
# copy of the license can be found in the License.html file at the root of this distribution. If
# you cannot locate the  Apache License, Version 2.0, please send an email to
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

# Psych's API on top of IronRuby's own YAML engine (IronRuby.Libraries.Yaml, a port of
# JvYAMLb). The engine defines the Psych module, Psych::Exception, Psych::SyntaxError and
# the Syck-era primitives - parse (the first document's node, or false), load (build the
# first document, no restrictions), each_document, parse_documents, dump. This file adds
# what Psych 4 has on top: load is a safe load that permits Symbol and refuses aliases,
# unsafe_load builds anything, and parse answers Psych::Nodes rather than engine nodes.

require 'stringio'
require 'date'
load_assembly 'IronRuby.Libraries.Yaml', 'IronRuby.StandardLibrary.Yaml'

module Psych
  VERSION = '5.3.1'
  LIBYAML_VERSION = '0.2.5'

  # Psych's tag registries.  A library registers a YAML tag for one of its
  # classes here at require time - ActiveSupport does it for
  # ActiveSupport::TimeWithZone, psych's own tests do it for their fixtures -
  # so the tables have to exist and behave like Hashes.
  #
  # What IronRuby does not do yet: consult them.  The YAML engine
  # (IronRuby.Libraries.Yaml) resolves `!ruby/...` tags itself, so a tag
  # registered here is recorded but does not change how a document loads or
  # dumps.  Registering is what libraries do at load time; relying on the tag to
  # round-trip is what does not work.
  @load_tags = {}
  @dump_tags = {}
  @domain_types = {}

  class << self
    attr_accessor :load_tags, :dump_tags, :domain_types
  end

  def self.add_tag(tag, klass)
    load_tags[tag] = klass.name
    dump_tags[klass] = tag
  end

  def self.add_domain_type(domain, type_tag, &block)
    key = ['tag', domain, type_tag].join ':'
    domain_types[key] = [key, block]
    domain_types["tag:#{type_tag}"] = [key, block]
  end

  def self.add_builtin_type(type_tag, &block)
    domain = 'yaml.org,2002'
    key = ['tag', domain, type_tag].join ':'
    domain_types[key] = [key, block]
  end

  def self.remove_type(type_tag)
    domain_types.delete type_tag
  end

  class BadAlias < Psych::Exception
  end

  class AliasesNotEnabled < BadAlias
    def initialize
      super "Alias parsing was not enabled. To enable it, pass `aliases: true` to `Psych::load` or `Psych::safe_load`."
    end
  end

  class AnchorNotDefined < BadAlias
    def initialize(anchor_name)
      super "An alias referenced an unknown anchor: #{anchor_name}"
    end
  end

  class DisallowedClass < Psych::Exception
    def initialize(action, klass_name)
      super "Tried to #{action} unspecified class: #{klass_name}"
    end
  end

  # The engine's entry points, kept under private names: the public ones below are Psych's.
  class << self
    alias_method :__engine_load, :load
    alias_method :__engine_parse, :parse
    private :__engine_load, :__engine_parse
    remove_method :load_file, :parse_file
  end

  def self.unsafe_load(yaml, filename: nil, fallback: false, symbolize_names: false, freeze: false, strict_integer: false)
    node = __parse_node(yaml, filename)
    return fallback unless node
    __finish(node.transform, symbolize_names, freeze)
  end

  def self.safe_load(yaml, permitted_classes: [], permitted_symbols: [], aliases: false, filename: nil,
                     fallback: nil, symbolize_names: false, freeze: false, strict_integer: false)
    node = __parse_node(yaml, filename)
    return fallback unless node
    __check_safe(node, permitted_classes, permitted_symbols, aliases)
    __finish(node.transform, symbolize_names, freeze)
  end

  def self.load(yaml, permitted_classes: [Symbol], permitted_symbols: [], aliases: false, filename: nil,
                fallback: nil, symbolize_names: false, freeze: false, strict_integer: false)
    safe_load(yaml, permitted_classes: permitted_classes, permitted_symbols: permitted_symbols, aliases: aliases,
              filename: filename, fallback: fallback, symbolize_names: symbolize_names, freeze: freeze,
              strict_integer: strict_integer)
  end

  def self.unsafe_load_file(filename, **kwargs)
    File.open(filename) { |f| unsafe_load(f, filename: filename, **kwargs) }
  end

  def self.safe_load_file(filename, **kwargs)
    File.open(filename) { |f| safe_load(f, filename: filename, **kwargs) }
  end

  def self.load_file(filename, **kwargs)
    File.open(filename) { |f| load(f, filename: filename, **kwargs) }
  end

  # Every document, yielded one at a time when there is a block and gathered into an Array
  # when there is not. Like Psych's, it builds any class.
  def self.load_stream(yaml, filename: nil, fallback: [], **kwargs)
    documents = []
    each_document(yaml) do |doc|
      doc = __finish(doc, kwargs[:symbolize_names], kwargs[:freeze])
      block_given? ? yield(doc) : documents << doc
    end
    return nil if block_given?
    documents.empty? ? fallback : documents
  end

  def self.parse(yaml, filename: nil)
    node = __parse_node(yaml, filename)
    node ? Nodes.__document(node) : false
  end

  def self.parse_file(filename, fallback: false)
    File.open(filename) { |f| parse(f, filename: filename) || fallback }
  end

  def self.parse_stream(yaml, filename: nil, &block)
    stream = Nodes::Stream.new
    parse_documents(yaml) do |node|
      doc = Nodes.__document(node)
      block ? block.call(doc) : stream.children << doc
    end
    block ? nil : stream
  rescue Psych::SyntaxError => e
    raise if filename.nil?
    raise Psych::SyntaxError, e.message.sub(/\A\(<unknown>\)/) { "(#{filename})" }
  end

  def self.__parse_node(yaml, filename) # :nodoc:
    __engine_parse(yaml)
  rescue Psych::SyntaxError => e
    raise if filename.nil?
    raise Psych::SyntaxError, e.message.sub(/\A\(<unknown>\)/) { "(#{filename})" }
  end

  def self.__finish(result, symbolize_names, freeze) # :nodoc:
    result = __symbolize_names(result) if symbolize_names
    result = __deep_freeze(result) if freeze
    result
  end

  def self.__symbolize_names(obj) # :nodoc:
    case obj
    when ::Hash
      obj.each_with_object({}) { |(k, v), h| h[k.respond_to?(:to_sym) ? k.to_sym : k] = __symbolize_names(v) }
    when ::Array
      obj.map { |v| __symbolize_names(v) }
    else
      obj
    end
  end

  def self.__deep_freeze(obj) # :nodoc:
    case obj
    when ::Hash
      obj.each { |k, v| __deep_freeze(k); __deep_freeze(v) }
    when ::Array
      obj.each { |v| __deep_freeze(v) }
    when ::String
      return -obj
    end
    obj.freeze
  end

  RUBY_TAG = 'tag:ruby.yaml.org,2002:' # :nodoc:
  YAML_TAG = 'tag:yaml.org,2002:' # :nodoc:

  # Walks the composed document the way Psych's restricted class loader would see it and
  # raises DisallowedClass for the first class that is not permitted, and AliasesNotEnabled
  # for a node reached twice (the engine resolves an alias to the anchored node itself).
  def self.__check_safe(root, permitted_classes, permitted_symbols, aliases) # :nodoc:
    classes = permitted_classes.map { |c| c.is_a?(::Module) ? c.name : c.to_s }
    symbols = permitted_symbols.map(&:to_s)
    seen = {}.compare_by_identity
    check = lambda do |node|
      if seen.key?(node)
        raise AliasesNotEnabled unless aliases
        next
      end
      seen[node] = true
      __classes_for(node).each do |name, symbol|
        raise DisallowedClass.new('load', name) unless classes.include?(name)
        if symbol && !symbols.empty? && !symbols.include?(symbol)
          raise DisallowedClass.new('load', 'Symbol')
        end
      end
      node.children.each(&check) if node.respond_to?(:children)
    end
    check.call(root)
  end

  # The classes (with the symbol name, for a Symbol) that building +node+ needs beyond the
  # ones every load may build: nil, true, false, Integer, Float, String, Array and Hash.
  def self.__classes_for(node) # :nodoc:
    tag = node.tag
    if tag.nil? || tag == YAML_TAG + 'str'
      if node.is_a?(Syck::Scalar) && node.style == Nodes::Scalar::PLAIN && node.value =~ /\A:./
        return [['Symbol', node.value.sub(/\A:/, '').sub(/\A(["'])(.*)\1\z/, '\2')]]
      end
      return []
    end
    if tag.start_with?(RUBY_TAG)
      kind, name = tag[RUBY_TAG.length..-1].split(':', 2)
      case kind
      when 'sym', 'symbol' then [['Symbol', node.value]]
      when 'regexp' then [['Regexp']]
      when 'range' then [['Range']]
      when 'class', 'module' then [[node.value]]
      when 'object' then [[name || 'Object']]
      when 'exception' then [[name || 'Exception']]
      when 'struct' then [[name || 'Struct']]
      when 'string', 'array', 'hash' then name ? [[name]] : []
      else []
      end
    else
      case tag
      when YAML_TAG + 'timestamp' then [['Time']]
      when YAML_TAG + 'timestamp#ymd' then [['Date']]
      else []
      end
    end
  end

  class << self
    private :__parse_node, :__finish, :__symbolize_names, :__deep_freeze, :__check_safe, :__classes_for
  end

  # A document tree, as Psych.parse answers it. Each node remembers the engine node it was
  # built from, which is what #to_ruby constructs.
  module Nodes
    class Node
      include Enumerable

      attr_reader :children
      attr_accessor :tag, :start_line, :start_column, :end_line, :end_column

      def initialize
        @children = []
      end

      def each(&block)
        return enum_for(:each) unless block
        yield self
        children.each { |child| child.each(&block) } if children
      end

      def to_ruby(symbolize_names: false, freeze: false, strict_integer: false)
        # A tree the engine built carries its own anchor resolution; a tree
        # built by hand or by Psych::TreeBuilder does not, so an anchor table is
        # kept for the length of the outermost conversion and Alias reads from
        # it.  The nested to_ruby calls a collection makes see the same table.
        outermost = Thread.current[:__psych_anchors__].nil?
        Thread.current[:__psych_anchors__] = {} if outermost
        begin
          Psych.__send__(:__finish, __build, symbolize_names, freeze)
        ensure
          Thread.current[:__psych_anchors__] = nil if outermost
        end
      end
      alias transform to_ruby

      def __anchor_table # :nodoc:
        Thread.current[:__psych_anchors__] ||= {}
      end

      def __anchored(value) # :nodoc:
        __anchor_table[anchor] = value if respond_to?(:anchor) && anchor
        value
      end

      # The YAML this tree stands for, written by Psych::Visitors::Emitter rather than by
      # dumping what #to_ruby answers, so a node's tag, anchor and quoting style survive.
      # Like upstream's, this only works on a whole stream: a bare document or scalar has no
      # stream start in front of it, and the emitter refuses it exactly as libyaml does.
      def yaml(io = nil, options = {})
        real_io = io || StringIO.new(''.dup.force_encoding(Encoding::UTF_8))
        Visitors::Emitter.new(real_io, options).accept self
        return real_io.string unless io
        io
      end
      alias to_yaml yaml

      def alias?;    false; end
      def document?; false; end
      def mapping?;  false; end
      def scalar?;   false; end
      def sequence?; false; end
      def stream?;   false; end

      def __build # :nodoc:
        @__engine_node ? @__engine_node.transform : __build_detached
      end
    end

    class Scalar < Node
      ANY = 0
      PLAIN = 1
      SINGLE_QUOTED = 2
      DOUBLE_QUOTED = 3
      LITERAL = 4
      FOLDED = 5

      attr_accessor :value, :anchor, :plain, :quoted, :style

      def initialize(value, anchor = nil, tag = nil, plain = true, quoted = false, style = ANY)
        @value = value
        @anchor = anchor
        @tag = tag
        @plain = plain
        @quoted = quoted
        @style = style
        @children = nil
      end

      def scalar?; true; end

      def __build_detached # :nodoc:
        __anchored(quoted || tag ? value : Psych.unsafe_load(value))
      end
    end

    class Sequence < Node
      ANY = 0
      BLOCK = 1
      FLOW = 2

      attr_accessor :anchor, :implicit, :style

      def initialize(anchor = nil, tag = nil, implicit = true, style = BLOCK)
        super()
        @anchor = anchor
        @tag = tag
        @implicit = implicit
        @style = style
      end

      def sequence?; true; end

      def __build_detached # :nodoc:
        # The list is registered under its anchor before its children are built,
        # so that a sequence containing an alias to itself terminates.
        list = __anchored([])
        children.each { |child| list << child.to_ruby }
        list
      end
    end

    class Mapping < Node
      ANY = 0
      BLOCK = 1
      FLOW = 2

      attr_accessor :anchor, :implicit, :style

      def initialize(anchor = nil, tag = nil, implicit = true, style = BLOCK)
        super()
        @anchor = anchor
        @tag = tag
        @implicit = implicit
        @style = style
      end

      def mapping?; true; end

      def __build_detached # :nodoc:
        hash = __anchored({})
        children.each_slice(2) { |k, v| hash[k.to_ruby] = v.to_ruby }
        hash
      end
    end

    class Alias < Node
      attr_accessor :anchor

      def initialize(anchor)
        @anchor = anchor
        @children = nil
      end

      def alias?; true; end

      def __build_detached # :nodoc:
        table = __anchor_table
        raise Psych::AnchorNotDefined, anchor unless table.key?(anchor)

        table[anchor]
      end
    end

    class Document < Node
      attr_accessor :version, :tag_directives, :implicit, :implicit_end

      def initialize(version = [], tag_directives = [], implicit = false)
        super()
        @version = version
        @tag_directives = tag_directives
        @implicit = implicit
        @implicit_end = true
      end

      def root
        children.first
      end

      def document?; true; end

      def __build # :nodoc:
        root.to_ruby
      end
    end

    class Stream < Node
      ANY = 0
      UTF8 = 1
      UTF16LE = 2
      UTF16BE = 3

      attr_accessor :encoding

      def initialize(encoding = UTF8)
        super()
        @encoding = encoding
      end

      def stream?; true; end

      def __build # :nodoc:
        children.map(&:to_ruby)
      end
    end

    # Wraps an engine node (and what hangs off it) in Psych nodes. A node reached a second
    # time was an alias: it becomes an Alias of an anchor put on the first occurrence (the
    # engine does not keep the anchor's name).
    def self.__document(engine_node) # :nodoc:
      counts = {}.compare_by_identity
      count = lambda do |n|
        counts[n] = (counts[n] || 0) + 1
        n.children.each(&count) if counts[n] == 1 && n.respond_to?(:children)
      end
      count.call(engine_node)

      anchors = {}.compare_by_identity
      wrap = lambda do |n|
        if (anchor = anchors[n])
          node = Alias.new(anchor)
        else
          anchor = anchors[n] = (anchors.size + 1).to_s if counts[n] > 1
          tag = n.tag
          if n.is_a?(Psych::Syck::Scalar)
            style = n.style
            quoted = style != Scalar::PLAIN
            node = Scalar.new(n.value, anchor, __explicit_tag(tag, n), !quoted, quoted, style)
          elsif n.is_a?(Psych::Syck::Seq)
            node = Sequence.new(anchor, __explicit_tag(tag, n), true, n.flow? ? Sequence::FLOW : Sequence::BLOCK)
            n.children.each { |c| node.children << wrap.call(c) }
          else
            node = Mapping.new(anchor, __explicit_tag(tag, n), true, n.flow? ? Mapping::FLOW : Mapping::BLOCK)
            n.children.each { |c| node.children << wrap.call(c) }
          end
        end
        node.instance_variable_set(:@__engine_node, n)
        node
      end

      document = Document.new([], [], true)
      document.children << wrap.call(engine_node)
      document.instance_variable_set(:@__engine_node, engine_node)
      document
    end

    # Psych reports the tag only when the document spells one out; the engine resolves plain
    # scalars and untagged collections to the core schema tags.
    def self.__explicit_tag(tag, node) # :nodoc:
      return nil if tag.nil?
      return nil if tag.start_with?(Psych::YAML_TAG)
      tag.sub(/\Atag:ruby\.yaml\.org,2002:/, '!ruby/')
    end

    class << self
      private :__explicit_tag
    end
  end
end

module Psych
  ##
  # The event interface Psych::Parser calls.  Every method is a no-op here; a
  # subclass overrides the events it cares about.  This is the same contract as
  # upstream psych's Psych::Handler, and Psych::TreeBuilder below is its main
  # implementation.
  class Handler
    # What Psych::Visitors::Emitter hands an emitter when the caller asked for non-default
    # formatting.
    class DumperOptions
      attr_accessor :line_width, :indentation, :canonical

      def initialize
        @line_width = 0
        @indentation = 2
        @canonical = false
      end
    end

    EVENTS = [
      :alias, :empty, :end_document, :end_mapping, :end_sequence, :end_stream,
      :scalar, :start_document, :start_mapping, :start_sequence, :start_stream,
    ].freeze

    def alias(anchor); end
    def empty; end
    def end_document(implicit = true); end
    def end_mapping; end
    def end_sequence; end
    def end_stream; end
    def scalar(value, anchor, tag, plain, quoted, style); end
    def start_document(version, tag_directives, implicit); end
    def start_mapping(anchor, tag, implicit, style); end
    def start_sequence(anchor, tag, implicit, style); end
    def start_stream(encoding); end

    # Called before each event with the source range it came from.  IronRuby's
    # YAML engine does not report source positions, so Psych::Parser passes nil
    # for all four - the same value Psych::Nodes already carry here.
    def event_location(start_line, start_column, end_line, end_column); end

    # Whether the handler is being fed a stream of documents rather than one.
    def streaming?
      false
    end
  end

  ##
  # Builds a Psych::Nodes tree from parser events.  Vendored from upstream psych
  # unchanged apart from the comments: it is pure Ruby, and every node class it
  # names is the one defined above.
  #
  #   parser = Psych::Parser.new Psych::TreeBuilder.new
  #   parser.parse('--- foo')
  #   tree = parser.handler.root
  class TreeBuilder < Psych::Handler
    attr_reader :root

    def initialize
      @stack = []
      @last  = nil
      @root  = nil

      @start_line   = nil
      @start_column = nil
      @end_line     = nil
      @end_column   = nil
    end

    def event_location(start_line, start_column, end_line, end_column)
      @start_line   = start_line
      @start_column = start_column
      @end_line     = end_line
      @end_column   = end_column
    end

    def start_sequence(anchor, tag, implicit, style)
      n = Nodes::Sequence.new(anchor, tag, implicit, style)
      set_start_location(n)
      @last.children << n
      push n
    end

    def end_sequence
      n = pop
      set_end_location(n)
      n
    end

    def start_mapping(anchor, tag, implicit, style)
      n = Nodes::Mapping.new(anchor, tag, implicit, style)
      set_start_location(n)
      @last.children << n
      push n
    end

    def end_mapping
      n = pop
      set_end_location(n)
      n
    end

    def start_document(version, tag_directives, implicit)
      n = Nodes::Document.new version, tag_directives, implicit
      set_start_location(n)
      @last.children << n
      push n
    end

    def end_document(implicit_end = !streaming?)
      @last.implicit_end = implicit_end
      n = pop
      set_end_location(n)
      n
    end

    def start_stream(encoding)
      @root = Nodes::Stream.new(encoding)
      set_start_location(@root)
      push @root
    end

    def end_stream
      n = pop
      set_end_location(n)
      n
    end

    def scalar(value, anchor, tag, plain, quoted, style)
      s = Nodes::Scalar.new(value, anchor, tag, plain, quoted, style)
      set_location(s)
      @last.children << s
      s
    end

    def alias(anchor)
      a = Nodes::Alias.new(anchor)
      set_location(a)
      @last.children << a
      a
    end

    private

    def push(value)
      @stack.push value
      @last = value
    end

    def pop
      x = @stack.pop
      @last = @stack.last
      x
    end

    def set_location(node)
      set_start_location(node)
      set_end_location(node)
    end

    def set_start_location(node)
      node.start_line   = @start_line
      node.start_column = @start_column
    end

    def set_end_location(node)
      node.end_line   = @end_line
      node.end_column = @end_column
    end
  end

  # This name was taken by the Syck-era representer the YAML engine registers; that class is
  # still Psych::Syck::Emitter, which is what it always was.
  remove_const :Emitter if const_defined?(:Emitter, false)

  ##
  # Writes the YAML text for the events it is handed - the other end of Parser, and what
  # Psych::Visitors::Emitter drives.  Upstream this is libyaml writing event by event; here
  # the events are collected and the YAML engine's emitter (the same algorithm) writes them
  # out when the stream ends, so the io only fills up at #end_stream.
  class Emitter < Psych::Handler
    attr_accessor :line_width, :indentation, :canonical

    def initialize(io, options = nil)
      @io = io
      @events = []
      @line_width = options ? options.line_width : 0
      @indentation = options ? options.indentation : 2
      @canonical = options ? options.canonical : false
    end

    def start_stream(encoding)
      @events << [0]
      self
    end

    # Every other event needs the stream open first, which upstream's emitter says with this
    # message rather than writing a document that has no stream around it.
    def __event(event) # :nodoc:
      raise 'expected STREAM-START' if @events.empty?
      @events << event
      self
    end
    private :__event

    def end_stream
      raise 'expected STREAM-START' if @events.empty?
      @events << [1]
      events, @events = @events, []
      @io.write Psych.__emit_events(events, @indentation, @line_width, @canonical)
      @io
    end

    def start_document(version, tag_directives, implicit)
      __event [2, version, implicit]
    end

    # +implicit+ here is Psych's implicit *end*: a document that does not write "...".
    def end_document(implicit = false)
      __event [3, !implicit]
    end

    def alias(anchor)
      __event [4, anchor]
    end

    def scalar(value, anchor, tag, plain, quoted, style)
      __event [5, value, anchor, tag, plain, quoted, style]
    end

    def start_sequence(anchor, tag, implicit, style)
      __event [6, anchor, tag, implicit, style]
    end

    def end_sequence
      __event [7]
    end

    def start_mapping(anchor, tag, implicit, style)
      __event [8, anchor, tag, implicit, style]
    end

    def end_mapping
      __event [9]
    end
  end

  ##
  # The event-driven half of Psych's API: parse a document and call methods on a
  # handler for each YAML event.  RuboCop's duplicate-key checker, Psych's own
  # JSON handlers and anything that wants to see a document without materialising
  # Ruby objects go through this.
  #
  # Upstream this drives libyaml's own event parser.  IronRuby's YAML engine
  # parses to a node tree instead of emitting events, so the events are replayed
  # from that tree in document order.  Every event a well-formed document
  # produces is generated, and with the same arguments, because Psych::Nodes
  # records exactly what the event carried - anchor, tag, style, implicit.  What
  # is *not* reproduced is source position: the engine does not record where a
  # node started, so event_location reports nil, which is already what
  # Psych::Nodes#start_line answers here.
  class Parser
    class Mark < Struct.new(:index, :line, :column)
    end

    ANY = Nodes::Stream::ANY
    UTF8 = Nodes::Stream::UTF8
    UTF16LE = Nodes::Stream::UTF16LE
    UTF16BE = Nodes::Stream::UTF16BE

    attr_accessor :handler
    attr_writer :external_encoding

    def initialize(handler = Handler.new)
      @handler = handler
      @external_encoding = ANY
      @mark = Mark.new(0, 0, 0)
    end

    # The position the parser has reached.  Nothing here advances it, for the
    # reason given above; it answers a Mark rather than nil so that a handler
    # that reads it works.
    def mark
      @mark
    end

    def parse(yaml, path = (yaml.respond_to?(:path) ? yaml.path : "<unknown>"))
      stream = Psych.parse_stream(yaml, filename: path)
      @handler.start_stream(UTF8)
      stream.children.each { |document| __replay(document) } if stream
      @handler.end_stream
      self
    end

    private

    def __replay(node)
      @handler.event_location(node.start_line, node.start_column, node.end_line, node.end_column)

      case node
      when Nodes::Document
        @handler.start_document(node.version, node.tag_directives, node.implicit)
        node.children.each { |child| __replay(child) }
        @handler.event_location(node.start_line, node.start_column, node.end_line, node.end_column)
        @handler.end_document(node.implicit_end)
      when Nodes::Mapping
        @handler.start_mapping(node.anchor, node.tag, node.implicit, node.style)
        node.children.each { |child| __replay(child) }
        @handler.event_location(node.start_line, node.start_column, node.end_line, node.end_column)
        @handler.end_mapping
      when Nodes::Sequence
        @handler.start_sequence(node.anchor, node.tag, node.implicit, node.style)
        node.children.each { |child| __replay(child) }
        @handler.event_location(node.start_line, node.start_column, node.end_line, node.end_column)
        @handler.end_sequence
      when Nodes::Alias
        @handler.alias(node.anchor)
      when Nodes::Scalar
        @handler.scalar(node.value, node.anchor, node.tag, node.plain, node.quoted, node.style)
      end
    end
  end
end

# The pure-Ruby half of psych that IronRuby's engine does not replace: the
# node-to-Ruby visitor and what it needs.  Loaded here rather than autoloaded so
# that `require "psych"` answers the same constants CRuby's does.
require 'psych/class_loader'
require 'psych/scalar_scanner'
require 'psych/coder'
require 'psych/visitors/visitor'
require 'psych/visitors/to_ruby'
require 'psych/streaming'
require 'psych/visitors/yaml_tree'
require 'psych/visitors/emitter'
require 'psych/visitors/depth_first'

require 'yaml/types'
