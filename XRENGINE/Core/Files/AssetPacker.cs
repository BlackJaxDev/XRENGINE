using System.Collections.Concurrent;
using System.Text;
using XREngine.Data;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Asset packer for compressing and storing multiple files in a single archive.
    /// Each file is stored with a key set to its path relative to the root input directory and is compressed using LZMA compression.
    /// </summary>
    public static class AssetPacker
    {
        private const int HashSize = 4; // Using 32-bit hash for path lookup
        private const int DataOffsetSize = 8; // 64-bit offset to data
        private const int CompressedSizeSize = 4; // Compressed data size
        private const int Magic = 0x4652454B; // "FREK"
        private static readonly Encoding StringEncoding = Encoding.UTF8;
        private const int Version = 1;

        private struct AssetFile
        {
            public string Path;
            public byte[] Data;
        }

        private struct TocEntry
        {
            public string Path;
            public long DataOffset;
            public int CompressedSize;
        }
        /// <summary>
        /// Repacks an existing archive with new files and optional removal of existing assets.
        /// </summary>
        /// <param name="archiveFilePath"></param>
        /// <param name="inputDirToAddFiles"></param>
        /// <param name="assetPathsToRemove"></param>
        public static void Repack(string archiveFilePath, string inputDirToAddFiles, params string[] assetPathsToRemove)
        {
            using var stream = new FileStream(archiveFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream);
            using var writer = new BinaryWriter(stream);
            if (reader.ReadInt32() != Magic)
                throw new Exception("Invalid file format");
            switch (reader.ReadInt32())
            {
                case Version:
                    RepackV1(stream, reader, writer, inputDirToAddFiles, assetPathsToRemove);
                    break;
                default:
                    throw new Exception("Unsupported archive version");
            }
        }

        /// <summary>
        /// Repacks an archive in version 1 format.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="reader"></param>
        /// <param name="inputDirToAddFiles"></param>
        /// <param name="assetPathsToRemove"></param>
        /// <exception cref="NotImplementedException"></exception>
        private static void RepackV1(FileStream stream, BinaryReader reader, BinaryWriter writer, string inputDirToAddFiles, string[] assetPathsToRemove)
        {
            int fileCount = reader.ReadInt32();

            // Read footer to find metadata locations
            stream.Seek(-24, SeekOrigin.End); // 3 * sizeof(long) = 24
            long tocPosition = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long dictionaryOffset = reader.ReadInt64();

            stream.Seek(dictionaryOffset, SeekOrigin.Begin);
            var stringCompressor = new StringCompressor(reader);
            stream.Seek(tocPosition, SeekOrigin.Begin);
            var tocEntries = new List<TocEntry>();
            for (int i = 0; i < fileCount; i++)
            {
                uint hash = reader.ReadUInt32();
                int stringOffset = reader.ReadInt32();
                long dataOffset = reader.ReadInt64();
                int compressedSize = reader.ReadInt32();
                string path = stringCompressor.GetString(stringOffset);
                if (!assetPathsToRemove.Contains(path))
                {
                    tocEntries.Add(new TocEntry
                    {
                        Path = path,
                        DataOffset = dataOffset,
                        CompressedSize = compressedSize,
                    });
                }
            }

            // Add new files
            var newFiles = Directory.GetFiles(inputDirToAddFiles, "*", SearchOption.AllDirectories)
                .Select(filePath => new AssetFile
                {
                    Path = Path.GetRelativePath(inputDirToAddFiles, filePath),
                    Data = File.ReadAllBytes(filePath)
                })
                .ToList();

            long currentOffset = stream.Length;
            foreach (var file in newFiles)
            {
                byte[] compressedData = Compression.Compress(file.Data, true);
                stream.Seek(currentOffset, SeekOrigin.Begin);
                stream.Write(compressedData, 0, compressedData.Length);
                tocEntries.Add(new TocEntry
                {
                    Path = file.Path,
                    DataOffset = currentOffset,
                    CompressedSize = compressedData.Length,
                });
                currentOffset += compressedData.Length;
            }
            // Rebuild string table
            long stringTableOffsetNew = WriteStringTable(stream, writer, stringCompressor);

            // Write new TOC
            stream.Seek(tocPosition, SeekOrigin.Begin);
            foreach (var entry in tocEntries)
            {
                writer.Write(FastHash(entry.Path));
                writer.Write(stringCompressor.GetStringOffset(entry.Path));
                writer.Write(entry.DataOffset);
                writer.Write(entry.CompressedSize);
            }
            // Write new footer
            WriteFooter(writer, tocPosition, stringCompressor, stringTableOffsetNew);
        }

        /// <summary>
        /// Packs a directory of files into a single archive.
        /// </summary>
        /// <param name="inputDir"></param>
        /// <param name="outputFile"></param>
        public static void Pack(string inputDir, string outputFile)
        {
            var files = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories)
                .Select(filePath => new AssetFile
                {
                    Path = Path.GetRelativePath(inputDir, filePath),
                    Data = File.ReadAllBytes(filePath)
                })
                .ToList();

            using var stream = new FileStream(outputFile, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            // Write header
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(files.Count);

            // Prepare space for TOC (will fill later)
            long tocPosition = stream.Position;
            int tocEntrySize = HashSize + DataOffsetSize + CompressedSizeSize; // hash(4) + offset(8) + size(4)
            int tocLen = files.Count * tocEntrySize;
            writer.Write(new byte[tocLen]);

            // Compress files and build TOC
            var tocEntries = new List<TocEntry>();
            long currentOffset = stream.Position;

            // Gather all strings for dictionary, compress files and build TOC
            string[] allStrings = new string[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                AssetFile file = files[i];
                allStrings[i] = file.Path;
                byte[] compressedData = Compression.Compress(file.Data, true);
                writer.Write(compressedData);
                tocEntries.Add(new TocEntry
                {
                    Path = file.Path,
                    DataOffset = currentOffset,
                    CompressedSize = compressedData.Length,
                });
                currentOffset += compressedData.Length;
            }

            // Build optimized string table
            var stringCompressor = new StringCompressor(allStrings);
            long stringTableOffset = WriteStringTable(stream, writer, stringCompressor);

            // Write TOC with hashes and string references
            stream.Seek(tocPosition, SeekOrigin.Begin);
            foreach (var entry in tocEntries)
            {
                writer.Write(FastHash(entry.Path)); // Hash for quick lookup
                writer.Write(stringCompressor.GetStringOffset(entry.Path));
                writer.Write(entry.DataOffset);
                writer.Write(entry.CompressedSize);
            }

            // Write footer with metadata positions
            WriteFooter(writer, tocPosition, stringCompressor, stringTableOffset);
        }

        private static long WriteStringTable(FileStream stream, BinaryWriter writer, StringCompressor stringCompressor)
        {
            byte[] stringTableData = stringCompressor.BuildStringTable();
            long stringTableOffset = stream.Position;
            writer.Write(stringTableData);
            return stringTableOffset;
        }

        private static void WriteFooter(BinaryWriter writer, long tocPosition, StringCompressor stringCompressor, long stringTableOffset)
        {
            writer.Write(tocPosition);
            writer.Write(stringTableOffset);
            writer.Write(stringCompressor.DictionaryOffset);
        }

        public static byte[] GetAsset(string archiveFilePath, string assetPath)
        {
            using var stream = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return GetAsset(stream, assetPath);
        }

        private static byte[] GetAsset(FileStream stream, string assetPath)
        {
            stream.Seek(0, SeekOrigin.Begin);

            using var reader = new BinaryReader(stream);

            if (reader.ReadInt32() != Magic)
                throw new Exception("Invalid file format");

            return reader.ReadInt32() switch
            {
                Version => GetAssetV1(assetPath, stream, reader),
                _ => throw new Exception("Unsupported archive version"),
            };
        }

        private static byte[] GetAssetV1(string assetPath, FileStream stream, BinaryReader reader)
        {
            int fileCount = reader.ReadInt32();

            // Read footer to find metadata locations
            stream.Seek(-24, SeekOrigin.End); // 3 * sizeof(long) = 24
            long tocPosition = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long dictionaryOffset = reader.ReadInt64();

            // Load string compressor for decompression
            stream.Seek(dictionaryOffset, SeekOrigin.Begin);
            var stringCompressor = new StringCompressor(reader);

            // Find requested asset using hash first
            uint targetHash = FastHash(assetPath);
            stream.Seek(tocPosition, SeekOrigin.Begin);

            for (int i = 0; i < fileCount; i++)
            {
                uint currentHash = reader.ReadUInt32();
                if (currentHash == targetHash)
                {
                    // Hash matches, verify full string
                    int stringOffset = reader.ReadInt32();
                    long dataOffset = reader.ReadInt64();
                    int compressedSize = reader.ReadInt32();

                    string currentPath = stringCompressor.GetString(stringOffset);
                    if (currentPath == assetPath)
                    {
                        stream.Seek(dataOffset, SeekOrigin.Begin);
                        byte[] compressedData = reader.ReadBytes(compressedSize);
                        return Compression.Decompress(compressedData, true);
                    }
                }
                else
                {
                    // Skip non-matching entries
                    stream.Seek(12, SeekOrigin.Current); // sizeof(int)+sizeof(long)+sizeof(int)
                }
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        // Fast 32-bit hash function (xxHash inspired)
        private static uint FastHash(string input)
        {
            uint hash = 5381;
            foreach (char c in input)
                hash = ((hash << 5) + hash) ^ c;
            return hash;
        }

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
