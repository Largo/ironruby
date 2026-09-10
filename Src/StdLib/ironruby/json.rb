# MRI implements json as a C extension. IronRuby provides it in C# instead
# (Src/Libraries/Json), the same way it provides Digest, Zlib and StringIO.
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Json'

module JSON
  # Convenience wrappers the C# module does not need to provide itself.
  def self.pretty_unparse(obj); pretty_generate(obj); end
  def self.unparse(obj); generate(obj); end
end

class Object
  def to_json(*); JSON.generate(self); end unless method_defined?(:to_json)
end
