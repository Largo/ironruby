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
  # No `::Config = self` here. It was Ruby 1.8's spelling of RbConfig and MRI dropped it in
  # 1.9.3; keeping it squats on a top-level name that applications use constantly - `class
  # Config` in a gem's own code then fails with "Config is not a class", which is how Hashie's
  # documented example breaks. Anything still asking for ::Config is asking for Ruby 1.8.

  # The directory holding ironruby/, ruby/<version>/ and ruby/site_ruby/: <prefix>/Lib
  # in an installed IronRuby, Src/StdLib in a source tree.
  libdir = File.expand_path("..", File.dirname(__FILE__))

  # Like MRI, TOPDIR is the installation prefix, or nil when running from a source tree
  # (MRI: "These directories have no meanings before the installation").
  TOPDIR = File.basename(libdir) == "Lib" ? File.dirname(libdir) : nil

  # DESTDIR on make install.
  DESTDIR = '' unless defined? DESTDIR

  windows = RUBY_PLATFORM =~ /mswin|mingw/

  CONFIG = {}
  CONFIG["DESTDIR"] = DESTDIR
  CONFIG["MAJOR"], CONFIG["MINOR"], CONFIG["TEENY"] = RUBY_VERSION.split('.')
  CONFIG["PATCHLEVEL"] = RUBY_PATCHLEVEL.to_s
  CONFIG["RUBY_PROGRAM_VERSION"] = RUBY_VERSION.dup
  CONFIG["RUBY_API_VERSION"] = "#{CONFIG["MAJOR"]}.#{CONFIG["MINOR"]}"
  CONFIG["EXEEXT"] = windows ? ".exe" : ""
  # What a PATH search tries after a bare command name: configure.ac sets it on Windows only.
  CONFIG["EXECUTABLE_EXTS"] = windows ? ".exe .com .cmd .bat" : ""
  # Libraries spawn RbConfig.ruby to run Ruby scripts, so this has to be a file in bindir:
  # the build puts an ironruby apphost beside ir (Ruby.Console.csproj). And Bundler and
  # Rails write it into binstubs as `#!/usr/bin/env ironruby`, which the ironruby launcher
  # answers - a #! line with "ruby" in it, as JRuby's "jruby" is. (RubyGems' own binstubs
  # name the launcher by its path: rubygems/defaults/ironruby.rb.)
  CONFIG["ruby_install_name"] = "ironruby"
  CONFIG["RUBY_INSTALL_NAME"] = "ironruby"
  CONFIG["RUBY_SO_NAME"] = "IronRuby"
  CONFIG["PATH_SEPARATOR"] = File::PATH_SEPARATOR.dup

  # There is no C-level libruby to link a C extension against: an extension's rb_* symbols are
  # resolved when it is loaded, against libironruby-capi (Src/Ruby/CApi), as they are in a CRuby
  # built without --enable-shared. IronRuby.dll is the runtime an embedding host links to.
  CONFIG["ENABLE_SHARED"] = "no"
  CONFIG["LIBRUBY"] = "IronRuby.dll"

  # Set up paths
  # bindir is where the running ir executable lives, so RbConfig.ruby names it: the entry
  # assembly's directory, which the host answers along with the runtime's version - without
  # CLR interop, whose call sites cost every boot a rule and a reflection lookup each.
  host_dir, clr_version = IronRuby.__send__(:__host_config__)
  bindir = (File.expand_path(host_dir) if host_dir) rescue nil
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
  # The .NET runtime IronRuby is running on, not the framework it was compiled for: the
  # net10.0 build answers "universal-dotnet11.0" under the .NET 11 runtime.
  CONFIG["arch"] = arch = "universal-dotnet#{clr_version}"
  CONFIG["sitearch"] = arch.dup

  # std lib
  # The API version, "4.0.0" as in CRuby 4.0. It names the site and vendor directories
  # (site_ruby/4.0.0 is first on $LOAD_PATH - Loader.cs - and empty as shipped) and the
  # gem trees (gems/4.0.0, ~/.gem/ironruby/4.0.0). The standard library itself is
  # ruby/4.0 (rubylibdir below).
  CONFIG["ruby_version"] = stdlib_version = "#{CONFIG["MAJOR"]}.#{CONFIG["MINOR"]}.0"
  CONFIG["RUBY_BASE_NAME"] = ruby_base_name = "ruby"              # directory name
  CONFIG["datadir"] = datadir = "#{prefix}/share"
  CONFIG["rubylibprefix"] = rubylibprefix = "#{libdir}/#{ruby_base_name}"
  CONFIG["rubylibdir"] = "#{rubylibprefix}/4.0"
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

  # The Unicode character data version this distribution ships: the bundled
  # Ruby 4.0 standard library (unicode_normalize tables etc.) is generated
  # from Unicode 17.0.0, as in CRuby 4.0.
  CONFIG["UNICODE_VERSION"] = "17.0.0"
  CONFIG["UNICODE_EMOJI_VERSION"] = "17.0"

  # Binutils, resolved via PATH as in an MRI mingw/gcc build. IronRuby has no
  # C-extension build chain of its own; these name the platform's tools.
  CONFIG["AR"] = "ar"
  CONFIG["STRIP"] = "strip"

  # IronRuby is not built by an autoconf configure script; mkmf only ever
  # shellsplits this, so an empty argument list is the honest answer.
  CONFIG["configure_args"] = ""
  CONFIG["CROSS_COMPILING"] = "no"

  CONFIG["SHELL"] = windows ? (ENV["COMSPEC"] || "cmd.exe").dup : "/bin/sh"
  CONFIG["NULLCMD"] = windows ? "rem" : ":"
  CONFIG["DLEXT"] = "so"
  CONFIG["DLEXT2"] = "dll"
  # The standard extensions (etc, socket, zlib, ...) are built in rather than loaded from
  # shared objects, as in an MRI built with --with-static-linked-ext.
  CONFIG["EXTSTATIC"] = "static"

  # The headers a C extension compiles against: CRuby 4.0's, with the macros that read object
  # memory turned into calls (Util/capi/import-headers.rb). config.h is per platform; only
  # x86_64-linux's is shipped so far.
  hdrdir = "#{libdir}/include"
  CONFIG["rubyhdrdir"] = hdrdir
  CONFIG["rubyarchhdrdir"] = windows ? "#{hdrdir}/x64-mingw-ucrt" : "#{hdrdir}/#{cpu_and_os[0]}-#{cpu_and_os[1].sub(/-gnu\z/, '')}"
  CONFIG["sitehdrdir"] = "#{hdrdir}/site_ruby"
  CONFIG["vendorhdrdir"] = "#{hdrdir}/vendor_ruby"
  CONFIG["topdir"] = File.dirname(__FILE__)
  CONFIG["build_os"] ||= CONFIG["host_os"].dup

  # autoconf's system triplets, as CRuby's configure records them ("x86_64-pc-linux"): build
  # scripts hand them to the configure of a library they compile (nokogiri's libxml2, through
  # mini_portile, passes --host=CONFIG["host"] unless host_alias names one). The *_alias are
  # what --host/--build/--target said, and IronRuby is not cross-built: empty.
  %w[host build target].each do |kind|
    CONFIG["#{kind}_cpu"] ||= cpu_and_os[0].dup
    CONFIG["#{kind}_os"] ||= cpu_and_os[1].dup
    CONFIG["#{kind}_vendor"] = "pc"
    CONFIG["#{kind}_alias"] = ""
  end

  # The installation layout autoconf describes, under prefix as in CRuby.
  {
    "datarootdir" => "#{prefix}/share", "includedir" => "#{prefix}/include", "oldincludedir" => "/usr/include",
    "docdir" => "#{prefix}/share/doc/ruby", "htmldir" => "#{prefix}/share/doc/ruby", "mandir" => "#{prefix}/share/man",
    "infodir" => "#{prefix}/share/info", "localedir" => "#{prefix}/share/locale", "sysconfdir" => "#{prefix}/etc",
    "localstatedir" => "#{prefix}/var", "runstatedir" => "#{prefix}/var/run", "sharedstatedir" => "#{prefix}/com",
    "sbindir" => "#{prefix}/sbin", "libexecdir" => "#{prefix}/libexec",
  }.each { |key, value| CONFIG[key] = value }
  CONFIG["platform"] = RUBY_PLATFORM.dup

  # The C toolchain mkmf drives. Where IronRuby has no C API (Windows so far), every one of
  # these is empty: mkmf reads them while it loads and would otherwise fail on nil, and empty
  # makes every compile mkmf attempts fail rather than appear to succeed. Linux fills the
  # toolchain in below.
  %w[
    ADDITIONAL_DLDFLAGS ARCH_FLAG ASSEMBLE_C ASSEMBLE_CXX BUILD_FILE_SEPARATOR
    CC_WRAPPER CFLAGS CLEANFILES COMMON_HEADERS COMMON_LIBS COMMON_MACROS
    COMPILE_C COMPILE_CXX COMPILE_RULES COUTFLAG CPPFLAGS CPPOUTFILE CSRCFLAG
    CXXFLAGS CXX_EXT DISTCLEANDIRS DISTCLEANFILES DLDFLAGS DLDLIBS EXPORT_PREFIX
    GCC LDFLAGS LIBARG LIBPATHFLAG LIBRUBYARG LIBRUBYARG_SHARED
    LIBRUBYARG_STATIC LIBS LINK_SO MAIN_DOES_NOTHING OUTFLAG RPATHFLAG
    RULE_SUBST TRY_LINK TRY_LINK_CXX UNIVERSAL_INTS warnflags
  ].each { |key| CONFIG[key] = "" }

  # The C toolchain for extensions (Linux; Windows and macOS do not have the C API yet): gcc, as
  # CRuby's Linux builds use, linking libironruby-capi where CRuby links libruby.
  unless windows
    {
      "CC" => "gcc", "CPP" => "$(CC) -E", "CXX" => "g++", "GCC" => "yes",
      "CFLAGS" => "$(cflags)", "cflags" => "$(optflags) $(debugflags) $(warnflags)",
      "optflags" => "-O2", "debugflags" => "-g", "warnflags" => "-Wall -Wno-unused-parameter",
      "CXXFLAGS" => "$(cxxflags)", "cxxflags" => "$(optflags) $(debugflags) $(warnflags)",
      "CPPFLAGS" => " $(DEFS) $(cppflags)", "cppflags" => "", "DEFS" => "",
      "CCDLFLAGS" => "-fPIC", "LDFLAGS" => "-L.", "DLDFLAGS" => "", "EXTDLDFLAGS" => "",
      "LDSHARED" => "$(CC) -shared", "LDSHAREDXX" => "$(CXX) -shared",
      "LIBS" => "-lm -lpthread", "DLDLIBS" => "-lc",
      # linked against the shim, which lives next to the ir executable, so that mkmf's
      # have_func test programs link and the extension records what it needs
      "LIBRUBYARG" => "$(LIBRUBYARG_SHARED)",
      "LIBRUBYARG_SHARED" => "-Wl,-rpath,$(bindir) -L$(bindir) -lironruby-capi",
      "LIBRUBYARG_STATIC" => "-Wl,-rpath,$(bindir) -L$(bindir) -lironruby-capi",
      "OUTFLAG" => "-o ", "COUTFLAG" => "-o ", "CPPOUTFILE" => "-o conftest.i",
      "LIBPATHFLAG" => " -L%1$-s", "RPATHFLAG" => " -Wl,-rpath,%1$-s",
      "LINK_SO" => "", "POSTLINK" => ":", "TRY_LINK" => "",
      "INSTALL" => "/usr/bin/install -c", "INSTALL_PROG" => "$(INSTALL) -m 0755",
      "INSTALL_DATA" => "$(INSTALL) -m 644", "INSTALL_SCRIPT" => "$(INSTALL)",
      "MAKEDIRS" => "mkdir -p", "MKDIR_P" => "mkdir -p", "RM" => "rm -f",
      "RMDIR" => "rmdir --ignore-fail-on-non-empty", "RMALL" => "rm -fr", "CP" => "cp",
    }.each { |key, value| CONFIG[key] = value.dup }
  else
    # Windows: MinGW gcc, as RubyInstaller's CRuby builds extensions (its DevKit, or MSYS2's
    # UCRT64 gcc and make), linking libironruby-capi.dll's import library from bindir.
    # RUBY_PLATFORM and host_os still say mswin64, which is what IronRuby is; build_os and the
    # shell describe the toolchain (mkmf turns C:/ paths into /C/ ones for build_os mingw32).
    {
      "CC" => "gcc", "CPP" => "$(CC) -E", "CXX" => "g++", "GCC" => "yes",
      "CFLAGS" => "$(cflags)", "cflags" => "$(optflags) $(debugflags) $(warnflags)",
      "optflags" => "-O2", "debugflags" => "-g", "warnflags" => "-Wall -Wno-unused-parameter",
      "CXXFLAGS" => "$(cxxflags)", "cxxflags" => "$(optflags) $(debugflags) $(warnflags)",
      "CPPFLAGS" => "-D_WIN32_WINNT=0x0600 -D__MINGW_USE_VC2005_COMPAT $(DEFS) $(cppflags)", "cppflags" => "", "DEFS" => "",
      "CCDLFLAGS" => "", "LDFLAGS" => "-L.", "DLDFLAGS" => "-Wl,--enable-auto-image-base,--enable-auto-import", "EXTDLDFLAGS" => "",
      "LDSHARED" => "$(CC) -shared", "LDSHAREDXX" => "$(CXX) -shared",
      "LIBS" => "-lshell32 -lws2_32 -liphlpapi -limagehlp -lshlwapi -lbcrypt", "DLDLIBS" => "",
      "LIBRUBYARG" => "$(LIBRUBYARG_SHARED)",
      # the directory itself, not $(bindir): mkmf takes the drive letter off a $(...) directory
      # (it becomes $(DESTDIR)), which leaves MSYS make a path under its own root
      "LIBRUBYARG_SHARED" => "-L\"#{CONFIG["bindir"]}\" -lironruby-capi",
      "LIBRUBYARG_STATIC" => "-L\"#{CONFIG["bindir"]}\" -lironruby-capi",
      "OUTFLAG" => "-o ", "COUTFLAG" => "-o ", "CPPOUTFILE" => "-o conftest.i",
      "LIBPATHFLAG" => " -L%s", "RPATHFLAG" => "", "EXPORT_PREFIX" => " ",
      "LINK_SO" => "", "POSTLINK" => ":", "TRY_LINK" => "",
      "INSTALL" => "/usr/bin/install -c", "INSTALL_PROG" => "$(INSTALL) -m 0755",
      "INSTALL_DATA" => "$(INSTALL) -m 644", "INSTALL_SCRIPT" => "$(INSTALL)",
      "MAKEDIRS" => "mkdir -p", "MKDIR_P" => "mkdir -p", "RM" => "rm -f",
      "RMDIR" => "rmdir --ignore-fail-on-non-empty", "RMALL" => "rm -fr", "CP" => "cp",
      "SHELL" => "/bin/sh", "NULLCMD" => ":", "build_os" => "mingw32",
      # gcc's, not MSVC's: mkmf's try_compile looks for conftest.$(OBJEXT)
      "OBJEXT" => "o", "LIBEXT" => "a",
    }.each { |key, value| CONFIG[key] = value.dup }
  end

  # the binutils and helpers configure found, by name: build scripts run them (mini_portile's
  # libxml2 build, extconf.rb's pkg-config lookups), and the triplets are complete only now
  {
    "NM" => "nm", "RANLIB" => "ranlib", "LD" => "ld", "OBJDUMP" => "objdump", "OBJCOPY" => "objcopy",
    "PKG_CONFIG" => "pkg-config", "GNU_LD" => "yes", "LN_S" => "ln -s", "GREP" => "grep", "EGREP" => "grep -E",
    "SOEXT" => windows ? "dll" : "so",
  }.each { |key, value| CONFIG[key] = value.dup }
  %w[host build target].each do |kind|
    CONFIG[kind] = "#{CONFIG["#{kind}_cpu"]}-#{CONFIG["#{kind}_vendor"]}-#{CONFIG["#{kind}_os"]}"
  end

  # Naming conventions rather than tools, so these have an answer even here.
  CONFIG["OBJEXT"] ||= windows ? "obj" : "o"
  CONFIG["LIBEXT"] ||= windows ? "lib" : "a"
  CONFIG["ASMEXT"] = "S"
  # There is no static libruby either; it must differ from LIBRUBY, which is how
  # mkmf decides whether the runtime is linked in.
  CONFIG["LIBRUBY_A"] = ""

  # In MRI this is the configuration as it went into the Makefile, with the
  # $(var) references still unexpanded; CONFIG is the expanded copy. IronRuby
  # never writes a Makefile, so nothing here holds a reference to expand and
  # the two hashes have the same contents - but they must not be the same
  # object, and mkmf mutates the values it is handed.
  MAKEFILE_CONFIG = {}
  CONFIG.each { |k, v| MAKEFILE_CONFIG[k] = v.dup }

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

  # As in MRI: updates +key+ in +mkconf+ with +val+, and every value of +conf+ that
  # depends on it. Returns the updated keys, or nil if nothing changed.
  def RbConfig.fire_update!(key, val, mkconf = MAKEFILE_CONFIG, conf = CONFIG) # :nodoc:
    return if mkconf[key] == val
    mkconf[key] = val
    keys = [key]
    deps = []
    begin
      re = Regexp.new("\\$\\((?:%1$s)\\)|\\$\\{(?:%1$s)\\}" % keys.join('|'))
      deps |= keys
      keys.clear
      mkconf.each {|k,v| keys << k if re =~ v}
    end until keys.empty?
    deps.each {|k| conf[k] = mkconf[k].dup}
    deps.each {|k| expand(conf[k])}
    deps
  end

  # returns the absolute pathname of the ruby command.
  def RbConfig.ruby
    File.join(RbConfig::CONFIG["bindir"], RbConfig::CONFIG["ruby_install_name"] + RbConfig::CONFIG["EXEEXT"])
  end
end
# Non-nil if configured for cross compiling.
CROSS_COMPILING = nil unless defined? CROSS_COMPILING
