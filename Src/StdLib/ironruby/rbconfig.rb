# frozen-string-literal: false
# (as in MRI's rbconfig.rb: CONFIG hands out mutable strings even under
# --enable-frozen-string-literal, and RbConfig.expand edits them in place)
#
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

module RbConfig
  ::Config = self # compatibility

  # The directory holding ironruby/, ruby/<version>/ and ruby/site_ruby/: <prefix>/Lib
  # in an installed IronRuby, Src/StdLib in a source tree.
  libdir = File.expand_path("..", File.dirname(__FILE__))

  # Like MRI, TOPDIR is the installation prefix, or nil when running from a source tree
  # (MRI: "These directories have no meanings before the installation").
  TOPDIR = File.basename(libdir) == "Lib" ? File.dirname(libdir) : nil

  windows = RUBY_PLATFORM =~ /mswin|mingw/

  CONFIG = {}
  CONFIG["MAJOR"], CONFIG["MINOR"], CONFIG["TEENY"] = RUBY_VERSION.split('.')
  CONFIG["PATCHLEVEL"] = RUBY_PATCHLEVEL.to_s
  CONFIG["RUBY_PROGRAM_VERSION"] = RUBY_VERSION.dup
  CONFIG["RUBY_API_VERSION"] = "#{CONFIG["MAJOR"]}.#{CONFIG["MINOR"]}"
  CONFIG["EXEEXT"] = windows ? ".exe" : ""
  # This value is used by libraries to spawn new processes to run Ruby scripts. Hence it needs to match the ir.exe name
  CONFIG["ruby_install_name"] = "ir"
  CONFIG["RUBY_INSTALL_NAME"] = "ir"
  CONFIG["RUBY_SO_NAME"] = "IronRuby"
  CONFIG["PATH_SEPARATOR"] = File::PATH_SEPARATOR.dup

  # There is no C-level libruby; IronRuby.dll is the runtime an embedding host links to.
  CONFIG["ENABLE_SHARED"] = "no"
  CONFIG["LIBRUBY"] = "IronRuby.dll"

  # Set up paths
  # bindir is where the running ir executable lives, so RbConfig.ruby names it.
  bindir = File.expand_path(System::IO::Path.get_directory_name(System::Reflection::Assembly.get_entry_assembly.location).to_s) rescue nil
  prefix = TOPDIR || File.dirname(libdir)

  CONFIG["bindir"] = bindir || "#{prefix}/bin"
  CONFIG["libdir"] = libdir
  CONFIG["libdirname"] = "libdir"
  CONFIG["LIBPATHENV"] = windows ? "PATH" : (RUBY_PLATFORM =~ /darwin/ ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH")
  CONFIG["prefix"] = prefix.dup
  CONFIG["exec_prefix"] = prefix.dup

  # cpu, os
  cpu_and_os = RUBY_PLATFORM.split('-')
  abort("Could not parse RUBY_PLATFORM") if cpu_and_os.size != 2
  CONFIG["host_cpu"] = CONFIG["target_cpu"] = cpu_and_os[0]
  CONFIG["host_os"] = CONFIG["target_os"] =  cpu_and_os[1]

  # architecture
  clr_version = "#{System::Environment.Version.Major}.#{System::Environment.Version.Minor}"
  CONFIG["arch"] = arch = "universal-dotnet#{clr_version}" # Not strictly true. For example, while running a .NET 2.0 version of IronRuby on .NET 4
  CONFIG["sitearch"] = arch.dup

  # std lib
  CONFIG["ruby_version"] = stdlib_version = "1.9.1"               # std library version
  CONFIG["RUBY_BASE_NAME"] = ruby_base_name = "ruby"              # directory name
  CONFIG["datadir"] = datadir = "#{prefix}/share"
  CONFIG["rubylibprefix"] = rubylibprefix = "#{libdir}/#{ruby_base_name}"
  # The 4.0 library comes first on the load path; the 1.9 tree only backs it up.
  rubylibdir = "#{rubylibprefix}/4.0"
  rubylibdir = "#{rubylibprefix}/#{stdlib_version}" unless File.directory?(rubylibdir)
  CONFIG["rubylibdir"] = rubylibdir
  # What MRI keeps in its arch dir as C extensions IronRuby implements in ironruby/.
  CONFIG["rubyarchdir"] = CONFIG["archdir"] = "#{libdir}/ironruby"

  # ri
  CONFIG["RI_BASE_NAME"] = ri_base_name = "ri"
  CONFIG["ridir"] = "#{datadir}/#{ri_base_name}"

  # site and vendor dirs
  CONFIG["sitedir"] = sitedir = "#{libdir}/#{ruby_base_name}/site_ruby"
  CONFIG["sitelibdir"] = sitelibdir = "#{sitedir}/#{stdlib_version}"
  CONFIG["sitearchdir"] = sitelibdir.dup
  CONFIG["vendordir"] = vendordir = "#{rubylibprefix}/vendor_ruby"
  CONFIG["vendorlibdir"] = vendorlibdir = "#{vendordir}/#{stdlib_version}"
  CONFIG["vendorarchdir"] = vendorlibdir.dup

  CONFIG["SHELL"] = windows ? (ENV["COMSPEC"] || "cmd.exe").dup : "/bin/sh"
  CONFIG["NULLCMD"] = windows ? "rem" : ":"
  CONFIG["DLEXT"] = "so"
  CONFIG["DLEXT2"] = "dll"
  # The standard extensions (etc, socket, zlib, ...) are built in rather than loaded from
  # shared objects, as in an MRI built with --with-static-linked-ext.
  CONFIG["EXTSTATIC"] = "static"

  def RbConfig::expand(val, config = CONFIG)
    newval = val.gsub(/\$\$|\$\(([^()]+)\)|\$\{([^{}]+)\}/) do
      var = $&
      if !(v = $1 || $2)
       '$'
      elsif key = config[v = v[/\A[^:]+(?=(?::(.*?)=(.*))?\z)/]]
        pat, sub = $1, $2
        config[v] = false
        config[v] = RbConfig::expand(key, config)
        key = key.gsub(/#{Regexp.quote(pat)}(?=\s|\z)/n) {sub} if pat
        key
      else
        var
      end
    end
    val.replace(newval) unless newval == val
    val
  end

  # returns the absolute pathname of the ruby command.
  def RbConfig.ruby
    File.join(RbConfig::CONFIG["bindir"], RbConfig::CONFIG["ruby_install_name"] + RbConfig::CONFIG["EXEEXT"])
  end
end
