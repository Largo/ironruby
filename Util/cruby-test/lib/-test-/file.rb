# frozen_string_literal: true
# ext/-test-/file in Ruby, as far as it can be. Fs.fsname and Fs.noatime? read the file system
# a path is on, here from /proc/self/mounts where there is one (nil otherwise, as the C
# extension answers without statfs). NewlineConv and Stat open files or read a struct stat
# through the C API, which IronRuby does not have: they raise NotImplementedError.
module Bug
  module File
    module Fs
      def self.__mount__(path)
        # the kernel writes a space, tab, newline or backslash in a mount point as \040 and so on
        mounts = ::File.read("/proc/self/mounts").lines.map do |line|
          fields = line.split
          fields[1] = fields[1].gsub(/\\([0-7]{3})/) { $1.to_i(8).chr }
          fields
        end rescue (return nil)
        path = ::File.realpath(path)
        mounts.select { |_, dir, *| path == dir || path.start_with?(dir.end_with?("/") ? dir : "#{dir}/") }
              .max_by { |_, dir, *| dir.size }
      end

      def self.fsname(path)
        __mount__(path)&.[](2)
      end

      def self.noatime?(path)
        mount = __mount__(path) or return nil
        mount[3].split(",").include?("noatime")
      end
    end

    module NewlineConv
      %i[rb_file_open rb_io_fdopen].each do |name|
        define_singleton_method(name) { |*| raise NotImplementedError, "Bug::File::NewlineConv.#{name} calls CRuby's C API" }
      end
    end

    module Stat
      %i[stat lstat].each do |name|
        define_singleton_method(name) { |*| raise NotImplementedError, "Bug::File::Stat.#{name} calls CRuby's C API" }
      end
    end
  end
end
