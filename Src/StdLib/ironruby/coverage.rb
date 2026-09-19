load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Coverage'

# MRI's ext/coverage. Only line coverage is measured (lines, oneshot_lines, eval):
# it counts at the TracePoint :line hooks of code compiled while the measurement is
# set up. Branch and method coverage are not implemented - supported? says so, and
# a result leaves them out.
module Coverage
  # CoverageModes in the runtime
  LINES = 0x1
  BRANCHES = 0x2
  METHODS = 0x4
  ONESHOT_LINES = 0x8
  EVAL = 0x10
  private_constant :LINES, :BRANCHES, :METHODS, :ONESHOT_LINES, :EVAL

  @state = :idle
  @mode = nil

  class << self
    def setup(*args)
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)" if args.size > 1
      raise RuntimeError, "coverage measurement is already setup" unless @state == :idle

      if args.empty?
        mode = 0 # compatible mode: lines, reported as bare arrays
      elsif args[0] == :all
        mode = LINES | BRANCHES | METHODS | EVAL
      else
        opt = Hash.try_convert(args[0]) or
          raise TypeError, "no implicit conversion of #{__type_name__(args[0])} into Hash"
        mode = 0
        mode |= LINES if opt[:lines]
        mode |= BRANCHES if opt[:branches]
        mode |= METHODS if opt[:methods]
        if opt[:oneshot_lines]
          raise RuntimeError, "cannot enable lines and oneshot_lines simultaneously" if mode & LINES != 0
          mode |= LINES | ONESHOT_LINES
        end
        mode |= EVAL if opt[:eval]
      end

      @mode = mode
      __setup__(mode == 0 ? LINES : mode)
      @state = :suspended
      nil
    end

    def resume
      raise RuntimeError, "coverage measurement is not set up yet" if @state == :idle
      raise RuntimeError, "coverage measurement is already running" if @state == :running
      __resume__(true)
      @state = :running
      nil
    end

    def suspend
      raise RuntimeError, "coverage measurement is not running" unless @state == :running
      __resume__(false)
      @state = :suspended
      nil
    end

    def start(*args)
      setup(*args)
      resume
      nil
    end

    def peek_result
      raise RuntimeError, "coverage measurement is not enabled" if @state == :idle

      result = {}
      __peek__.each do |path, lines, oneshot_lines|
        if @mode == 0
          result[path] = lines.freeze
        else
          file = {}
          if @mode & ONESHOT_LINES != 0
            file[:oneshot_lines] = oneshot_lines.freeze
          elsif @mode & LINES != 0
            file[:lines] = lines.freeze
          end
          result[path] = file
        end
      end
      result.freeze
    end

    def result(*args)
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)" if args.size > 1
      raise RuntimeError, "coverage measurement is not enabled" if @state == :idle

      stop = clear = true
      unless args.empty?
        opt = Hash.try_convert(args[0]) or
          raise TypeError, "no implicit conversion of #{__type_name__(args[0])} into Hash"
        stop = !!opt[:stop]
        clear = !!opt[:clear]
      end

      result = peek_result
      if stop && !clear
        warn "stop implies clear", uplevel: 1
        clear = true
      end
      __clear__ if clear
      if stop
        suspend if @state == :running
        __reset__
        @state = :idle
        @mode = nil
      end
      result
    end

    def state
      @state
    end

    def running?
      @state == :running
    end

    def supported?(mode)
      raise TypeError, "wrong argument type #{__type_name__(mode)} (expected Symbol)" unless mode.is_a?(Symbol)
      mode == :lines || mode == :oneshot_lines || mode == :eval
    end

    private

    def __type_name__(obj)
      case obj
      when nil, true, false then obj.inspect
      else obj.class.to_s
      end
    end
  end
end
