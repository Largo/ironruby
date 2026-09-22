# ruby-bench: the Rails, web and database macro benchmarks on IronRuby

ruby-bench (https://github.com/ruby/ruby-bench) checked out at
`/root/workspace/ruby-bench`; each of these benchmarks is an application with
its own Gemfile, run the way `run_benchmarks.rb` runs it:

    WARMUP_ITRS=1 MIN_BENCH_ITRS=3 MIN_BENCH_TIME=0 \
      <ruby> -I harness benchmarks/<name>/benchmark.rb

Times below are the median of the three measured iterations, IronRuby Release
on .NET 8 against CRuby 4.0.6 on the same box.  They are indicative rather than
precise: another agent was running benchmarks on the same machine throughout,
and three iterations is not enough warmup for a JIT that is still speeding up at
the last one (rack goes 1197 -> 771 -> 564 ms across the three).

| benchmark    | status | IronRuby | CRuby   | IR/CRuby | what it does, or why not |
|--------------|--------|----------|---------|----------|--------------------------|
| rack         | OK     | 771 ms   | 123 ms  | 6.3x     | 10k requests through a 7-middleware Rack stack |
| activerecord | OK     | 3616 ms  | 759 ms  | 4.8x     | ActiveRecord 8.1 + sqlite3, 100 posts x 20 comments, eager loaded |
| mail         | OK     | 1556 ms  | 406 ms  | 3.8x     | parse and re-emit a multipart message 50x |
| erubi-rails  | OK     | 15689 ms | 5062 ms | 3.1x     | render a Discourse topic template 10k times |
| grape        | OK     | 19334 ms | 1773 ms | 10.9x    | Grape API, 100k requests |
| rubocop      | OK     | 26219 ms | 1139 ms | 23x      | autocorrect a fixture through RuboCop::Runner (one iteration, see below) |
| railsbench   | OK     | 124815 ms| 6271 ms | 19.9x    | Rails 8.1 + sqlite3, 2000 requests, every one asserted 200 |
| sequel       | FAIL   | -        | 94 ms*  | -        | its Gemfile asks for `sqlite3 ~> 1.4`; IronRuby provides sqlite3 2.9.6 and the 1.x gem is C source.  Sequel itself works here - the same script on IronRuby's sqlite3 gives byte-identical output to CRuby.  *one iteration.  The shim is 2.x's API, not 1.x's, so claiming 1.4 would be false - see below |
| ruby-lsp     | N/A    | -        | -       | -        | ruby-lsp depends on rbs, a C extension with no pure-Ruby fallback |
| fluentd      | N/A    | -        | -       | -        | fluentd depends on strptime, and the Gemfile on yajl-ruby; both are C extensions |
| lobsters     | N/A    | -        | -       | -        | needs bcrypt and markly (C extensions), and pins rubocop 0.81 which needs jaro_winkler (C) |
| shipit       | N/A    | -        | -       | -        | shipit-engine -> paquito -> msgpack, and puma; both C extensions |

Correctness was checked against CRuby rather than assumed, because a route that
500s is a benchmark that still produces a number:

* **railsbench** - `/posts`, `/posts.json`, `/posts/1`, `/posts/42`,
  `/posts/100.json` rendered through the real middleware stack are identical to
  CRuby's, 93195 bytes of HTML and JSON, apart from one attribute *order*
  difference per `link_to` (see "Hash insertion order" below).  The benchmark
  itself asserts a 200 on all 2000 requests.
* **erubi-rails** - the rendered template is identical to CRuby's, 9358 bytes,
  apart from the `Time.now` timestamps in it.
* **mail** - the re-emitted message is byte-identical to CRuby's apart from the
  randomly generated Content-ID.
* **activerecord** - pluck, decimal columns, datetimes, `includes`, joins,
  `group.count`, `sum`, `average`, `as_json` and `to_sql` all answer exactly
  what CRuby answers.
* **rubocop** - the same 40 offences, with the same cop names, lines, columns
  and messages.
* **rack** and **grape** assert their own status codes and bodies.

## Bugs found, and what was done about them

Fixed (see the three commits on this branch):

1. `Fiddle::Function` was not implemented, so *every* benchmark died in the
   harness: ruby-bench reads MAXRSS through `getrusage(2)` via Fiddle, and the
   NameError threw away a run that had already finished.  Foreign calls are
   emitted as IL now - a `DynamicMethod` whose body is one `calli`, cached per
   signature - with CRuby's argument conversions, including writing an out
   parameter back into the Ruby String passed for it.  `Fiddle::Pointer` and
   the C99 type numbers came with it.
2. `require "random/formatter"` did not put Formatter into Random, because MRI
   does that from random.c.  `Random.alphanumeric`, which ActiveRecord's own
   benchmark calls, was a NoMethodError.
3. Every patch in `gem_compat.rb` silently stopped applying inside a bundle:
   `module_function` gives Kernel a second copy of `require` on its singleton,
   and Ruby 4.0's bundled_gems.rb routes every require through that copy once
   `Bundler.setup` has run.  This is why `require "active_record"` still
   deadlocked in concurrent-ruby.
4. Bundler resolved bigdecimal, json, psych, sqlite3, nokogiri, cgi and erb
   against rubygems.org and stopped at extconf.rb, for libraries IronRuby
   implements in C#.  Those default gemspecs are marked `ironruby_pinned` now
   and Bundler offers IronRuby's version as the only candidate for them.  irb
   and reline are pinned as well, because irb -> rdoc 8 -> rbs reaches a C
   compiler from `gem "railties"`.
5. `ary.replace(ary)` emptied the array.  `Rails::Command#with_argv` does
   `ARGV.replace(argv)` where argv *is* ARGV, so `rails db:migrate db:seed` ran
   the migration and silently dropped every later task - railsbench's database
   was never seeded and it served 404s.
6. `Object#type`, Ruby 1.8's spelling of `Object#class`, was still defined.
   ActiveRecord's `TimeZoneConverter` answers `#type` through DelegateClass, so
   it returned the class instead of `:datetime` and every time column cast
   wrongly.
7. `JSON::State` did not exist, and ActiveSupport asks
   `options.is_a?(::JSON::State)` in every `to_json` it defines - the first
   jbuilder template rendered raised NameError, so `/posts.json` was a 500.
8. `JSON.generate` took no options at all.  It honours indent, space,
   space_before, object_nl and array_nl now, matching CRuby's output.
9. A folded block scalar with a chomping indicator - `Description: >-`, which
   is how RuboCop writes most of its 2400-line default.yml - was not recognised
   as a block scalar at all, so the first colon in the text raised "mapping
   values are not allowed here".  `|-` was always fine; `>` carried a guard
   that `|` did not.
10. Psych had no event API (`Psych::Parser`, `Handler`, `TreeBuilder`) and none
    of the visitors (`ToRuby`, `ScalarScanner`, `ClassLoader`, `Coder`,
    `YAMLTree`) - which is how RuboCop reads a config and how
    `Gem::Specification#to_yaml` serialises one.  `Psych::Nodes::Alias` could
    not be converted to Ruby in a hand-built tree either.
11. `DateTime#inspect` printed its internal `(jd, seconds)` pair in local time;
    MRI prints it in UTC with the offset beside it.  Only inspect was affected.

Found, not fixed:

* **Hash insertion order after a delete.**  `IronRuby.Builtins.Hash` is a
  `Dictionary<object, object>`, and .NET's Dictionary hands a removed entry's
  slot to the next insertion, so key order diverges from Ruby's:

      h = {"method"=>1, "data"=>2}; h.delete("method"); h["rel"]=3
      CRuby:    ["data", "rel"]        IronRuby: ["rel", "data"]

  ActionView's `link_to` deletes `"method"` from the options and then adds
  `"rel"`, which is why railsbench renders
  `<a rel="nofollow" data-confirm=...>` where CRuby renders
  `<a data-confirm=... rel="nofollow">`.  Hash.cs has said "TODO: ordered
  dictionary" since 2010; fixing it means replacing the backing store, which is
  too large and too shared a change for this round.
* **rake 0.8.7 shadows the rake gem outside a bundle.**  `Src/StdLib/ruby/1.9.1`
  still carries Ruby 1.9's rake, and -X:StdLib puts it on `$LOAD_PATH`, so
  `require "rake"` answers 0.8.7 rather than activating the installed gem.
  Inside a bundle Bundler's load paths win and rake 13 is used, which is why no
  benchmark tripped over it.  The 1.9.1 tree is a deliberate fallback for the
  libraries Ruby 4.0 gemified, so this is a policy question rather than a bug to
  patch here.
* **A YAML timestamp loses its offset.**  `t: 2001-12-14 21:59:43.10 -5` loads
  as `2001-12-15 03:59:43.1 +0100` (the box's zone) instead of CRuby's
  `2001-12-14 21:59:43.1 -0500`.  Pre-existing, in the engine's timestamp
  constructor, and unrelated to the scanner fix above.

### Why the sqlite3 shim cannot stand in for `~> 1.4`

`SQLite3::VERSION` is 2.9.6 because the shim implements sqlite3-ruby 2.x, and
2.0 removed API that 1.x code may use.  Run against CRuby with each real gem
(1.7.3 and 2.9.6) and against the shim, the shim answers exactly as 2.9.6 does
and differently from 1.7.3 on every one of these:

- bind parameters as varargs, `db.execute(sql, 1, "x")` - 1.x binds them, 2.x
  (and the shim) raise ArgumentError;
- `Database#type_translation=`, `#translator`, `SQLite3::Translator`;
- rows that answer `#fields` / `#types` (1.x's ArrayWithTypesAndFields);
- `results_as_hash` rows indexable by column number (`row[0]`), which 1.x
  allows and 2.x does not;
- `SQLite3::VersionProxy`.

So there is no honest way to let Bundler accept it for `~> 1.4`: a gemspec
that said 1.x would promise behaviour the library does not have.  Sequel
supports both majors, so the benchmark's pin is the only obstacle; it is
ruby-bench's, and that checkout is not modified here.

## Running these by hand

The Gemfile.lock of every one of these benchmarks pins bigdecimal, json, prism
or sqlite3 to a version IronRuby does not provide, so the lock has to be removed
before `bundle install` - the same thing a JRuby user does.  With the lock in
place Bundler now fails during resolution, naming the version that is here,
instead of failing inside a C compiler.

Two further things about the environment, neither of them an IronRuby bug:

* ruby-bench's harness shells out to `bundle`, and it compares
  `` `ruby -e 'print RbConfig.ruby'` `` with `RbConfig.ruby` to decide whether to
  rewrite PATH.  A directory holding `ruby`, `gem` and `bundle` shims that exec
  `ir.sh` makes both work; without it the harness runs CRuby's bundler and
  installs CRuby's gems.
* The **rubocop** benchmark reuses one `RuboCop::Runner` across iterations, and
  rubocop 1.91 freezes `@inspected_files` at the end of a run, so the second
  iteration raises FrozenError.  That happens identically on CRuby 4.0.6, so
  rubocop is measured at one iteration on both.  (rubocop 1.91 is what a fresh
  resolution picks; the removed lock pinned 1.79.1.)
