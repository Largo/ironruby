# frozen_string_literal: true
#
# puma's native half, provided by IronRuby itself - what `require "puma/puma_http11"` loads in
# place of the C extension ext/puma_http11 builds.
#
# `gem install puma` cannot work here: the extension is C (a Ragel-generated HTTP parser, and
# MiniSSL over OpenSSL's memory BIOs).  IronRuby ships puma's Ruby half unchanged
# (Src/StdLib/ironruby/puma.rb and puma/, from puma 8.0.2) and implements the parser in C# -
# Puma::HttpParser and Puma::HttpParserError, in Src/Libraries/Puma/PumaHttp11.cs, a port of the
# same state machine with the C extension's messages and limits.
#
# Not implemented:
#
#   * MiniSSL, puma's TLS.  Puma::MiniSSL::Engine is not defined, which is exactly how a puma
#     built without OpenSSL looks: Puma.ssl? is false, and binding an ssl:// URL fails with
#     puma's own "Puma compiled without SSL support" error.  Terminate TLS in front of puma.
#   * Cluster mode.  .NET cannot fork(2), so Process.respond_to?(:fork) is false and puma
#     refuses `workers N` with its own "worker mode not supported on ironruby on this
#     platform" message.  Single mode - one process, a thread pool - is fully supported.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Puma'
