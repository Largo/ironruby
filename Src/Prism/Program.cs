using System;
using System.IO;
using System.Text.Json;

namespace IronRuby.Prism {
    internal static class Program {
        private static int Main(string[] args) {
            if (args.Length == 2 && args[0] == "--run") {
                // execute a Ruby file with prism as the front end (legacy parser bypassed)
                IronRuby.Runtime.RubyContext.AlternativeParser = PrismAstBridge.Parse;
                var engine = IronRuby.Ruby.CreateEngine();
                engine.Execute(File.ReadAllText(args[1]));
                return 0;
            }

            if (args.Length == 2 && args[0] == "--json") {
                Console.WriteLine(PrismParser.ParseToJson(File.ReadAllText(args[1])));
                return 0;
            }

            string source = args.Length > 0 ? File.ReadAllText(args[0]) : "puts 1 + 2\n[1, 2, 3].each { |x| puts x * 2 }\n";

            byte[] serialized = PrismParser.ParseSerialized(source);
            string magic = System.Text.Encoding.ASCII.GetString(serialized, 0, 5);
            Console.WriteLine($"serialized: {serialized.Length} bytes, magic={magic}, version={serialized[5]}.{serialized[6]}.{serialized[7]}");

            string json = PrismParser.ParseToJson(source);
            using var doc = JsonDocument.Parse(json);
            int nodes = CountNodes(doc.RootElement);
            Console.WriteLine($"json AST: {json.Length} chars, {nodes} nodes, root type = {doc.RootElement.GetProperty("type").GetString()}");
            DumpTypes(doc.RootElement, 0);
            return 0;
        }

        private static int CountNodes(JsonElement element) {
            int count = 0;
            if (element.ValueKind == JsonValueKind.Object) {
                if (element.TryGetProperty("type", out _)) count++;
                foreach (var prop in element.EnumerateObject()) count += CountNodes(prop.Value);
            } else if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray()) count += CountNodes(item);
            }
            return count;
        }

        private static void DumpTypes(JsonElement element, int depth) {
            if (depth > 6) return;
            if (element.ValueKind == JsonValueKind.Object) {
                if (element.TryGetProperty("type", out var type)) {
                    Console.WriteLine(new string(' ', depth * 2) + type.GetString());
                    depth++;
                }
                foreach (var prop in element.EnumerateObject()) DumpTypes(prop.Value, depth);
            } else if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray()) DumpTypes(item, depth);
            }
        }
    }
}
