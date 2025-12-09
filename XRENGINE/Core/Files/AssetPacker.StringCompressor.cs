using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace XREngine.Core.Files
{
    public static partial class AssetPacker
    {
        // Optimized string compression with prefix and dictionary compression
        private class StringCompressor
        {
            private List<string> _strings = [];
            private Dictionary<string, int> _stringOffsets = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _commonSubstrings = new(StringComparer.Ordinal);
            private readonly List<string> _substringByIndex = [];

            // Offset relative to the beginning of the serialized string table data.
            public long DictionaryOffset { get; private set; }

            public StringCompressor(IEnumerable<string> strings)
            {
                DictionaryOffset = 0;
                BuildUncompressedTable(strings);
            }

            public StringCompressor(CookedBinaryReader reader)
            {
                DictionaryOffset = 0;

                int commonSubstringCount = reader.ReadInt32();
                _commonSubstrings.Clear();
                _substringByIndex.Clear();
                for (int i = 0; i < commonSubstringCount; i++)
                {
                    int length = reader.ReadByte();
                    string substring = StringEncoding.GetString(reader.ReadBytes(length));
                    _commonSubstrings[substring] = i;
                    _substringByIndex.Add(substring);
                }

                int stringCount = reader.ReadInt32();
                _strings.Clear();
                _stringOffsets.Clear();
                _strings.Capacity = Math.Max(_strings.Capacity, stringCount);

                for (int i = 0; i < stringCount; i++)
                {
                    int flags = reader.ReadByte();
                    string value;

                    if ((flags & 0x80) != 0)
                    {
                        int prefixLength = reader.ReadByte();
                        string prefix = _strings[i - 1][..prefixLength];
                        var builder = new StringBuilder();
                        while (true)
                        {
                            byte partType = reader.ReadByte();
                            if (partType == 0xFF)
                                break;

                            if ((partType & 0x80) != 0)
                            {
                                int index = partType & 0x7F;
                                builder.Append(index < _substringByIndex.Count ? _substringByIndex[index] : string.Empty);
                            }
                            else
                            {
                                int length = partType;
                                builder.Append(StringEncoding.GetString(reader.ReadBytes(length)));
                            }
                        }

                        value = prefix + builder.ToString();
                    }
                    else
                    {
                        int length = reader.ReadUInt16();
                        value = StringEncoding.GetString(reader.ReadBytes(length));
                    }

                    _strings.Add(value);
                    _stringOffsets[value] = i;
                }
            }

            private void BuildUncompressedTable(IEnumerable<string> strings)
            {
                _strings.Clear();
                _stringOffsets.Clear();
                _commonSubstrings.Clear();
                _substringByIndex.Clear();

                var sorted = strings.OrderBy(static s => s, StringComparer.Ordinal).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    string value = sorted[i];
                    _strings.Add(value);
                    _stringOffsets[value] = i;
                }
            }

            public byte[] BuildStringTable()
            {
                BufferBuilder builder = new();

                builder.WriteInt32(_commonSubstrings.Count);
                if (_commonSubstrings.Count > 0)
                {
                    foreach (var kv in _commonSubstrings.OrderBy(static kv => kv.Value))
                    {
                        byte[] bytes = StringEncoding.GetBytes(kv.Key);
                        builder.WriteByte((byte)bytes.Length);
                        builder.WriteBytes(bytes);
                    }
                }

                builder.WriteInt32(_strings.Count);
                foreach (string str in _strings)
                {
                    byte[] bytes = StringEncoding.GetBytes(str);
                    if (bytes.Length > ushort.MaxValue)
                        throw new InvalidOperationException($"String '{str}' exceeds maximum supported length.");
                    builder.WriteByte(0);
                    builder.WriteUInt16((ushort)bytes.Length);
                    builder.WriteBytes(bytes);
                }

                return builder.ToArray();
            }

            public int GetStringOffset(string str)
                => _stringOffsets[str];

            public string GetString(int index)
                => _strings[index];

            private sealed class BufferBuilder
            {
                private readonly ArrayBufferWriter<byte> _buffer = new();

                public void WriteByte(byte value)
                {
                    Span<byte> span = _buffer.GetSpan(1);
                    span[0] = value;
                    _buffer.Advance(1);
                }

                public void WriteBytes(ReadOnlySpan<byte> data)
                {
                    Span<byte> span = _buffer.GetSpan(data.Length);
                    data.CopyTo(span);
                    _buffer.Advance(data.Length);
                }

                public void WriteInt32(int value)
                {
                    Span<byte> span = _buffer.GetSpan(sizeof(int));
                    BinaryPrimitives.WriteInt32LittleEndian(span, value);
                    _buffer.Advance(sizeof(int));
                }

                public void WriteUInt16(ushort value)
                {
                    Span<byte> span = _buffer.GetSpan(sizeof(ushort));
                    BinaryPrimitives.WriteUInt16LittleEndian(span, value);
                    _buffer.Advance(sizeof(ushort));
                }

                public byte[] ToArray()
                    => _buffer.WrittenSpan.ToArray();
            }
        }
    }
}
