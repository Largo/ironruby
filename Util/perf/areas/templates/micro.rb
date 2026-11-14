# frozen_string_literal: true
# Template-shaped micro shapes; prints CPU seconds per shape (least of 3).
def t(name)
  best = 1e9
  3.times { s = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); yield; best = [best, Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) - s].min }
  printf "%-22s %.3f\n", name, best
end
N = 2_000_000
h = {"name" => "rake", "version" => "13.0.1", "n" => 42}
t("append_lit") { b = String.new; N.times { b << '  <dt>
  '.freeze; b << 'x' }; b.size }
t("append_to_s") { b = String.new; N.times { b << (h["name"]).to_s } }
t("append_int_to_s") { b = String.new; N.times { b << (h["n"]).to_s } }
t("interp") { N.times { "<a href=\"##{h["name"]}\">#{h["version"]}</a>" } }
t("new_buf_small") { N.times { b = ::String.new; b << 'abc'; b << 'def' } }
t("map_join") { a = Array.new(20) { |i| {"name"=>"g#{i}"} }; (N/20).times { a.map { |v| "<a>#{v["name"]}</a>" }.join ', ' } }
t("escape") { require 'cgi/escape' rescue nil; N.times { CGI.escapeHTML("a<b>&c") } } if defined?(CGI) || (require('cgi') rescue false)
