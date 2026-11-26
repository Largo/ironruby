# check-startup.sh runs this with a library of not-run.rb and refinements.rb and without it, and compares.
require_relative "not-run"
require_relative "refinements"
p StartupFixture.evals, StartupFixture.binding_set, StartupFixture.defined, StartupFixture.trace, StartupFixture.string_evals
p %i[A B C].map { |c| StartupFixture.const_source_location(c)&.then { |f, l| [File.basename(f), l] } }
p StartupFixture.const_source_location(:Later)&.then { |f, l| [File.basename(f), l] }
p((StartupFixtureRefinements.shout rescue $!), (StartupFixtureRefinements.twice rescue $!))
p StartupFixture.noted_argument_order(1), StartupFixture.failed_notes
