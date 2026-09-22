# frozen_string_literal: true
#
# error_highlight, for IronRuby.
#
# The upstream library (Src/StdLib/ruby/4.0/error_highlight) finds the exact
# code fragment an exception came from by asking
# RubyVM::AbstractSyntaxTree.of(backtrace_location) for the node that raised.
# That is CRuby's own compiled AST, addressed by the node id CRuby stores in
# every backtrace frame.  IronRuby's frames carry a file and a line and no node
# identity, so there is nothing to look the node up by - `ErrorHighlight.spot`
# would raise NameError for RubyVM the moment anything called it.
#
# `spot` returning nil is upstream's documented answer for "the fragment could
# not be identified" - it returns nil for multi-line expressions, for frames
# without script lines, and whenever the AST lookup comes up empty - and every
# caller already handles it.  ActiveSupport calls it for every frame of the
# Rails error page (active_support/core_ext/thread/backtrace/location.rb), so
# the constant has to exist and the call has to succeed; the page then simply
# shows the line rather than the underlined fragment.
#
# Giving IronRuby the real thing means giving prism-parsed positions to
# backtrace frames, which is a separate piece of work (see Src/Prism).

require_relative "../ruby/4.0/error_highlight/version"

module ErrorHighlight
  # :call-seq:
  #   ErrorHighlight.spot(obj, **opts) -> nil
  #
  # Always nil here: see the note at the top of this file.
  def self.spot(_obj, point_type: :name, backtrace_location: nil)
    nil
  end

  # The formatter upstream installs on NoMethodError/NameError messages.  With
  # spot answering nil there is never a snippet to append, so the message is
  # the plain one.
  module DefaultFormatter
    def self.message_for(_spot)
      ""
    end
  end

  def self.formatter
    @formatter ||= DefaultFormatter
  end

  def self.formatter=(formatter)
    @formatter = formatter
  end
end
