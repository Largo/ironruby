# The catalog of CRuby default and bundled gems the harness knows about.
#
# Each entry says what to require, where a test suite for it lives, and whether
# IronRuby implements it itself (in which case the gem's lib/ must stay off
# $LOAD_PATH - it is only a shim over a C extension that does not exist here).
#
#   :require  - the feature name, or nil to skip the load check
#   :tests    - test/unit or minitest files: globs under the CRuby source tree
#               (RUBY_SRC), or absolute globs
#   :specs    - a directory under spec/library, run with mspec when there is no
#               :tests suite. The gem's own lib/ is on the path for it, so this
#               exercises the modern gem and not IronRuby's 1.9 copy.
#   :native   - IronRuby supplies this library; do not put the gem's lib/ first
#   :note     - what is worth saying about it in the table
#
# The test sources come from two places on disk:
#   GEM_DIR  - installed gems, for lib/ and for the few gems that ship test/
#   RUBY_SRC - an unpacked CRuby source tree, for test/ and tool/lib
# Nothing is downloaded. A library with neither suite is reported "load only",
# which is the honest answer rather than a fake pass.

module GemCatalog
  GEM_DIR = ENV['GEM_DIR'] || '/usr/local/lib/ruby/gems/4.0.0/gems'
  RUBY_SRC = ENV['RUBY_SRC'] || '/root/workspace/cosmoruby-upgrade/ruby-4.0.6'

  ENTRIES = {
    # --- pure-Ruby default gems whose tests live in the CRuby tree ------------
    'tsort'        => { require: 'tsort',        tests: %w[test/test_tsort.rb] },
    'weakref'      => { require: 'weakref',      tests: %w[test/test_weakref.rb] },
    'fileutils'    => { require: 'fileutils',    tests: %w[test/fileutils/*.rb] },
    'tmpdir'       => { require: 'tmpdir',       tests: %w[test/test_tmpdir.rb] },
    'tempfile'     => { require: 'tempfile',     tests: %w[test/test_tempfile.rb] },
    'open3'        => { require: 'open3',        tests: %w[test/test_open3.rb] },
    'shellwords'   => { require: 'shellwords',   tests: %w[test/test_shellwords.rb] },
    'securerandom' => { require: 'securerandom', tests: %w[test/test_securerandom.rb] },
    'singleton'    => { require: 'singleton',    tests: %w[test/test_singleton.rb] },
    'forwardable'  => { require: 'forwardable',  tests: %w[test/test_forwardable.rb] },
    'delegate'     => { require: 'delegate',     tests: %w[test/test_delegate.rb] },
    'timeout'      => { require: 'timeout',      tests: %w[test/test_timeout.rb],
                        note: 'hangs: Thread#raise does not interrupt a running loop' },
    'find'         => { require: 'find',         tests: %w[test/test_find.rb] },
    'ipaddr'       => { require: 'ipaddr',       tests: %w[test/test_ipaddr.rb] },
    'pp'           => { require: 'pp',           tests: %w[test/test_pp.rb] },
    'prettyprint'  => { require: 'prettyprint',  tests: %w[test/test_prettyprint.rb] },
    'time'         => { require: 'time',         tests: %w[test/test_time.rb] },
    'optparse'     => { require: 'optparse',     tests: %w[test/optparse/*.rb] },
    'uri'          => { require: 'uri',          tests: %w[test/uri/*.rb] },
    'erb'          => { require: 'erb',          tests: %w[test/erb/*.rb] },
    'resolv'       => { require: 'resolv',       tests: %w[test/resolv/*.rb] },
    'open-uri'     => { require: 'open-uri',     tests: %w[test/open-uri/*.rb] },
    'monitor'      => { require: 'monitor',      tests: %w[test/monitor/*.rb] },
    'pathname'     => { require: 'pathname',     tests: %w[test/pathname/*.rb] },

    'net-http'     => { require: 'net/http',     tests: %w[test/net/http/*.rb],
                        note: 'spawns servers; slow' },
    'net-imap'     => { require: 'net/imap',     specs: 'net-imap' },
    'net-smtp'     => { require: 'net/smtp' },
    'net-pop'      => { require: 'net/pop' },
    'net-ftp'      => { require: 'net/ftp',      specs: 'net-ftp' },

    # --- libraries IronRuby implements itself (C extensions in MRI) ----------
    'digest'       => { require: 'digest',   native: true, tests: %w[test/digest/*.rb] },
    'stringio'     => { require: 'stringio', native: true, tests: %w[test/stringio/*.rb] },
    'strscan'      => { require: 'strscan',  native: true, tests: %w[test/strscan/*.rb] },
    'zlib'         => { require: 'zlib',     native: true, tests: %w[test/zlib/*.rb],
                        note: 'the suite aborts the process partway through' },
    'etc'          => { require: 'etc',      native: true, tests: %w[test/etc/*.rb] },
    'fcntl'        => { require: 'fcntl',    native: true },
    'json'         => { require: 'json',     native: true, tests: %w[test/json/*.rb] },
    'psych'        => { require: 'psych',    native: true, tests: %w[test/psych/*.rb test/psych/*/*.rb] },
    'date'         => { require: 'date',     native: true, tests: %w[test/date/*.rb],
                        note: 'the suite overflows the stack partway through' },
    'bigdecimal'   => { require: 'bigdecimal', native: true, specs: 'bigdecimal' },
    'syslog'       => { require: 'syslog',   native: true, specs: 'syslog',
                        note: "IronRuby's own; the gem's lib/ only loads a .so" },
    'nkf'          => { require: 'nkf',      native: true,
                        note: "IronRuby's own, on String#encode" },
    'objspace'     => { require: 'objspace', native: true, specs: 'objectspace' },

    # --- pure-Ruby gems whose own suite is not on disk -----------------------
    'rake'         => { require: 'rake' },
    'rexml'        => { require: 'rexml/document' },
    'rss'          => { require: 'rss' },
    'csv'          => { require: 'csv',        specs: 'csv' },
    'matrix'       => { require: 'matrix',     specs: 'matrix' },
    'prime'        => { require: 'prime',      specs: 'prime' },
    'ostruct'      => { require: 'ostruct',    specs: 'openstruct' },
    'pstore'       => { require: 'pstore' },
    'observer'     => { require: 'observer',   specs: 'observer' },
    'abbrev'       => { require: 'abbrev',     specs: 'abbrev' },
    'drb'          => { require: 'drb',        specs: 'drb' },
    'racc'         => { require: 'racc/parser' },
    'getoptlong'   => { require: 'getoptlong', specs: 'getoptlong' },
    'rinda'        => { require: 'rinda/rinda' },
    'resolv-replace' => { require: 'resolv-replace' },
    'benchmark'    => { require: 'benchmark' },
    'logger'       => { require: 'logger',     specs: 'logger' },
    'base64'       => { require: 'base64',     specs: 'base64' },
    'english'      => { require: 'English',    specs: 'English' },
    'mutex_m'      => { require: 'mutex_m' },

    # --- gems that ship their own tests --------------------------------------
    'minitest'     => { require: 'minitest/autorun', framework: :minitest,
                        tests: ["#{GEM_DIR}/minitest-*/test/**/*_test.rb"] },

    # --- deliberately not run ------------------------------------------------
    'test-unit'    => { require: 'test/unit', isolated: true,
                        note: "shadows CRuby's tool/lib/test/unit, so only loaded" },
    'power_assert' => { require: 'power_assert',
                        note: 'needs RubyVM::InstructionSequence' },
  }

  # The pure-Ruby default/bundled gems that go on $LOAD_PATH for every run, so
  # that a gem's dependencies (net-protocol, prettyprint, ...) resolve to the
  # modern copy rather than IronRuby's 1.9 tree.  Native gems are excluded:
  # their lib/ is a stub that only requires a .so.  test-unit is excluded too -
  # its lib/test/unit.rb would shadow the one the suites are run with.
  PATH_GEMS = %w[
    abbrev base64 benchmark csv delegate drb english erb find fileutils
    forwardable getoptlong ipaddr logger matrix mutex_m net-ftp net-http
    net-imap net-pop net-protocol net-smtp observer open3 open-uri optparse
    ostruct prettyprint prime pp pstore racc rake resolv resolv-replace rexml
    rinda rss securerandom shellwords singleton time timeout tmpdir tsort uri
    weakref
  ]

  # One gem is installed under several versions (bigdecimal 3 and 4, rake 13.3
  # and 13.4, json 2 and 3); take the highest, and skip the platform-specific
  # builds, whose lib/ is a stub for a .so.
  def self.gem_lib(name)
    Dir["#{GEM_DIR}/#{name}-*/lib"].
      map { |d| [File.basename(File.dirname(d)).sub(/\A#{Regexp.escape(name)}-/, ''), d] }.
      reject { |v, _| v !~ /\A\d/ || v =~ /-/ }.
      max_by { |v, _| v.split('.').map(&:to_i) }&.last
  end

  def self.load_path(name, entry)
    return [] if entry[:native]
    ([gem_lib(name)] + PATH_GEMS.map { |g| gem_lib(g) }).compact.uniq
  end

  def self.test_files(entry)
    (entry[:tests] || []).flat_map { |g|
      g.start_with?('/') ? Dir[g] : Dir[File.join(RUBY_SRC, g)]
    }.sort
  end
end
