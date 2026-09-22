require 'listen'
require 'fileutils'
require 'tmpdir'

# The polling backend, which needs no OS event API - the ffi-backed inotify
# adapter is a separate question (see POPULAR.md).
dir = File.join(Dir.tmpdir, "listen-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(dir)

seen = Queue.new
listener = Listen.to(dir, force_polling: true, latency: 0.05, wait_for_delay: 0.05) do |mod, add, rem|
  seen << [mod.map { |f| File.basename(f) }, add.map { |f| File.basename(f) }, rem.map { |f| File.basename(f) }]
end
puts listener.class
listener.start
puts listener.processing?

File.write(File.join(dir, 'a.txt'), 'one')
deadline = Time.now + 10
events = []
while Time.now < deadline
  begin
    events << seen.pop(true)
  rescue ThreadError
    sleep 0.1
  end
  break if events.flatten.include?('a.txt')
end
puts events.flatten.include?('a.txt')

listener.pause
listener.start
puts listener.processing?
listener.stop
puts listener.stopped?

puts Listen::VERSION.split('.').first
puts Listen::Adapter::Polling.usable?
puts Listen::Adapter.select(force_polling: true).name.split('::').last
FileUtils.rm_rf(dir)
