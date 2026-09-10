# REPLACED in this fork. The upstream RFC3986 parser builds its regexp from
# \g<name> subexpression calls and possessive quantifiers; .NET's regex engine
# has neither, so the original pattern cannot be compiled here and raised at
# load time, taking the whole uri library with it.
#
# RFC2396_Parser implements the same interface with a plain regexp and was
# Ruby's default parser for years. The practical difference is weaker
# validation of exotic authorities such as bracketed IPv6 literals and
# IPvFuture. uri/common.rb reaches this file through require_relative, so a
# shim elsewhere on the load path cannot shadow it.
require_relative "rfc2396_parser"

module URI
  unless defined?(RFC3986_Parser)
    class RFC3986_Parser < RFC2396_Parser
      def inspect
        "#<URI::RFC3986_Parser (RFC2396-backed: .NET regexes have no \\g)>"
      end
    end
  end
end
