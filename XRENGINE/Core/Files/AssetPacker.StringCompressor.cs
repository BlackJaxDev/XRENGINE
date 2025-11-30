using System.Collections.Concurrent;

namespace XREngine.Core.Files
{

public static partial class AssetPacker
    {
        // Optimized string compression with prefix and dictionary compression
        private class StringCompressor
        {
            private readonly List<string> _strings = [];
            private readonly Dictionary<string, int> _stringOffsets = [];
            private readonly Dictionary<string, int> _commonSubstrings = [];

            public long DictionaryOffset { get; private set; }

            public StringCompressor(IEnumerable<string> strings)
            {
                // Analyze strings for common patterns
                var substringFrequency = new ConcurrentDictionary<string, int>();

                Parallel.ForEach(strings, str =>
                {
                    // Split into path components
                    var parts = str.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

                    foreach (var part in parts)
                    {
                        substringFrequency.AddOrUpdate(part, 1, (_, count) => count + 1);
                    }
                });

                // Take top 64 most common substrings
                _commonSubstrings = substringFrequency
                    .OrderByDescending(kv => kv.Value)
                    .Take(64)
                    .Select((kv, i) => new { kv.Key, Index = i })
                    .ToDictionary(x => x.Key, x => x.Index);

                // Build string table with prefix compression
                var sortedStrings = strings.OrderBy(s => s).ToList();
                string prevString = "";

                foreach (var str in sortedStrings)
                {
                    int commonPrefix = 0;
                    while (commonPrefix < prevString.Length &&
                           commonPrefix < str.Length &&
                           prevString[commonPrefix] == str[commonPrefix])
                    {
                        commonPrefix++;
                    }

                    _strings.Add(str);
                    prevString = str;
                }
            }

            public StringCompressor(BinaryReader reader)
            {
                // Read from existing archive
                int commonSubstringCount = reader.ReadInt32();
                _commonSubstrings = new Dictionary<string, int>(commonSubstringCount);

                for (int i = 0; i < commonSubstringCount; i++)
                {
                    int length = reader.ReadByte();
                    byte[] bytes = reader.ReadBytes(length);
                    string substring = StringEncoding.GetString(bytes);
                    _commonSubstrings[substring] = i;
                }

                // Read string table
                int stringCount = reader.ReadInt32();
                _strings = new List<string>(stringCount);
                _stringOffsets = new Dictionary<string, int>(stringCount);

                for (int i = 0; i < stringCount; i++)
                {
                    int flags = reader.ReadByte();
                    string str;

                    if ((flags & 0x80) != 0)
                    {
                        // Compressed string
                        int prefixLength = reader.ReadByte();
                        string prefix = _strings[i - 1][..prefixLength];

                        var parts = new List<string>();
                        while (true)
                        {
                            byte partType = reader.ReadByte();
                            if (partType == 0xFF) break;

                            if ((partType & 0x80) != 0)
                            {
                                // Common substring reference
                                int index = partType & 0x7F;
                                string common = _commonSubstrings.First(kv => kv.Value == index).Key;
                                parts.Add(common);
                            }
                            else
                            {
                                // Literal string
                                int length = partType;
                                byte[] bytes = reader.ReadBytes(length);
                                parts.Add(StringEncoding.GetString(bytes));
                            }
                        }

                        str = prefix + string.Join("", parts);
                    }
                    else
                    {
                        // Full string
                        int length = reader.ReadUInt16();
                        byte[] bytes = reader.ReadBytes(length);
                        str = StringEncoding.GetString(bytes);
                    }

                    _strings.Add(str);
                    _stringOffsets[str] = i;
                }
            }

            public byte[] BuildStringTable()
            {
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                // Write common substring dictionary
                writer.Write(_commonSubstrings.Count);
                foreach (var kv in _commonSubstrings.OrderBy(kv => kv.Value))
                {
                    byte[] bytes = StringEncoding.GetBytes(kv.Key);
                    writer.Write((byte)bytes.Length);
                    writer.Write(bytes);
                }

                // Write string table
                writer.Write(_strings.Count);
                string prevString = "";

                foreach (string str in _strings)
                {
                    int commonPrefix = GetCommonPrefixLength(prevString, str);
                    bool canCompress = commonPrefix > 3 && (str.Length - commonPrefix) > 0;

                    if (canCompress)
                    {
                        // Compressed format: [1 bit flag + 7 bit prefix length][parts...][0xFF terminator]
                        writer.Write((byte)(0x80 | (commonPrefix & 0x7F)));
                        writer.Write((byte)commonPrefix);

                        string remaining = str[commonPrefix..];
                        foreach (var part in CompressStringParts(remaining))
                        {
                            if (_commonSubstrings.TryGetValue(part, out int index))
                            {
                                // Common substring reference
                                writer.Write((byte)(0x80 | index));
                            }
                            else
                            {
                                // Literal string
                                byte[] bytes = StringEncoding.GetBytes(part);
                                writer.Write((byte)bytes.Length);
                                writer.Write(bytes);
                            }
                        }

                        writer.Write((byte)0xFF); // End marker
                    }
                    else
                    {
                        // Uncompressed format: [0 bit flag][16-bit length][string bytes]
                        byte[] bytes = StringEncoding.GetBytes(str);
                        writer.Write((byte)0);
                        writer.Write((ushort)bytes.Length);
                        writer.Write(bytes);
                    }

                    prevString = str;
                }

                DictionaryOffset = 4 + _commonSubstrings.Sum(kv => 1 + StringEncoding.GetByteCount(kv.Key));
                return ms.ToArray();
            }

            public int GetStringOffset(string str)
                => _stringOffsets[str];

            public string GetString(int index)
                => _strings[index];

            private List<string> CompressStringParts(string input)
            {
                // Try to find the longest common substrings first
                var result = new List<string>();
                int pos = 0;

                while (pos < input.Length)
                {
                    int bestLength = 0;
                    string? bestMatch = null;

                    foreach (var substr in _commonSubstrings.Keys)
                    {
                        if (input.Length - pos >= substr.Length &&
                            string.CompareOrdinal(input, pos, substr, 0, substr.Length) == 0)
                        {
                            if (substr.Length > bestLength)
                            {
                                bestLength = substr.Length;
                                bestMatch = substr;
                            }
                        }
                    }

                    if (bestMatch != null)
                    {
                        if (pos < input.Length - bestLength)
                        {
                            string literal = input[pos..^bestLength];
                            if (!string.IsNullOrEmpty(literal))
                            {
                                result.Add(literal);
                            }
                        }
                        result.Add(bestMatch);
                        pos += bestLength;
                    }
                    else
                    {
                        result.Add(input[pos..]);
                        break;
                    }
                }

                return result;
            }

            private static int GetCommonPrefixLength(string a, string b)
            {
                int len = 0;
                int max = Math.Min(a.Length, b.Length);
                while (len < max && a[len] == b[len]) len++;
                return len;
            }
        }
    }
}
