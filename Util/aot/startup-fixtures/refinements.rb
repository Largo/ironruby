# Uses refinements, which compiled code does not run: the compiler leaves this file out of the library (check-startup.sh
# checks that it says so, and that startup-fixtures/main.rb prints the same as without the library).
module StartupFixtureRefinements
  module Shout
    refine String do
      def shout = upcase + "!"
    end
  end
  using Shout
  def self.shout = "hi".shout

  using Module.new { refine(::Integer) { def twice = self * 2 } }
  def self.twice = 3.twice
end
