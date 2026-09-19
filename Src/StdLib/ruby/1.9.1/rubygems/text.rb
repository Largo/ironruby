require 'rubygems'

##
# A collection of text-wrangling methods

module Gem::Text

  ##
  # Remove any non-printable characters and make the text suitable for
  # printing (backported from RubyGems 3.0.3, CVE-2019-8321/8323/8325).

  def clean_text(text)
    text = text.gsub(/[\000-\b\v-\f\016-\037\177]/, ".")

    # Match C1 control characters (U+0080-U+009F) as codepoints. This requires
    # a valid UTF-8 string so the regexp does not split a multibyte sequence;
    # strings in other encodings are left unchanged.
    if text.encoding == Encoding::UTF_8 && text.valid_encoding?
      text = text.gsub(/[\u0080-\u009f]/, ".")
    end

    text
  end

  ##
  # Wraps +text+ to +wrap+ characters and optionally indents by +indent+
  # characters

  def format_text(text, wrap, indent=0)
    result = []
    work = text.dup

    while work.length > wrap do
      if work =~ /^(.{0,#{wrap}})[ \n]/ then
        result << $1
        work.slice!(0, $&.length)
      else
        result << work.slice!(0, wrap)
      end
    end

    result << work if work.length.nonzero?
    result.join("\n").gsub(/^/, " " * indent)
  end

end

