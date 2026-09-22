# Compatibility patches for gems that assume an engine they know about.
#
# A gem that branches on RUBY_ENGINE sometimes lands on a fallback path that is
# broken for everyone outside its matrix.  Where the fix belongs upstream but a
# popular library is unusable without it, register it here: the patch runs once,
# right after the file that needs it is required, and only if the condition that
# makes it necessary actually holds.
#
# Keep this list short, keep every entry commented with the upstream reason, and
# prefer fixing IronRuby over patching a gem.
module IronRuby
  module GemCompat
    @patches = Hash.new { |h, k| h[k] = [] }
    @applied = {}

    class << self
      # Run +block+ after the feature whose path ends in +suffix+ is required.
      def on_require(suffix, &block)
        @patches[suffix] << block
        self
      end

      # Called by the require hook with the feature as the caller spelled it.
      def after_require(feature)
        return if @patches.empty?
        name = feature.to_s
        @patches.each do |suffix, blocks|
          next unless name == suffix || name.end_with?("/#{suffix}")
          next if @applied[suffix]
          @applied[suffix] = true
          blocks.each do |block|
            begin
              block.call
            rescue ::StandardError, ::ScriptError
              # A compatibility patch must never break the require it follows.
            end
          end
        end
      end
    end
  end

end

# Wrap Kernel#require by capturing the current implementation rather than by
# prepending a module.  RubyGems boots later and does
# `alias_method :gem_original_require, :require`, which picks up whatever is in
# front -- a prepended module included -- and then calls it from its own #require.
# A wrapper that used `super` would therefore be re-entered by the very method it
# delegates to, and the second `super` would find nothing.  Binding the previous
# implementation makes the chain a straight line: RubyGems -> this -> the loader.
module Kernel # :nodoc:
  previous_require = instance_method(:require)

  define_method(:require) do |feature|
    loaded = previous_require.bind(self).call(feature)
    ::IronRuby::GemCompat.after_require(feature) if loaded
    loaded
  end

  private :require
  module_function :require
  private :require
end

# concurrent-ruby: Concurrent::Map falls back to SynchronizedMapBackend for any
# engine that is not MRI, JRuby or TruffleRuby, and that backend guards each
# method with a non-reentrant Mutex -- its own comment says "the synchronized
# methods are not allowed to call each other".  A Map built with a default_proc
# does exactly that (the block runs inside #[] and calls #compute_if_absent), so
# the first lookup deadlocks.  ActiveSupport::Notifications::Fanout and i18n both
# build such a Map, which makes `require "active_record"` and every translation
# raise ThreadError on IronRuby.
#
# Monitor has the same mutual exclusion between threads and is reentrant within
# one, which is the property the backend's callers need.  Upstream fix pending.
IronRuby::GemCompat.on_require("concurrent/collection/map/synchronized_map_backend") do
  backend = Concurrent::Collection::SynchronizedMapBackend
  if backend.instance_method(:initialize).owner == backend
    backend.class_eval do
      def initialize(*args, &block)
        super
        @mutex = ::Monitor.new
      end
    end
  end
end
