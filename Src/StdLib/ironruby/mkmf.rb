# frozen_string_literal: false
#
# CRuby's mkmf (Src/StdLib/ruby/4.0/mkmf.rb), loaded as it is, with one IronRuby adjustment.
#
# IronRuby on Windows is x64-mswin64 - RUBY_PLATFORM and RbConfig's host_os say so - but it
# builds C extensions with MinGW gcc and MSYS make, as RubyInstaller's CRuby does
# (rbconfig.rb, Src/Ruby/CApi). mkmf reads the platform from RUBY_PLATFORM and would apply
# MSVC's conventions - nmake, cmd.exe's `exit /b0`, splitting libraries on every space - to a
# MinGW build, so on Windows it is told it is on mingw.
load File.join(RbConfig::CONFIG["rubylibdir"], "mkmf.rb")

if RUBY_PLATFORM.include?("mswin") && RbConfig::CONFIG["build_os"] == "mingw32"
  $mswin = nil
  $mingw = 0
  $nmake = nil
  $ignore_error = " 2> #{File::NULL} || true"
end
