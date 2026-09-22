# frozen_string_literal: true

module Nokogiri
  # The HTML4 parser.
  #
  # Nokogiri's HTML4 is libxml2's HTML parser, which predates the HTML5 parsing
  # specification and has its own recovery rules.  There is no libxml2 here, so
  # HTML4 parses with the same WHATWG algorithm HTML5 parses with, and differs
  # from it only where the difference is visible to the callers that matter: the
  # HTML4 serialization carries libxml2's doctype and drops an empty <head>, and
  # the contents of <style> and <script> are CDATA sections the way libxml2
  # reports them.  Markup on which the two parsers genuinely disagree - unclosed
  # tables, misnested formatting elements - is parsed the HTML5 way.
  module HTML4
    class Document < Nokogiri::XML::Document
      def self.document_kind
        :html4
      end
    end

    class DocumentFragment < Nokogiri::XML::DocumentFragment
      def self.document_class
        HTML4::Document
      end
    end

    class << self
      def parse(*args, &block)
        Document.parse(*args, &block)
      end

      def fragment(markup, encoding = nil)
        DocumentFragment.parse(markup)
      end

      def call(*args, &block)
        parse(*args, &block)
      end
    end
  end

  # The HTML5 parser: AngleSharp's implementation of the WHATWG tokenizer and
  # tree-construction algorithm, which is the algorithm gumbo implements for
  # nokogiri.
  module HTML5
    class Document < Nokogiri::XML::Document
      def self.document_kind
        :html5
      end

      def self.parse(string_or_io, url = nil, encoding = nil, **options, &block)
        super(string_or_io, url, encoding, nil, &block)
      end
    end

    class DocumentFragment < Nokogiri::XML::DocumentFragment
      def self.document_class
        HTML5::Document
      end

      def self.parse(tags, encoding = nil, **options)
        super(tags)
      end

      def initialize(document, tags = nil, ctx = nil, **options)
        super(document, tags, ctx)
      end
    end

    class << self
      def parse(string_or_io, url = nil, encoding = nil, **options, &block)
        Document.parse(string_or_io, url, encoding, **options, &block)
      end

      def fragment(markup, encoding = nil, **options)
        DocumentFragment.parse(markup, encoding, **options)
      end

      def get(uri, options = {})
        raise NotImplementedError, "Nokogiri::HTML5.get is not implemented on IronRuby"
      end
    end
  end

  # Nokogiri::HTML has always been the HTML4 parser, and Loofah aliases its own
  # HTML to HTML4 to match.
  HTML = HTML4

  class << self
    def HTML4(*args, &block)
      HTML4::Document.parse(*args, &block)
    end

    def HTML5(input, url = nil, encoding = nil, **options, &block)
      HTML5::Document.parse(input, url, encoding, **options, &block)
    end

    def HTML(*args, &block)
      HTML4::Document.parse(*args, &block)
    end

    def XML(*args, &block)
      XML::Document.parse(*args, &block)
    end

    def parse(string, url = nil, encoding = nil, options = nil)
      if string =~ /^\s*<[^Hh>]*html/ || string !~ /^\s*</
        HTML4::Document.parse(string, url, encoding, options)
      else
        XML::Document.parse(string, url, encoding, options)
      end
    end

    def make(input = nil, opts = {}, &block)
      raise NotImplementedError, "Nokogiri.make (the Builder API) is not implemented on IronRuby"
    end
  end
end
