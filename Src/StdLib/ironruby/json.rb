# MRI implements json as a C extension. IronRuby provides it in C# instead
# (Src/Libraries/Json), the same way it provides Digest, Zlib and StringIO.
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Json'

module JSON
  # Convenience wrappers the C# module does not need to provide itself.
  def self.pretty_unparse(obj); pretty_generate(obj); end
  def self.unparse(obj); generate(obj); end

  # MRI declares these with module_function, so "include JSON" hands them on as
  # private instance methods - which is how JSON's own test suite calls parse().
  # The C# side can only declare singleton methods, so the instance half is here.
  %i[parse load generate dump pretty_generate unparse pretty_unparse].each do |name|
    define_method(name) { |*args, &block| JSON.send(name, *args, &block) }
    private name
  end
end

class Object
  def to_json(*); JSON.generate(self); end unless method_defined?(:to_json)
end
