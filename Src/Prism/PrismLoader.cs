using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using IronRuby.Prism.Ast;

namespace IronRuby.Prism {
    public struct PrismError {
        public string Message;
        public PmLocation Location;
        public byte Level; // 0 fatal, 1 argument, 2 load
    }

    public struct PrismWarning {
        public string Message;
        public PmLocation Location;
        public byte Level; // 0 default, 1 verbose
    }

    public sealed class PrismParseResult {
        public PmNode Root;
        public string EncodingName;
        public int StartLine;
        public List<PrismError> Errors;
        public List<PrismWarning> Warnings;
        public PmLocation? DataLocation; // __END__
    }

    /// <summary>
    /// Deserializer for prism's binary AST format (docs/serialization.md). The
    /// per-node loading switch lives in PrismLoader.Generated.cs, produced by
    /// generate.rb from prism's config.yml — the same scheme JRuby uses.
    /// </summary>
    public sealed partial class PrismLoader {
        private readonly byte[]/*!*/ _buffer;
        private int _pos;
        private uint _cpoolBase;
        private string[] _constants;

        private PrismLoader(byte[]/*!*/ buffer) {
            _buffer = buffer;
        }

        public static PrismParseResult/*!*/ LoadParse(byte[]/*!*/ serialized) {
            var loader = new PrismLoader(serialized);
            return loader.Load();
        }

        private PrismParseResult/*!*/ Load() {
            if (_buffer.Length < 8 || _buffer[0] != 'P' || _buffer[1] != 'R' || _buffer[2] != 'I' || _buffer[3] != 'S' || _buffer[4] != 'M') {
                throw new InvalidOperationException("Invalid prism serialization (bad magic)");
            }
            if (_buffer[5] != SerializationMajor || _buffer[6] != SerializationMinor) {
                throw new InvalidOperationException(
                    $"prism serialization version mismatch: libprism {_buffer[5]}.{_buffer[6]}.{_buffer[7]}, generated loader {SerializationMajor}.{SerializationMinor}.{SerializationPatch} — re-run generate.rb");
            }
            _pos = 8;
            if (LoadByte() != 0) {
                throw new InvalidOperationException("prism serialization must include location fields");
            }

            var result = new PrismParseResult();
            result.EncodingName = LoadString();
            result.StartLine = LoadVarSInt();

            uint newlineCount = LoadVarUInt();
            for (uint i = 0; i < newlineCount; i++) LoadVarUInt();

            uint commentCount = LoadVarUInt();
            for (uint i = 0; i < commentCount; i++) { LoadByte(); LoadLocation(); }

            uint magicCommentCount = LoadVarUInt();
            for (uint i = 0; i < magicCommentCount; i++) { LoadLocation(); LoadLocation(); }

            result.DataLocation = LoadOptionalLocation();

            result.Errors = new List<PrismError>();
            uint errorCount = LoadVarUInt();
            for (uint i = 0; i < errorCount; i++) {
                LoadVarUInt(); // error type
                var error = new PrismError { Message = LoadString(), Location = LoadLocation(), Level = LoadByte() };
                result.Errors.Add(error);
            }

            result.Warnings = new List<PrismWarning>();
            uint warningCount = LoadVarUInt();
            for (uint i = 0; i < warningCount; i++) {
                LoadVarUInt(); // warning type
                var warning = new PrismWarning { Message = LoadString(), Location = LoadLocation(), Level = LoadByte() };
                result.Warnings.Add(warning);
            }

            LoadByte(); // continuable
            _cpoolBase = LoadRawUInt32();
            uint cpoolSize = LoadVarUInt();
            _constants = new string[cpoolSize];

            result.Root = LoadNode();
            return result;
        }

        // ---- primitive readers ----

        private byte LoadByte() {
            return _buffer[_pos++];
        }

        private uint LoadVarUInt() {
            uint result = 0;
            int shift = 0;
            while (true) {
                byte b = _buffer[_pos++];
                result |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }

        private int LoadVarSInt() {
            uint encoded = LoadVarUInt();
            return (int)(encoded >> 1) ^ -(int)(encoded & 1);
        }

        private uint LoadRawUInt32() {
            uint value = BitConverter.ToUInt32(_buffer, _pos);
            _pos += 4;
            return value;
        }

        private string/*!*/ LoadString() {
            int length = (int)LoadVarUInt();
            string result = Encoding.UTF8.GetString(_buffer, _pos, length);
            _pos += length;
            return result;
        }

        private PmLocation LoadLocation() {
            return new PmLocation((int)LoadVarUInt(), (int)LoadVarUInt());
        }

        private PmLocation? LoadOptionalLocation() {
            if (LoadByte() == 0) return null;
            return LoadLocation();
        }

        private PmNode LoadOptionalNode() {
            if (_buffer[_pos] == 0) {
                _pos++;
                return null;
            }
            return LoadNode();
        }

        private PmNode/*!*/[]/*!*/ LoadNodeList() {
            var nodes = new PmNode[LoadVarUInt()];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = LoadNode();
            return nodes;
        }

        private string/*!*/ LoadConstant() {
            return GetConstant(LoadVarUInt());
        }

        private string LoadOptionalConstant() {
            uint index = LoadVarUInt();
            return index == 0 ? null : GetConstant(index);
        }

        private string/*!*/[]/*!*/ LoadConstantList() {
            var constants = new string[LoadVarUInt()];
            for (int i = 0; i < constants.Length; i++) constants[i] = LoadConstant();
            return constants;
        }

        private string/*!*/ GetConstant(uint oneBasedIndex) {
            int index = (int)oneBasedIndex - 1;
            string result = _constants[index];
            if (result == null) {
                int entry = (int)_cpoolBase + index * 8;
                int start = (int)BitConverter.ToUInt32(_buffer, entry);
                int length = (int)BitConverter.ToUInt32(_buffer, entry + 4);
                result = _constants[index] = Encoding.UTF8.GetString(_buffer, start, length);
            }
            return result;
        }

        private BigInteger LoadInteger() {
            bool negative = LoadByte() != 0;
            uint words = LoadVarUInt();
            BigInteger value = BigInteger.Zero;
            for (int i = 0; i < words; i++) {
                value |= new BigInteger(LoadVarUInt()) << (i * 32);
            }
            return negative ? -value : value;
        }

        private double LoadDouble() {
            double value = BitConverter.ToDouble(_buffer, _pos);
            _pos += 8;
            return value;
        }
    }
}
