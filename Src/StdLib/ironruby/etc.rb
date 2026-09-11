# ****************************************************************************
#
# Copyright (c) Microsoft Corporation.
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A
# copy of the license can be found in the License.html file at the root of this distribution. If
# you cannot locate the  Apache License, Version 2.0, please send an email to
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

# The Unix half of this used to be a binding to Mono.Posix, which does not exist on
# .NET 8 -- `require "etc"` therefore raised LoadError and took every spec file that
# requires it down with it. It is now plain Ruby over /etc/passwd and /etc/group,
# which is what the C extension falls back to reading anyway when NSS is file-backed.

module Etc
  Passwd = Struct.new(:name, :passwd, :uid, :gid, :gecos, :dir, :shell)
  Group  = Struct.new(:name, :passwd, :gid, :mem)

  # Ruby also publishes these under Struct for backwards compatibility.
  Struct.const_set(:Passwd, Passwd) unless Struct.const_defined?(:Passwd, false)
  Struct.const_set(:Group, Group) unless Struct.const_defined?(:Group, false)

  # sysconf(3) variable names. The values are the Linux/glibc ones.
  SC_ARG_MAX = 0
  SC_CHILD_MAX = 1
  SC_CLK_TCK = 2
  SC_NGROUPS_MAX = 3
  SC_OPEN_MAX = 4
  SC_STREAM_MAX = 5
  SC_TZNAME_MAX = 6
  SC_JOB_CONTROL = 7
  SC_SAVED_IDS = 8
  SC_REALTIME_SIGNALS = 9
  SC_PAGESIZE = 30
  SC_PAGE_SIZE = 30
  SC_RE_DUP_MAX = 44
  SC_LOGIN_NAME_MAX = 71
  SC_TTY_NAME_MAX = 72
  SC_SYMLOOP_MAX = 173
  SC_HOST_NAME_MAX = 180
  SC_VERSION = 29
  SC_NPROCESSORS_CONF = 83
  SC_NPROCESSORS_ONLN = 84

  # confstr(3) variable names.
  CS_PATH = 0

  class << self
    if defined?(System::Environment) &&
       [System::PlatformID.Win32S, System::PlatformID.WinCE,
        System::PlatformID.Win32Windows, System::PlatformID.Win32NT
       ].include?(System::Environment.OSVersion.Platform)

      def getlogin
        ENV['USERNAME']
      end

      def endgrent(*args)
        nil
      end

      [:endpwent, :getgrent, :getgrgid, :getgrnam, :getpwent,
       :getpwnam, :getpwuid, :group, :passwd, :setgrent,
       :setpwent].each do |method|
        alias_method method, :endgrent
      end

      def systmpdir
        ENV['TMP'] || ENV['TEMP'] || 'C:/Windows/Temp'
      end

      def sysconfdir
        'C:/Windows'
      end
    else
      PASSWD_FILE = '/etc/passwd'
      GROUP_FILE = '/etc/group'

      def getlogin
        # getlogin(2) is not reachable from here; MRI falls back to ENV['USER']
        # when it returns NULL, and LOGNAME is the same thing logname(1) prints.
        ENV['LOGNAME'] || ENV['USER']
      end

      def systmpdir
        '/tmp'
      end

      def sysconfdir
        '/etc'
      end

      def nprocessors
        System::Environment.ProcessorCount
      end

      def uname
        {
          :sysname => read_proc('/proc/sys/kernel/ostype') || 'Linux',
          :nodename => read_proc('/proc/sys/kernel/hostname') || '',
          :release => read_proc('/proc/sys/kernel/osrelease') || '',
          :version => read_proc('/proc/sys/kernel/version') || '',
          :machine => (`uname -m`.chomp rescue ''),
        }
      end

      # Only the variables whose value we can answer honestly are reported;
      # sysconf is documented to return nil for anything it does not know.
      def sysconf(name)
        case name
        when SC_NPROCESSORS_CONF, SC_NPROCESSORS_ONLN then nprocessors
        when SC_PAGESIZE then 4096
        when SC_OPEN_MAX then (read_proc('/proc/sys/fs/nr_open') || '1048576').to_i
        when SC_HOST_NAME_MAX then 64
        when SC_LOGIN_NAME_MAX then 256
        when SC_NGROUPS_MAX then 65536
        when SC_TTY_NAME_MAX then 32
        when SC_SYMLOOP_MAX then 40
        when SC_CLK_TCK then 100
        else nil
        end
      end

      def confstr(name)
        case name
        when CS_PATH then '/bin:/usr/bin'
        else raise Errno::EINVAL, "confstr(#{name})"
        end
      end

      def getpwnam(name)
        raise TypeError, "no implicit conversion of #{name.class} into String" unless name.is_a?(String)
        entry = each_passwd.find { |pw| pw.name == name }
        raise ArgumentError, "can't find user for #{name}" unless entry
        entry
      end

      def getpwuid(uid = nil)
        uid = Process.uid if uid.nil?
        raise TypeError, "no implicit conversion of #{uid.class} into Integer" unless uid.is_a?(Integer)
        entry = each_passwd.find { |pw| pw.uid == uid }
        raise ArgumentError, "can't find user for #{uid}" unless entry
        entry
      end

      def getgrnam(name)
        raise TypeError, "no implicit conversion of #{name.class} into String" unless name.is_a?(String)
        entry = each_group.find { |gr| gr.name == name }
        raise ArgumentError, "can't find group for #{name}" unless entry
        entry
      end

      def getgrgid(gid = nil)
        gid = Process.gid if gid.nil?
        raise TypeError, "no implicit conversion of #{gid.class} into Integer" unless gid.is_a?(Integer)
        entry = each_group.find { |gr| gr.gid == gid }
        raise ArgumentError, "can't find group for #{gid}" unless entry
        entry
      end

      def setpwent
        @passwd_cursor = 0
        nil
      end

      def endpwent
        @passwd_cursor = nil
        nil
      end

      def getpwent
        @passwd_cursor ||= 0
        entry = each_passwd[@passwd_cursor]
        @passwd_cursor += 1 if entry
        entry
      end

      def setgrent
        @group_cursor = 0
        nil
      end

      def endgrent
        @group_cursor = nil
        nil
      end

      def getgrent
        @group_cursor ||= 0
        entry = each_group[@group_cursor]
        @group_cursor += 1 if entry
        entry
      end

      def passwd
        return getpwent unless block_given?
        raise RuntimeError, "parallel passwd iteration" if @passwd_iterating
        @passwd_iterating = true
        begin
          setpwent
          while (pw = getpwent)
            yield pw
          end
          setpwent
        ensure
          @passwd_iterating = false
        end
        getpwent
      end

      def group
        return getgrent unless block_given?
        raise RuntimeError, "parallel group iteration" if @group_iterating
        @group_iterating = true
        begin
          setgrent
          while (gr = getgrent)
            yield gr
          end
          setgrent
        ensure
          @group_iterating = false
        end
        getgrent
      end

      private

      def read_proc(path)
        File.read(path).chomp
      rescue SystemCallError
        nil
      end

      def each_passwd
        parse(PASSWD_FILE, 7) do |f|
          Passwd.new(f[0], f[1], f[2].to_i, f[3].to_i, f[4], f[5], f[6])
        end
      end

      def each_group
        parse(GROUP_FILE, 4) do |f|
          Group.new(f[0], f[1], f[2].to_i, f[3].to_s.split(',').reject(&:empty?))
        end
      end

      def parse(path, fields)
        result = []
        File.foreach(path) do |line|
          line = line.chomp
          next if line.empty? || line.start_with?('#')
          f = line.split(':', -1)
          next if f.length < fields
          result << yield(f)
        end
        result
      rescue SystemCallError
        []
      end
    end
  end
end
