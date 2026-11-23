# frozen_string_literal: true
#
# erb's C extension, provided by IronRuby itself.
#
# erb/util.rb starts with
#
#     begin
#       require 'erb/escape'
#     rescue LoadError
#       module ERB::Escape
#         def html_escape(s) = CGI.escapeHTML(s.to_s)  # (spelled out there)
#         module_function :html_escape
#       end
#     end
#
# and this file is what that require finds: ERB::Escape.html_escape in C#
# (Src/Libraries/Erb/ErbEscape.cs), what escape.c defines on CRuby. It hands back the String
# it was given when nothing in it needs escaping, as escape.c does, and it is what every
# <%= h(...) %> of a template and ERB::Util.html_escape run.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Erb'
