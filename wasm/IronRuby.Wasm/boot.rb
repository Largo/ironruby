# The kernel of the browser host (Interop.cs runs this file as the main program, so the core
# prelude ruby4.rb is loaded first, exactly as for `ir --disable-gems`).
module IronRubyWasm
  BOOT_FILE = __FILE__

  class << self
    attr_reader :count

    # A fresh top-level binding: locals defined by one run stay visible to the next, like the
    # cells of a notebook or the lines of irb. reset starts over (methods and constants stay).
    def reset
      @count = 0
      # The first session runs in TOPLEVEL_BINDING itself (frames read "<main>"); a reset gets a
      # fresh binding nested in it, so the old locals are gone but methods and constants stay.
      @binding = @binding ? TOPLEVEL_BINDING.eval("proc { binding }").call : TOPLEVEL_BINDING
      nil
    end

    # => [:ok, inspect] | [:exit, status] | [:error, class name, message, backtrace]
    def run(code, file = nil)
      @count += 1
      file ||= "cell#{@count}"
      begin
        value = @binding.eval(code, file, 1)
        [:ok, safe_inspect(value)]
      rescue SystemExit => e
        [:exit, e.status]
      rescue Exception => e
        [:error, (e.class.name || e.class.inspect), safe_message(e), clean_backtrace(e)]
      ensure
        begin; $stdout.flush; rescue Exception; end
        begin; $stderr.flush; rescue Exception; end
        begin; STDOUT.flush; rescue Exception; end
      end
    end

    private

    def safe_inspect(value)
      value.inspect.to_s
    rescue Exception => e
      "#<#{value.class} (inspect raised #{e.class}: #{e.message})>"
    end

    def safe_message(e)
      e.message.to_s
    rescue Exception
      ""
    end

    def clean_backtrace(e)
      # leave out the kernel's own frames and the host's CLR frames (":0:in 'Run'", ...)
      (e.backtrace || []).reject { |line| line.start_with?(BOOT_FILE) || line.start_with?(":0:") }
    end
  end

  reset
end
