# IronRuby regression checks for discarded fibers. Run with a built Release runtime:
# IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/fiber-reclaim-matrix.rb
require 'weakref'

module FiberReclaimChecks
  def self.assert(value, message)
    raise message unless value
  end

  def self.eventually(message)
    deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + 10
    loop do
      GC.start
      System::GC.wait_for_pending_finalizers
      return if yield
      raise message if Process.clock_gettime(Process::CLOCK_MONOTONIC) >= deadline
      sleep 0.01
    end
  end

  # No handle local in this factory. Its completed interpreter frame must not keep
  # its return value alive through the block's captured Ruby scope.
  def self.build(events)
    Fiber.new do
      begin
        begin
          Fiber.yield :parked
          events << :continued
        rescue Exception
          events << :rescued
        ensure
          events << :inner_ensure
        end
      ensure
        events << :outer_ensure
      end
    end
  end

  def self.discard(events)
    fiber = build(events)
    assert(fiber.resume == :parked, 'fiber did not park')
    [WeakRef.new(fiber), fiber.__state__.__core__, fiber.__state__.__thread__]
  end

  def self.build_enumerator(events)
    Enumerator.new do |y|
      begin
        y << :parked
        events << :continued
      rescue Exception
        events << :rescued
      ensure
        events << :ensure
      end
    end
  end

  def self.discard_enumerator(events)
    enum = build_enumerator(events)
    assert(enum.next == :parked, 'enumerator did not park')
    fiber = enum.instance_variable_get(:@__fiber__)
    [WeakRef.new(enum), WeakRef.new(fiber), fiber.__state__.__core__]
  end

  def self.build_native_enumerator
    { :a => 1, :b => 2 }.each
  end

  def self.discard_native_enumerator
    enum = build_native_enumerator
    assert(enum.next == [:a, 1], 'native enumerator did not park')
    fiber = enum.instance_variable_get(:@__fiber__)
    [WeakRef.new(enum), WeakRef.new(fiber), fiber.__state__.__core__]
  end

  def self.build_traced_fiber(events)
    Fiber.new do
      reclaim_marker = true
      begin
        Fiber.yield
      ensure
        events << :traced_ensure
      end
    end
  end

  def self.discard_traced_fiber(events)
    fiber = build_traced_fiber(events)
    fiber.resume
    [WeakRef.new(fiber), fiber.__state__.__core__]
  end

  def self.block_factory(events)
    # Covers a block factory returning the handle, not just a method factory.
    [events].map do |list|
      Fiber.new { begin; Fiber.yield; ensure; list << :ensure; end }
    end.first
  end

  def self.discard_block_factory(events)
    fiber = block_factory(events)
    fiber.resume
    [WeakRef.new(fiber), fiber.__state__.__core__]
  end

  def self.captured_handle(clear)
    fiber = nil
    fiber = Fiber.new { Fiber.yield; fiber }
    fiber.resume
    weak = WeakRef.new(fiber)
    fiber = nil if clear
    weak
  end

  def self.run
    root = Fiber.current
    initial_threads = Thread.list.size
    events = []
    weak, core, worker = discard(events)
    eventually('discarded handle or worker was retained') do
      !weak.weakref_alive? && core.status == 'terminated' && !Thread.list.include?(worker)
    end
    assert(events.empty?, "reclamation ran Ruby code: #{events.inspect}")
    assert(Fiber.current.equal?(root), 'reclamation changed the owner fiber')

    # A reclaimed worker must be reusable, with the reclamation flag cleared and
    # ordinary ensure execution restored.
    fresh = Fiber.new { begin; :complete; ensure; events << :normal_ensure; end }
    assert(fresh.resume == :complete, 'reused worker did not complete')
    assert(fresh.__state__.__thread__.equal?(worker), 'reclaimed worker was not reused')
    assert(events == [:normal_ensure], 'reused worker skipped its ensure')
    events.clear

    enum_weak, fiber_weak, enum_core = discard_enumerator(events)
    eventually('discarded enumerator retained its worker') do
      !enum_weak.weakref_alive? && !fiber_weak.weakref_alive? && enum_core.status == 'terminated'
    end
    assert(events.empty?, 'discarded enumerator ran an ensure or rescue')

    native_weak, native_fiber_weak, native_core = discard_native_enumerator
    eventually('discarded native enumerator retained its worker') do
      !native_weak.weakref_alive? && !native_fiber_weak.weakref_alive? && native_core.status == 'terminated'
    end

    trace = TracePoint.new(:b_return) do |tp|
      if tp.path == __FILE__ && tp.binding.local_variable_defined?(:reclaim_marker)
        events << :trace_return
      end
    end
    begin
      trace.enable
      traced_weak, traced_core = discard_traced_fiber(events)
      eventually('traced fiber retained its worker') do
        !traced_weak.weakref_alive? && traced_core.status == 'terminated'
      end
      assert(events.empty?, 'reclamation invoked Ruby tracing or ensure code')
      normal_traced = build_traced_fiber(events)
      normal_traced.resume
      normal_traced.resume
      assert(events.include?(:trace_return), 'ordinary fiber termination lost its trace hook')
      assert(events.include?(:traced_ensure), 'ordinary traced fiber lost its ensure')
      events.clear
    ensure
      trace.disable
    end

    block_weak, block_core = discard_block_factory(events)
    eventually('completed block factory retained its return value') do
      !block_weak.weakref_alive? && block_core.status == 'terminated'
    end
    assert(events.empty?, 'discarded block-factory fiber ran its ensure')

    # Keeping the public handle alive must keep a suspended fiber resumable.
    live = Fiber.new { Fiber.yield :first; :last }
    assert(live.resume == :first, 'live fiber did not yield')
    3.times { GC.start; System::GC.wait_for_pending_finalizers }
    assert(live.alive? && live.resume == :last, 'reachable fiber was reclaimed')

    [:dup, :clone].each do |method|
      copy = live.__send__(method)
      begin
        copy.resume
        raise 'copied handle shared its source fiber execution state'
      rescue FiberError => error
        assert(error.message == 'uninitialized fiber', 'copied fiber raised the wrong error')
      end
    end

    # An overridden #each must still run on its original public receiver.
    custom = [1].to_enum
    def custom.each
      yield self
    end
    assert(custom.next.equal?(custom), 'custom Enumerator#each receiver changed')
    custom.rewind

    killed = build(events)
    killed.resume
    killed.kill
    assert(events == [:inner_ensure, :outer_ensure], 'normal kill skipped ensure')
    events.clear
    raised = build(events)
    raised.resume
    raised.raise(RuntimeError, 'normal raise')
    assert(events == [:rescued, :inner_ensure, :outer_ensure], 'normal raise changed rescue/ensure')
    events.clear

    # A worker stays strongly attached to its handle while executing user code.
    active = Fiber.new do
      GC.start
      Fiber.yield Fiber.current
      GC.start
      Fiber.current
    end
    assert(active.resume.equal?(active), 'running fiber lost its identity')
    assert(active.resume.equal?(active), 'resumed fiber lost its identity')

    3.times do
      wave = Array.new(32) { discard(events) }
      eventually('a wave of discarded workers leaked') do
        wave.all? { |w, c, _t| !w.weakref_alive? && c.status == 'terminated' } &&
          Thread.list.size <= initial_threads
      end
    end
    assert(events.empty?, 'discarded worker waves ran Ruby cleanup')

    # Captured program references remain real roots. They require explicit
    # completion/kill; clearing the captured local makes the handle collectible.
    cycle = captured_handle(false)
    3.times { GC.start; System::GC.wait_for_pending_finalizers }
    assert(cycle.weakref_alive?, 'a captured live handle was reclaimed')
    cycle.__getobj__.kill
    cleared = captured_handle(true)
    eventually('cleared captured local still retained its handle') { !cleared.weakref_alive? }

    assert(worker.join(5) && !worker.alive?, 'idle reclaimed worker did not retire')

    puts 'PASS: collection, worker reuse, nested ensures, rescue, enumerators, live handles, kill/raise, and discarded-worker waves'
  end
end

FiberReclaimChecks.run
