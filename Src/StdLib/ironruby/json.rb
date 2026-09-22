# MRI implements json as a C extension. IronRuby provides it in C# instead
# (Src/Libraries/Json), the same way it provides Digest, Zlib and StringIO.
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Json'

module JSON
  # IronRuby implements the JSON 2.x API in C#; this is the version of that
  # API, not of upstream's C extension.
  VERSION = "2.18.0"
  VERSION_ARRAY = VERSION.split(".").map {|x| x.to_i } # :nodoc:
  VERSION_MAJOR = VERSION_ARRAY[0] # :nodoc:
  VERSION_MINOR = VERSION_ARRAY[1] # :nodoc:
  VERSION_BUILD = VERSION_ARRAY[2] # :nodoc:

  # Convenience wrappers the C# module does not need to provide itself.
  def self.pretty_unparse(obj, opts = nil); pretty_generate(obj, opts); end
  def self.unparse(obj, opts = nil); generate(obj, opts); end

  # MRI declares these with module_function, so "include JSON" hands them on as
  # private instance methods - which is how JSON's own test suite calls parse().
  # The C# side can only declare singleton methods, so the instance half is here.
  %i[parse load generate dump pretty_generate unparse pretty_unparse].each do |name|
    define_method(name) { |*args, &block| JSON.send(name, *args, &block) }
    private name
  end
end

class Object
  def to_json(*args); JSON.generate(self, *args); end unless method_defined?(:to_json)
end
