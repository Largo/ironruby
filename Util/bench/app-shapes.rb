# Ordinary Ruby, ns per unit of work, to put next to CRuby: the code the standard library and
# application programs are made of (`||=`, constants, keyword arguments, string building, small
# objects, Enumerable chains with blocks, `super`, `public_send`), which the Are-We-Fast-Yet
# kernels and Util/perf/micro.rb do not contain. A change that lets the JIT or the light frames
# take more of ordinary Ruby shows here or nowhere; Util/perf/census.rb -u on a trace of this
# file says which of the methods it called the JIT took.
#
#   IR_CONFIG=Release ./ir.sh Util/bench/app-shapes.rb
#   IR_CONFIG=Release ./ir.sh -X:NoJIT -X:NoOSR Util/bench/app-shapes.rb
#   ruby Util/bench/app-shapes.rb
#   ... app-shapes.rb erb_result invoice_build   # only these (one process each is steadiest)
#   ... app-shapes.rb --list                     # the names
#   ruby Util/perf/ab.rb Util/bench/app-shapes.rb cruby=ruby "base=..." "new=..." -- invoice_build   # two builds, taking turns
#
# Two groups: the standard library's pure-Ruby code driven the way a program drives it (gem_*,
# optparse, erb_*, uri, set, csv_*, pp, shellwords, time, forwardable, delegate, ostruct, strscan,
# json_*, prism_walk), and four small programs written below the way application code is written,
# not the way a benchmark is: an order and invoice model (invoice_*), a template renderer
# (render), a rule engine of lambdas and public_send (rules), and a query over an Array of Hashes
# (query_*). A unit is one piece of work such a program does (parse one argv, render one page,
# price a batch of orders); each shape has its own count, chosen so that a section takes about
# 0.3 s on IronRuby, and N (per mille, default 1000) scales them all: N=100 runs a tenth of each.
#
# Every line prints a checksum of what the work produced next to its time; it must be CRuby's.
# Left out: rdoc's markup to HTML (requiring rdoc/markup/to_html takes more than a second on
# IronRuby, more than a section).
$stdout.sync = true
require 'zlib'
require 'optparse'
require 'erb'
require 'uri'
require 'set'
require 'csv'
require 'pp'
require 'shellwords'
require 'time'
require 'forwardable'
require 'delegate'
require 'ostruct'
require 'strscan'
require 'json'
require 'prism'

def crc(s) = Zlib.crc32(s)

# --- the standard library ----------------------------------------------------------------------

module Lib
  VERSIONS = %w[1.0.0 1.2.3 1.2.10 2.0.0.pre1 2.0.0 0.9.12 3.1.4 1.2.3.a 10.0 1.10.1].freeze
  # a lockfile's worth: the same versions with the last part moved, so that no two are the same object
  MANY = (0...8).flat_map { |k| VERSIONS.map { |v| v.sub(/\d+\z/) { |d| (d.to_i + k).to_s } } }.freeze

  def self.gem_version
    vs = MANY.map { |v| Gem::Version.new(v) }
    s = vs.sort.each_with_index.sum { |v, i| v.segments.size * (i + 1) }
    s += vs.count { |v| v.prerelease? }
    s + vs.sum { |v| crc(v.bump.to_s) & 0xff } + vs.sum { |v| crc(v.release.to_s) & 0xff }
  end

  REQ = Gem::Requirement.new("~> 1.2", ">= 1.2.3")
  DEP = Gem::Dependency.new("widget", "~> 2.0")
  def self.gem_requirement
    s = 0
    MANY.first(30).each do |v|
      gv = Gem::Version.new(v)
      s += 1 if REQ.satisfied_by?(gv)
      s += 2 if Gem::Requirement.new(">= #{v}").satisfied_by?(Gem::Version.new("1.5"))
      s += 4 if DEP.match?("widget", v)
    end
    s
  end

  def self.gem_spec = (1..8).sum { |k| gem_spec1(k) }
  def self.gem_spec1(k)
    spec = Gem::Specification.new do |g|
      g.name = "widget#{k}"
      g.version = "1.4.#{k}"
      g.summary = "A widget for examples"
      g.authors = ["A. Writer", "B. Coder"]
      g.email = "dev@example.com"
      g.homepage = "https://example.com/widget"
      g.license = "MIT"
      g.files = %w[lib/widget.rb lib/widget/version.rb README.md]
      g.required_ruby_version = ">= 3.1"
      g.add_dependency "gadget", "~> 2.1"
      g.add_development_dependency "rake", ">= 13"
      g.metadata = { "source_code_uri" => "https://example.com/widget/src" }
    end
    s = crc(spec.full_name) + spec.files.size + spec.dependencies.size + spec.runtime_dependencies.size
    s += crc(spec.required_ruby_version.to_s) + spec.authors.sum(&:size) + crc(spec.homepage)
    s + spec.dependencies.sum { |d| crc(d.requirement.to_s) & 0xffff }
  end

  ARGV1 = %w[-v --name=widget -o out.txt --count 12 -q --level debug --tag a --tag b
             --ratio 0.25 --no-color --dry-run -j 4 input1 input2].freeze
  def self.optparse
    opts = { tags: [] }
    parser = OptionParser.new do |o|
      o.banner = "usage: widget [options] FILE..."
      o.on("-v", "--verbose", "say more") { opts[:verbose] = true }
      o.on("-q", "--quiet", "say less") { opts[:quiet] = true }
      o.on("-n", "--name NAME", String, "the name") { |v| opts[:name] = v }
      o.on("-o", "--output FILE", "where to write") { |v| opts[:output] = v }
      o.on("-c", "--count N", Integer, "how many") { |v| opts[:count] = v }
      o.on("-l", "--level LEVEL", %w[debug info warn], "log level") { |v| opts[:level] = v }
      o.on("-t", "--tag TAG", "a tag, again for more") { |v| opts[:tags] << v }
      o.on("-r", "--ratio R", Float, "a ratio") { |v| opts[:ratio] = v }
      o.on("--[no-]color", "colour the output") { |v| opts[:color] = v }
      o.on("--dry-run", "do nothing") { opts[:dry] = true }
      o.on("-j", "--jobs N", Integer, "parallel jobs") { |v| opts[:jobs] = v }
      o.on("-h", "--help", "this text") { opts[:help] = true }
    end
    rest = parser.parse(ARGV1)
    rest.size + opts.size + opts[:count] + opts[:jobs] + opts[:tags].size + (opts[:ratio] * 100).to_i + crc(opts[:name] + opts[:level]) & 0xffffff
  end

  PAGE = <<~'EOS'
    <h1><%= title %></h1>
    <ul>
    <% items.each do |item| -%>
      <li class="<%= item[:done] ? 'done' : 'open' %>"><%= ERB::Util.html_escape(item[:text]) %> (<%= item[:n] %>)</li>
    <% end -%>
    </ul>
    <p>Total: <%= items.sum { |i| i[:n] } %></p>
  EOS
  ITEMS = (1..40).map { |i| { text: "Task <#{i}> & more", n: i * 3, done: i.even? } }.freeze
  PAGE_ERB = ERB.new(PAGE, trim_mode: '-')
  def self.erb_result = (1..4).sum { |k| crc(PAGE_ERB.result_with_hash(title: "Tasks #{k}", items: ITEMS)) }
  def self.erb_compile = (1..3).sum { |k| crc(ERB.new(PAGE * k, trim_mode: '-').src) }

  URLS = ["https://example.com/a/b/c?x=1&y=two&z=%20sp", "http://user@example.org:8080/path/to/page.html#frag",
          "https://example.com/search?q=ruby+on+dotnet&page=3&sort=desc", "ftp://files.example.net/pub/file.tar.gz"].freeze
  def self.uri
    s = 0
    URLS.each do |u|
      x = URI.parse(u)
      s += crc(x.to_s) & 0xffff
      s += x.port.to_i + x.path.size + x.host.size
      s += URI.decode_www_form(x.query).sum { |k, v| k.size + v.size } if x.query
      # URI::FTP#merge takes only a URI (CRuby's too), and URI.join hands it a String
      s += crc(URI.join(u, "../other/x.html?a=1").to_s) & 0xffff if x.is_a?(URI::HTTP)
    end
    s + crc(URI.encode_www_form([["a", "1 2"], ["b", "x&y"], ["c", "é"]]))
  end

  def self.set
    a = Set.new(1..200)
    b = Set.new((100..300).step(2))
    c = (a | b) - Set[1, 2, 3]
    d = a & b
    s = c.size + d.size + (a ^ b).size + (d.subset?(a) ? 1 : 0) + (a.superset?(d) ? 2 : 0)
    words = Set.new
    %w[apple pear apple plum fig pear kiwi fig].each { |w| words << w }
    s + words.size + c.select(&:even?).sum + d.map { |x| x * 2 }.sum + (a.disjoint?(Set[1000]) ? 1 : 0)
  end

  TABLE = (["id,name,price,qty,date"] + (1..20).map { |i| "#{i},item #{i},#{i * 1.25},#{i % 7},2026-0#{i % 9 + 1}-1#{i % 10}" }).join("\n") + "\n"
  def self.csv_parse
    rows = CSV.parse(TABLE, headers: true, converters: :numeric, header_converters: :symbol)
    rows.sum { |r| r[:id] + r[:qty] } + (rows.sum { |r| r[:price] } * 100).round + rows.map { |r| r[:date].size }.sum
  end
  def self.csv_generate
    crc(CSV.generate do |csv|
      csv << %w[id name note]
      100.times { |i| csv << [i, "name #{i}", i.even? ? "has, comma" : "has \"quote\""] }
    end)
  end

  NESTED = { name: "widget", tags: %w[a b c], dims: { w: 10, h: 20, d: [1, 2, 3] }, parts: (1..6).map { |i| { id: i, label: "part #{i}", ok: i.odd?, weight: i * 0.5 } } }.freeze
  def self.pp = crc(PP.pp(NESTED, +"", 60))

  CMDS = ["git commit -m 'a message with spaces' --author=\"A. Writer\"", "ls -la /tmp/some\\ dir", "echo \"x y\" 'z w' plain"].freeze
  def self.shellwords
    (CMDS * 4).sum { |c| w = Shellwords.split(c); w.size + crc(w.shelljoin) & 0xffff } + crc(Shellwords.escape("it's here"))
  end

  STAMPS = ["2026-03-14 15:09:26", "2025-12-31 23:59:59", "2024-02-29 00:00:01", "2001-09-09 01:46:40"].freeze
  def self.time
    (STAMPS * 2).sum do |st|
      t = Time.strptime(st + " UTC", "%Y-%m-%d %H:%M:%S %Z").utc
      iso = t.iso8601
      back = Time.iso8601(iso)
      back.to_i % 100_000 + crc(iso) % 1000 + crc(t.strftime("%a %d %b %Y %H:%M")) % 1000 + crc(Time.parse(st + " +0000").utc.httpdate) % 1000
    end
  end
end

class Playlist
  extend Forwardable
  def_delegators :@songs, :size, :<<, :first, :each, :map
  def_delegator :@meta, :fetch, :meta
  def initialize; @songs = []; @meta = { owner: "someone", public: true }; end
end
class LoudString < SimpleDelegator
  def upcase_size = __getobj__.upcase.size
end
module Lib
  def self.forwardable
    pl = Playlist.new
    200.times { |i| pl << i }
    pl.size + pl.first + pl.map { |x| x * 2 }.sum + (1..200).sum { |k| (pl.meta(:public) ? k : 0) + pl.meta(:owner).size + pl.size }
  end
  def self.delegate
    d = LoudString.new("delegated text")
    s = 0
    200.times { s += d.size + d.upcase_size + (d.start_with?("del") ? 1 : 0) + d.index("t") }
    s
  end
  def self.ostruct
    s = 0
    10.times do |i|
      o = OpenStruct.new(name: "row #{i}", qty: i, price: i * 2)
      o.total = o.qty * o.price
      s += o.total + o.name.size + o.to_h.size + (o.respond_to?(:total) ? 1 : 0) + o[:qty]
    end
    s
  end

  SOURCE = (<<~'EOS' * 20).freeze
    let total = price * (qty + 3) - discount; # a comment here
    if total >= 100 { emit("big", total / 2.5); } else { emit('small', total); }
  EOS
  def self.strscan
    ss = StringScanner.new(SOURCE)
    counts = Hash.new(0)
    until ss.eos?
      if ss.skip(/\s+/) then next
      elsif ss.skip(/#[^\n]*/) then counts[:comment] += 1
      elsif ss.scan(/\d+(?:\.\d+)?/) then counts[:number] += ss.matched.size
      elsif ss.scan(/[A-Za-z_]\w*/) then counts[%w[let if else].include?(ss.matched) ? :keyword : :ident] += 1
      elsif ss.scan(/"[^"]*"|'[^']*'/) then counts[:string] += ss.matched.size
      elsif ss.scan(/>=|[-+*\/=();{}<>,]/) then counts[:punct] += 1
      else ss.getch; counts[:other] += 1
      end
    end
    counts.sum { |k, v| v * (k.size + 1) }
  end

  DOC = { "id" => 42, "name" => "widget \"pro\"", "price" => 19.5, "tags" => %w[x y z], "ok" => true, "none" => nil,
          "items" => (1..100).map { |i| { "n" => i, "label" => "line #{i}", "parts" => [i, i * 2, { "deep" => "é#{i}" }] } } }.freeze
  DOC_JSON = JSON.generate(DOC).freeze
  def self.json_generate = crc(JSON.generate(DOC)) + crc(JSON.pretty_generate(DOC)) + DOC["items"].sum { |i| JSON.generate(i).size }
  def self.json_parse
    d = JSON.parse(DOC_JSON)
    d["items"].sum { |i| i["n"] + i["parts"][2]["deep"].size } + d.size + JSON.parse(DOC_JSON, symbolize_names: true)[:tags].size
  end

  class Counter < Prism::Visitor
    attr_reader :counts
    def initialize; @counts = Hash.new(0); super(); end
    def visit_call_node(node); @counts[:call] += 1; @counts[:block] += 1 if node.block; super; end
    def visit_def_node(node); @counts[:def] += 1; super; end
    def visit_local_variable_read_node(node); @counts[:lvar] += 1; super; end
    def visit_instance_variable_read_node(node); @counts[:ivar] += 1; super; end
    def visit_string_node(node); @counts[:string] += node.unescaped.size; super; end
    def visit_integer_node(node); @counts[:int] += node.value; super; end
    def visit_if_node(node); @counts[:if] += 1; super; end
    def visit_class_node(node); @counts[:class] += 1; super; end
  end
  # what is timed is the walk: a hundred lines of ordinary Ruby (this file's first), parsed once
  TREE = Prism.parse(File.read(__FILE__, encoding: "UTF-8").lines.grep_v(/\A\s*#/).first(100).join).value
  def self.prism_walk
    c = Counter.new
    TREE.accept(c)
    c.counts.sum { |k, v| v * (k.size + 1) }
  end
end

# --- an order and invoice model ----------------------------------------------------------------

module Shop
  TAX_RATES = { standard: 0.2, reduced: 0.05, zero: 0.0 }.freeze
  STATES = %i[draft placed paid shipped cancelled].freeze
  FREE_SHIPPING_OVER = 50

  Address = Struct.new(:street, :city, :country, keyword_init: true) do
    def domestic? = country == "XX"
  end
  Money = Data.define(:cents) do
    include Comparable
    def +(other) = with(cents: cents + other.cents)
    def *(n) = with(cents: (cents * n).round)
    def <=>(other) = cents <=> other.cents
    def to_s = format("%d.%02d", cents / 100, cents % 100)
  end

  module Auditable
    def audit_log = (@audit_log ||= [])
    def record(event, **details) = audit_log << { event: event, at: audit_log.size, **details }
  end

  class Product
    attr_reader :sku, :name, :price, :tax
    def initialize(sku:, name:, price:, tax: :standard)
      raise ArgumentError, "unknown tax #{tax}" unless TAX_RATES.key?(tax)
      @sku, @name, @price, @tax = sku.freeze, name, Money.new(price), tax
    end
    def tax_rate = TAX_RATES.fetch(tax)
  end

  class Line
    attr_accessor :product, :qty, :discount
    def initialize(product, qty: 1, discount: 0)
      @product, @qty, @discount = product, qty, discount
    end
    def subtotal = (@subtotal ||= product.price * (qty * (1 - discount / 100.0)))
    def tax = (@tax ||= subtotal * product.tax_rate)
    def total = subtotal + tax
  end

  class Document
    include Auditable
    attr_reader :number, :lines
    def initialize(number, lines: [])
      @number = number
      @lines = lines
    end
    def describe = "#{self.class.name.split('::').last} #{number}"
    def total = lines.sum(Money.new(0)) { |l| l.total }
  end

  class Order < Document
    attr_reader :state, :address
    def initialize(number, address:, lines: [], state: :draft)
      super(number, lines: lines)
      raise ArgumentError, "bad state #{state}" unless STATES.include?(state)
      @address = address
      @state = state
    end
    def add(product, qty: 1, discount: 0)
      @total = nil
      lines << Line.new(product, qty: qty, discount: discount)
      record(:added, sku: product.sku, qty: qty)
      self
    end
    def shipping = (@shipping ||= total_before_shipping.cents >= FREE_SHIPPING_OVER * 100 ? Money.new(0) : Money.new(address.domestic? ? 495 : 1295))
    def total_before_shipping = (@total ||= super_total)
    def total = total_before_shipping + shipping
    def place!
      raise ArgumentError, "empty order" if lines.empty?
      @state = :placed
      record(:placed)
      self
    end
    def describe = "#{super} (#{state})"

    private

    def super_total = lines.sum(Money.new(0), &:total)
  end

  class Invoice < Document
    def self.for(order)
      inv = new("INV-#{order.number}", lines: order.lines.map(&:dup))
      inv.instance_variable_set(:@order, order)
      inv
    end
    def by_tax = lines.group_by { |l| l.product.tax }.transform_values { |ls| ls.sum(Money.new(0), &:tax) }
    def render
      out = +"#{describe}\n"
      lines.sort_by { |l| [-l.total.cents, l.product.sku] }.each do |l|
        out << format("%-8s %-20s %3d x %8s = %9s\n", l.product.sku, l.product.name, l.qty, l.product.price, l.total)
      end
      by_tax.each { |rate, amount| out << "  tax #{rate}: #{amount}\n" unless amount.cents.zero? }
      out << "  shipping: #{@order.shipping}\n  total: #{total + @order.shipping}\n"
    end
  end

  CATALOG = [
    Product.new(sku: "A-100", name: "Notebook", price: 450),
    Product.new(sku: "A-200", name: "Pen set", price: 1299),
    Product.new(sku: "B-300", name: "Recipe book", price: 2450, tax: :reduced),
    Product.new(sku: "C-400", name: "Gift card", price: 2500, tax: :zero),
    Product.new(sku: "A-500", name: "Desk lamp", price: 3999),
    Product.new(sku: "B-600", name: "Map", price: 899, tax: :reduced),
  ].freeze
  HOME = Address.new(street: "1 Example Road", city: "Sampleton", country: "XX").freeze
  ABROAD = Address.new(street: "2 Example Street", city: "Elsewhere", country: "YY").freeze

  def self.classify(x)
    case x
    when Order then 3
    when Document then 2
    when Money, Product then 1
    else x.respond_to?(:cents) ? 4 : 0
    end
  end

  # the hot entry point: build and price a batch of orders
  def self.invoice_build
    orders = (1..8).map do |i|
      o = Order.new(1000 + i, address: i.odd? ? HOME : ABROAD)
      CATALOG.each_with_index { |p, j| o.add(p, qty: (i + j) % 4 + 1, discount: j.even? ? 0 : 10) if (i * j) % 3 != 1 }
      o.place!
    end
    by_state = orders.group_by(&:state).transform_values(&:size)
    s = orders.sum { |o| o.total.cents } + by_state.fetch(:placed, 0) + by_state.fetch(:paid, 0)
    s += orders.sum { |o| o.audit_log.size + classify(o) + classify(o.total) }
    s + orders.select { |o| o.total > Money.new(10_000) }.size + orders.max_by(&:total).number
  end

  ORDERS = (1..4).map { |k| CATALOG.each_with_index.inject(Order.new(76 + k, address: k.odd? ? HOME : ABROAD)) { |o, (p, j)| o.add(p, qty: j + k, discount: j * 5) }.place! }.freeze
  def self.invoice_render = ORDERS.sum { |o| crc(Invoice.for(o).render) }
end

# --- a template renderer -----------------------------------------------------------------------

module Markup
  ESCAPES = { "&" => "&amp;", "<" => "&lt;", ">" => "&gt;", '"' => "&quot;" }.freeze
  TEMPLATE = <<~'EOS'.freeze
    # {{title}}

    Welcome back, **{{user.name}}**! You have {{count}} new _messages_ from {{user.team}}.

    * first item with `code` and a [link](https://example.com/one)
    * second item: **bold** & <angle> "quotes"
    * third item for {{user.name}}

    ## Recent

    {{#each recent}}- {{.}} {{/each}}

    Plain paragraph that goes on for a while, with *emphasis*, some **strong text**, and
    a second line; nothing in it is special except the {{count}} at the end.
  EOS

  def self.escape(s) = s.gsub(/[&<>"]/, ESCAPES)

  def self.lookup(ctx, path) = path.split(".").inject(ctx) { |o, k| o.is_a?(Hash) ? o.fetch(k.to_sym, "") : o }.to_s

  def self.expand(src, ctx)
    src = src.gsub(/\{\{#each (\w+)\}\}(.*?)\{\{\/each\}\}/m) do
      body = $2
      Array(ctx[$1.to_sym]).map { |v| body.gsub("{{.}}", v.to_s) }.join
    end
    src.gsub(/\{\{([\w.]+)\}\}/) { escape(lookup(ctx, $1)) }
  end

  def self.inline(text)
    text = escape(text)
    text = text.gsub(/\*\*(.+?)\*\*/) { "<strong>#{$1}</strong>" }
    text = text.gsub(/(?<![*\w])[*_](.+?)[*_](?![*\w])/) { "<em>#{$1}</em>" }
    text = text.gsub(/`([^`]+)`/) { "<code>#{$1}</code>" }
    text.gsub(/\[([^\]]+)\]\(([^)]+)\)/) { %(<a href="#{$2}">#{$1}</a>) }
  end

  def self.render(ctx)
    out = +""
    expand(TEMPLATE, ctx).split("\n\n").each do |block|
      lines = block.split("\n")
      case lines.first
      when /\A(#+) (.*)/
        out << "<h#{$1.size}>#{inline($2)}</h#{$1.size}>\n"
      when /\A[*-] /
        out << "<ul>\n"
        lines.each { |l| out << "  <li>" << inline(l.sub(/\A[*-] /, "")) << "</li>\n" }
        out << "</ul>\n"
      else
        out << "<p>" << lines.map { |l| inline(l.strip) }.join(" ") << "</p>\n"
      end
    end
    words = out.scan(/<(\w+)/).flatten.tally
    [out, words]
  end

  CTXS = [{ title: "Inbox <today>", count: 7, user: { name: "Sam Example", team: "R&D" }, recent: %w[alpha beta gamma delta] },
          { title: "Drafts", count: 0, user: { name: "Lee Sample", team: "Ops & Support" }, recent: %w[one] },
          { title: "Archive \"2025\"", count: 1234, user: { name: "Kim Placeholder" }, recent: [] }].freeze
  def self.run
    CTXS.sum { |ctx| out, words = render(ctx); crc(out) + words.sum { |k, v| k.size * v } }
  end
end

# --- a rule engine -----------------------------------------------------------------------------

module Rules
  Applicant = Struct.new(:age, :income, :country, :score, :flags)

  class Engine
    CHECKS = {
      adult: ->(a) { a.age >= 18 },
      income: ->(a) { a.income > 20_000 },
      region: ->(a) { %w[XX YY].include?(a.country) },
      score: ->(a) { a.score.between?(600, 850) },
      clean: ->(a) { (a.flags & %i[fraud default]).empty? },
    }.freeze
    WEIGHTS = { adult: 5, income: 3, region: 2, score: 4, clean: 6 }.freeze
    ACTIONS = { approve: :approve!, review: :review!, reject: :reject! }.freeze

    attr_reader :log
    def initialize(only: CHECKS.keys) = (@only = only; @log = Hash.new(0))
    def passed(app) = @only.select { |k| CHECKS.fetch(k).call(app) }
    def score(app) = passed(app).sum { |k| WEIGHTS.fetch(k, 0) }
    def decide(app)
      case score(app)
      when 18.. then :approve
      when 10...18 then :review
      else :reject
      end
    end
    def apply(app) = public_send(ACTIONS.fetch(decide(app)), app)
    def approve!(app) = (@log[:approve] += 1; app.score + 100)
    def review!(app) = (@log[:review] += 1; app.score)
    def reject!(app) = (@log[:reject] += 1; -app.flags.size)
  end

  APPLICANTS = (0...120).map do |i|
    Applicant.new(16 + i % 30, 15_000 + i * 900, %w[XX YY ZZ][i % 3], 550 + (i * 37) % 320, i % 7 == 0 ? %i[fraud] : i % 5 == 0 ? %i[late] : [])
  end.freeze
  def self.run
    e = Engine.new
    s = APPLICANTS.sum { |a| e.apply(a) }
    s + e.log.sum { |k, v| k.size * v } + Engine.new(only: %i[adult score]).then { |e2| APPLICANTS.count { |a| e2.decide(a) == :reject } }
  end
end

# --- a query over records ----------------------------------------------------------------------

module Query
  DEPTS = %w[sales support engineering finance].freeze
  PEOPLE = (1..200).map do |i|
    { id: i, name: "person #{i}", dept: DEPTS[i % 4], salary: 30_000 + (i * 7919) % 50_000, age: 20 + (i * 13) % 45,
      skills: %w[ruby sql excel go writing][0, 1 + i % 5], active: i % 9 != 0 }
  end.freeze

  def self.where(rows, **conds) = rows.select { |r| conds.all? { |k, v| v === r[k] } }
  def self.pluck(rows, *keys) = rows.map { |r| r.values_at(*keys) }

  def self.aggregate
    active = where(PEOPLE, active: true, age: 25..55)
    by_dept = active.group_by { |r| r[:dept] }.transform_values do |rs|
      { n: rs.size, avg: rs.sum { |r| r[:salary] } / rs.size, top: rs.max_by { |r| r[:salary] }[:id] }
    end
    skills = active.flat_map { |r| r[:skills] }.tally.sort_by { |k, v| [-v, k] }.first(3)
    s = by_dept.sum { |d, h| d.size + h[:n] + h[:avg] + h[:top] } + skills.sum { |k, v| k.size * v }
    s + pluck(active, :id, :age).each_with_object(Hash.new(0)) { |(id, age), h| h[age / 10] += id }.sum { |k, v| k * v }
  end

  def self.lazy
    PEOPLE.lazy.select { |r| r[:active] }.map { |r| r.slice(:id, :salary) }.reject { |r| r[:salary] < 40_000 }
          .each_slice(5).map { |sl| sl.sum { |r| r[:id] } }.first(12).sum +
      PEOPLE.each_with_index.filter_map { |r, i| i if r[:skills].include?("go") }.take(10).sum +
      PEOPLE.sort_by { |r| [r[:dept], -r[:salary]] }.first(5).sum { |r| r[:id] }
  end
end

# --- timing ------------------------------------------------------------------------------------

def cpu; File.read("/proc/thread-self/schedstat").to_i / 1e9; end
# INSTR=1 (Util/perf/ab.rb -i) adds a column: the instructions of a call, from the least of the
# seven sections as the time is. See Util/perf/instructions.rb.
INSTR = ENV["INSTR"] && (require_relative "../perf/instructions"; Instructions.open || warn("no instruction counter (perf_event_open)"))
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; fewest = nil; counted = !!INSTR; sum = nil
  7.times do
    c0 = INSTR && INSTR.read; t0 = cpu; sum = yield n; d = cpu - t0; c1 = INSTR && INSTR.read
    best = d if d < best
    # a read is nil once the counter is lost: then there is no count, rather than one of the sections that had it
    if c0 && c1 then c = c1 - c0; fewest = c if fewest.nil? || c < fewest else counted = false end
  end
  printf "%-16s %9.1f ns  %d%s\n", name, best * 1e9 / n, sum, counted ? format("  %.1f instr", fewest.to_f / n) : ""
end
# N is per mille of each shape's own count, so that one setting scales shapes whose units differ tenfold
N = (ENV["N"] || 1000).to_i
def n(count) = [count * N / 1000, 1].max

t("gem_version", n(1100))     { |n| i = 0; s = 0; while i < n; s += Lib.gem_version; i += 1; end; s }
t("gem_requirement", n(1000)) { |n| i = 0; s = 0; while i < n; s += Lib.gem_requirement; i += 1; end; s }
t("gem_spec", n(870))         { |n| i = 0; s = 0; while i < n; s += Lib.gem_spec; i += 1; end; s }
t("optparse", n(490))         { |n| i = 0; s = 0; while i < n; s += Lib.optparse; i += 1; end; s }
t("erb_result", n(40))        { |n| i = 0; s = 0; while i < n; s += Lib.erb_result; i += 1; end; s }
t("erb_compile", n(380))      { |n| i = 0; s = 0; while i < n; s += Lib.erb_compile; i += 1; end; s }
t("uri", n(160))              { |n| i = 0; s = 0; while i < n; s += Lib.uri; i += 1; end; s }
t("set", n(1300))             { |n| i = 0; s = 0; while i < n; s += Lib.set; i += 1; end; s }
t("csv_parse", n(240))        { |n| i = 0; s = 0; while i < n; s += Lib.csv_parse; i += 1; end; s }
t("csv_generate", n(660))     { |n| i = 0; s = 0; while i < n; s += Lib.csv_generate; i += 1; end; s }
t("pp", n(790))               { |n| i = 0; s = 0; while i < n; s += Lib.pp; i += 1; end; s }
t("shellwords", n(2700))      { |n| i = 0; s = 0; while i < n; s += Lib.shellwords; i += 1; end; s }
t("time", n(500))             { |n| i = 0; s = 0; while i < n; s += Lib.time; i += 1; end; s }
t("forwardable", n(1800))     { |n| i = 0; s = 0; while i < n; s += Lib.forwardable; i += 1; end; s }
t("delegate", n(620))         { |n| i = 0; s = 0; while i < n; s += Lib.delegate; i += 1; end; s }
t("ostruct", n(70))           { |n| i = 0; s = 0; while i < n; s += Lib.ostruct; i += 1; end; s }
t("strscan", n(550))          { |n| i = 0; s = 0; while i < n; s += Lib.strscan; i += 1; end; s }
t("json_generate", n(1400))   { |n| i = 0; s = 0; while i < n; s += Lib.json_generate; i += 1; end; s }
t("json_parse", n(1360))      { |n| i = 0; s = 0; while i < n; s += Lib.json_parse; i += 1; end; s }
t("prism_walk", n(670))       { |n| i = 0; s = 0; while i < n; s += Lib.prism_walk; i += 1; end; s }
t("invoice_build", n(390))    { |n| i = 0; s = 0; while i < n; s += Shop.invoice_build; i += 1; end; s }
t("invoice_render", n(480))   { |n| i = 0; s = 0; while i < n; s += Shop.invoice_render; i += 1; end; s }
t("render", n(1900))          { |n| i = 0; s = 0; while i < n; s += Markup.run; i += 1; end; s }
t("rules", n(770))            { |n| i = 0; s = 0; while i < n; s += Rules.run; i += 1; end; s }
t("query_aggregate", n(1400)) { |n| i = 0; s = 0; while i < n; s += Query.aggregate; i += 1; end; s }
t("query_lazy", n(700))       { |n| i = 0; s = 0; while i < n; s += Query.lazy; i += 1; end; s }
