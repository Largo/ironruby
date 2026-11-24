# The parts of app-shapes.rb's invoice_build and invoice_render (module Shop), one operation per
# shape, to see which of them carry the gap to CRuby:
#   ruby Util/bench/invoice-parts.rb [shape...]
#   ruby Util/perf/ab.rb Util/bench/invoice-parts.rb "base=..." "new=..." -- money_new money_with
$stdout.sync = true
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
def t(name, n)
  return puts(name) if ARGV == ['--list']
  return unless ARGV.empty? || ARGV.include?(name)
  best = 1e9; sum = nil
  7.times { t0 = cpu; sum = yield n; d = cpu - t0; best = d if d < best }
  printf "%-16s %9.1f ns  %d\n", name, best * 1e9 / n, sum
end
N = (ENV["N"] || 1000).to_i
def n(c) = [c * N / 1000, 1].max

module P
  Address = Struct.new(:street, :city, :country, keyword_init: true)
  Money = Data.define(:cents) do
    include Comparable
    def +(other) = with(cents: cents + other.cents)
    def *(n) = with(cents: (cents * n).round)
    def <=>(other) = cents <=> other.cents
    def to_s = format("%d.%02d", cents / 100, cents % 100)
  end
  class Product
    attr_reader :sku, :price
    def initialize(sku:, name:, price:, tax: :standard)
      @sku, @name, @price, @tax = sku, name, Money.new(price), tax
    end
  end
  class Line
    attr_reader :qty
    def initialize(product, qty: 1, discount: 0)
      @product, @qty, @discount = product, qty, discount
    end
  end
  class Doc
    def audit_log = (@audit_log ||= [])
    def record(event, **details) = audit_log << { event: event, at: audit_log.size, **details }
  end
  module Deep; class Order; end; end
  M = Money.new(5)
  PR = Product.new(sku: "A", name: "B", price: 1)
  LINES = Array.new(6) { |i| Money.new(i) }
end

t("money_new", n(100000))   { |n| s = 0; n.times { |i| s += P::Money.new(i).cents }; s }
t("money_new_kw", n(100000)) { |n| s = 0; n.times { |i| s += P::Money.new(cents: i).cents }; s }
t("money_plus", n(100000))  { |n| m = P::M; s = 0; n.times { s += (m + m).cents }; s }
t("money_mul", n(100000))   { |n| m = P::M; s = 0; n.times { s += (m * 2).cents }; s }
t("money_with", n(100000))  { |n| m = P::M; s = 0; n.times { |i| s += m.with(cents: i).cents }; s }
t("address_new", n(100000)) { |n| s = 0; n.times { s += P::Address.new(street: "a", city: "b", country: "c").country.size }; s }
t("product_new", n(100000)) { |n| s = 0; n.times { s += P::Product.new(sku: "A", name: "B", price: 3).price.cents }; s }
t("product_new_opt", n(100000)) { |n| s = 0; n.times { s += P::Product.new(sku: "A", name: "B", price: 3, tax: :zero).price.cents }; s }
t("line_new", n(100000))    { |n| s = 0; pr = P::PR; n.times { s += P::Line.new(pr, qty: 2).qty }; s }
t("line_new2", n(100000))   { |n| s = 0; pr = P::PR; n.times { s += P::Line.new(pr, qty: 2, discount: 1).qty }; s }
t("record", n(100000))      { |n| s = 0; d = P::Doc.new; n.times { |i| d.instance_variable_set(:@audit_log, nil) if i % 8 == 0; s += d.record(:x, a: 1).size }; s }
t("sum_init", n(30000))     { |n| s = 0; l = P::LINES; n.times { s += l.sum(P::Money.new(0)) { |x| x }.cents }; s }
t("format", n(100000))      { |n| s = 0; n.times { |i| s += format("%d.%02d", i / 100, i % 100).size }; s }
t("class_last", n(100000))  { |n| s = 0; o = P::Deep::Order.new; n.times { s += o.class.name.split('::').last.size }; s }
t("cmp", n(100000))         { |n| s = 0; a = P::M; b = P::Money.new(7); n.times { s += (a <=> b) }; s }
t("sort", n(30000))         { |n| s = 0; l = P::LINES; n.times { s += l.sort.first.cents }; s }
t("sort_by_arr", n(30000))  { |n| s = 0; l = P::LINES; n.times { s += l.sort_by { |x| [-x.cents, "a"] }.first.cents }; s }
