using System;
using System.Linq;
using System.Text;
using XREngine.Data;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Asset packer for compressing and storing multiple files in a single archive.
    /// Each file is stored with a key set to its path relative to the root input directory and is compressed using LZMA compression.
    /// </summary>
    public static partial class AssetPacker
    {
        private const int HashSize = 4; // Using 32-bit hash for path lookup
        private const int DataOffsetSize = 8; // 64-bit offset to data
        private const int CompressedSizeSize = 4; // Compressed data size
        private const int Magic = 0x4652454B; // "FREK"
        private static readonly Encoding StringEncoding = Encoding.UTF8;
        private const int VersionV1 = 1;
        private const int VersionV2 = 2;
        private const int CurrentVersion = VersionV2;
        private const int TocEntrySize = HashSize + 4 + DataOffsetSize + CompressedSizeSize;
        private const int FooterSizeV1 = sizeof(long) * 3;
        private const int FooterSizeV2 = sizeof(long) * 4;

        public static TocLookupMode DefaultLookupMode { get; set; } = TocLookupMode.HashBuckets;

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
            public uint Hash;
        }

        private sealed class BucketLayout(int bucketCount, int[] starts, int[] counts)
        {
            public int BucketCount { get; } = bucketCount;
            public int[] Starts { get; } = starts;
            public int[] Counts { get; } = counts;
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
                case VersionV1:
                    RepackV1(stream, reader, writer, inputDirToAddFiles, assetPathsToRemove);
                    break;
                case VersionV2:
                    RepackV2(stream, reader, writer, inputDirToAddFiles, assetPathsToRemove);
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
            WriteFooterV1(writer, tocPosition, stringCompressor, stringTableOffsetNew);
        }

        private static void RepackV2(FileStream stream, BinaryReader reader, BinaryWriter writer, string inputDirToAddFiles, string[] assetPathsToRemove)
        {
            var lookupMode = (TocLookupMode)reader.ReadInt32();
            int originalFileCount = reader.ReadInt32();
            var footer = ReadFooter(stream, reader, VersionV2);

            stream.Seek(footer.DictionaryOffset, SeekOrigin.Begin);
            var existingStrings = new StringCompressor(reader);

            var removals = assetPathsToRemove is { Length: > 0 }
                ? new HashSet<string>(assetPathsToRemove, StringComparer.Ordinal)
                : null;

            var entries = new Dictionary<string, TocEntry>(originalFileCount, StringComparer.Ordinal);

            stream.Seek(footer.TocPosition, SeekOrigin.Begin);
            for (int i = 0; i < originalFileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                string path = existingStrings.GetString(entry.StringOffset);
                if (removals is not null && removals.Contains(path))
                    continue;

                entries[path] = new TocEntry
                {
                    Path = path,
                    DataOffset = entry.DataOffset,
                    CompressedSize = entry.CompressedSize,
                    Hash = entry.Hash,
                };
            }

            long metadataStart = footer.StringTableOffset;
            stream.SetLength(metadataStart);
            stream.Seek(metadataStart, SeekOrigin.Begin);

            if (!string.IsNullOrWhiteSpace(inputDirToAddFiles) && Directory.Exists(inputDirToAddFiles))
            {
                foreach (string filePath in Directory.GetFiles(inputDirToAddFiles, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(inputDirToAddFiles, filePath);
                    byte[] fileData = File.ReadAllBytes(filePath);
                    byte[] compressed = Compression.Compress(fileData, true);
                    long dataOffset = stream.Position;
                    writer.Write(compressed);

                    entries[relativePath] = new TocEntry
                    {
                        Path = relativePath,
                        DataOffset = dataOffset,
                        CompressedSize = compressed.Length,
                        Hash = FastHash(relativePath),
                    };
                }
            }

            if (entries.Count == 0)
                throw new InvalidOperationException("Cannot repack archive without any assets.");

            var finalEntries = entries.Values.ToList();
            var stringCompressor = new StringCompressor(finalEntries.Select(static e => e.Path));
            long stringTableOffset = WriteStringTable(stream, writer, stringCompressor);

            var arranged = ArrangeTocEntries(finalEntries, lookupMode);
            long tocPosition = stream.Position;
            WriteTocEntries(stream, writer, tocPosition, arranged.Entries, stringCompressor);

            long tocEnd = tocPosition + arranged.Entries.Count * (long)TocEntrySize;
            stream.Seek(tocEnd, SeekOrigin.Begin);

            long indexOffset = 0;
            if (lookupMode == TocLookupMode.HashBuckets && arranged.Buckets is not null)
                indexOffset = WriteBucketTable(writer, arranged.Buckets);

            WriteFooterV2(writer, tocPosition, stringTableOffset, stringCompressor.DictionaryOffset, indexOffset);

            stream.Seek(sizeof(int) * 3, SeekOrigin.Begin);
            writer.Write(arranged.Entries.Count);
        }

        /// <summary>
        /// Packs a directory of files into a single archive.
        /// </summary>
        /// <param name="inputDir"></param>
        /// <param name="outputFile"></param>
        public static void Pack(string inputDir, string outputFile, TocLookupMode? lookupMode = null)
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

            TocLookupMode mode = lookupMode ?? DefaultLookupMode;

            // Write header
            writer.Write(Magic);
            writer.Write(CurrentVersion);
            writer.Write((int)mode);
            writer.Write(files.Count);

            // Prepare space for TOC (will fill later)
            long tocPosition = stream.Position;
            int tocLen = files.Count * TocEntrySize;
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
                    Hash = FastHash(file.Path)
                });
                currentOffset += compressedData.Length;
            }

            // Build optimized string table
            var stringCompressor = new StringCompressor(allStrings);
            long stringTableOffset = WriteStringTable(stream, writer, stringCompressor);

            var arranged = ArrangeTocEntries(tocEntries, mode);

            // Write TOC with hashes and string references
            WriteTocEntries(stream, writer, tocPosition, arranged.Entries, stringCompressor);

            // Write index structures, if any
            long indexOffset = 0;
            stream.Seek(0, SeekOrigin.End);
            if (mode == TocLookupMode.HashBuckets && arranged.Buckets is not null)
                indexOffset = WriteBucketTable(writer, arranged.Buckets);

            // Write footer with metadata positions
            WriteFooterV2(writer, tocPosition, stringTableOffset, stringCompressor.DictionaryOffset, indexOffset);
        }

        private static (List<TocEntry> Entries, BucketLayout? Buckets) ArrangeTocEntries(List<TocEntry> entries, TocLookupMode mode)
        {
            switch (mode)
            {
                case TocLookupMode.HashBuckets:
                    return ArrangeWithBuckets(entries);
                case TocLookupMode.SortedByHash:
                    var sorted = entries
                        .OrderBy(static e => e.Hash)
                        .ThenBy(static e => e.Path, StringComparer.Ordinal)
                        .ToList();
                    return (sorted, null);
                default:
                    return (new List<TocEntry>(entries), null);
            }
        }

        private static (List<TocEntry> Entries, BucketLayout Buckets) ArrangeWithBuckets(List<TocEntry> entries)
        {
            int bucketCount = CalculateBucketCount(Math.Max(1, entries.Count));
            var buckets = new List<TocEntry>[bucketCount];

            foreach (var entry in entries)
            {
                int bucketIndex = (int)(entry.Hash & (bucketCount - 1));
                buckets[bucketIndex] ??= new List<TocEntry>();
                buckets[bucketIndex]!.Add(entry);
            }

            List<TocEntry> ordered = new(entries.Count);
            int[] starts = new int[bucketCount];
            int[] counts = new int[bucketCount];

            for (int i = 0; i < bucketCount; i++)
            {
                starts[i] = ordered.Count;
                if (buckets[i] is { Count: > 0 } bucketEntries)
                {
                    ordered.AddRange(bucketEntries);
                    counts[i] = bucketEntries.Count;
                }
                else
                {
                    counts[i] = 0;
                }
            }

            return (ordered, new BucketLayout(bucketCount, starts, counts));
        }

        private static int CalculateBucketCount(int fileCount)
        {
            int bucketCount = 1;
            while (bucketCount < fileCount)
                bucketCount <<= 1;

            return Math.Clamp(bucketCount, 1, 1 << 20);
        }

        private static void WriteTocEntries(FileStream stream, BinaryWriter writer, long tocPosition, List<TocEntry> entries, StringCompressor stringCompressor)
        {
            stream.Seek(tocPosition, SeekOrigin.Begin);
            foreach (var entry in entries)
            {
                writer.Write(entry.Hash);
                writer.Write(stringCompressor.GetStringOffset(entry.Path));
                writer.Write(entry.DataOffset);
                writer.Write(entry.CompressedSize);
            }
        }

        private static long WriteBucketTable(BinaryWriter writer, BucketLayout layout)
        {
            long offset = writer.BaseStream.Position;
            writer.Write(layout.BucketCount);
            for (int i = 0; i < layout.BucketCount; i++)
            {
                writer.Write(layout.Starts[i]);
                writer.Write(layout.Counts[i]);
            }
            return offset;
        }

        private static void WriteFooterV2(BinaryWriter writer, long tocPosition, long stringTableOffset, long dictionaryOffset, long indexOffset)
        {
            writer.Write(tocPosition);
            writer.Write(stringTableOffset);
            writer.Write(dictionaryOffset);
            writer.Write(indexOffset);
        }

        private static long WriteStringTable(FileStream stream, BinaryWriter writer, StringCompressor stringCompressor)
        {
            byte[] stringTableData = stringCompressor.BuildStringTable();
            long stringTableOffset = stream.Position;
            writer.Write(stringTableData);
            return stringTableOffset;
        }

        private static void WriteFooterV1(BinaryWriter writer, long tocPosition, StringCompressor stringCompressor, long stringTableOffset)
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

            int version = reader.ReadInt32();
            return version switch
            {
                VersionV1 => GetAssetV1(assetPath, stream, reader),
                VersionV2 => GetAssetV2(assetPath, stream, reader),
                _ => throw new Exception("Unsupported archive version"),
            };
        }

        private static byte[] GetAssetV1(string assetPath, FileStream stream, BinaryReader reader)
        {
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(stream, reader, VersionV1);

            stream.Seek(footer.DictionaryOffset, SeekOrigin.Begin);
            var stringCompressor = new StringCompressor(reader);

            return GetAssetLinear(assetPath, fileCount, stream, reader, stringCompressor, footer.TocPosition);
        }

        private static byte[] GetAssetV2(string assetPath, FileStream stream, BinaryReader reader)
        {
            var mode = (TocLookupMode)reader.ReadInt32();
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(stream, reader, VersionV2);

            stream.Seek(footer.DictionaryOffset, SeekOrigin.Begin);
            var stringCompressor = new StringCompressor(reader);

            return mode switch
            {
                TocLookupMode.HashBuckets => GetAssetFromBuckets(assetPath, fileCount, stream, reader, stringCompressor, footer),
                TocLookupMode.SortedByHash => GetAssetSorted(assetPath, fileCount, stream, reader, stringCompressor, footer),
                _ => GetAssetLinear(assetPath, fileCount, stream, reader, stringCompressor, footer.TocPosition)
            };
        }

        private static FooterInfo ReadFooter(FileStream stream, BinaryReader reader, int version)
        {
            int footerSize = version >= VersionV2 ? FooterSizeV2 : FooterSizeV1;
            stream.Seek(-footerSize, SeekOrigin.End);

            long tocPosition = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long dictionaryOffset = reader.ReadInt64();
            long indexOffset = version >= VersionV2 ? reader.ReadInt64() : 0;

            return new FooterInfo(tocPosition, stringTableOffset, dictionaryOffset, indexOffset);
        }

        private static byte[] GetAssetLinear(string assetPath, int fileCount, FileStream stream, BinaryReader reader, StringCompressor stringCompressor, long tocPosition)
        {
            uint targetHash = FastHash(assetPath);
            stream.Seek(tocPosition, SeekOrigin.Begin);

            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                if (TryLoadEntry(assetPath, targetHash, stream, reader, stringCompressor, entry, out byte[] data))
                    return data;
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        private static byte[] GetAssetFromBuckets(string assetPath, int fileCount, FileStream stream, BinaryReader reader, StringCompressor stringCompressor, FooterInfo footer)
        {
            if (footer.IndexTableOffset == 0)
                return GetAssetLinear(assetPath, fileCount, stream, reader, stringCompressor, footer.TocPosition);

            stream.Seek(footer.IndexTableOffset, SeekOrigin.Begin);
            int bucketCount = reader.ReadInt32();
            if (bucketCount <= 0 || (bucketCount & (bucketCount - 1)) != 0)
                return GetAssetLinear(assetPath, fileCount, stream, reader, stringCompressor, footer.TocPosition);

            uint targetHash = FastHash(assetPath);
            int bucketIndex = (int)(targetHash & (bucketCount - 1));
            long bucketEntryOffset = footer.IndexTableOffset + sizeof(int) + bucketIndex * sizeof(int) * 2L;
            stream.Seek(bucketEntryOffset, SeekOrigin.Begin);
            int start = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count <= 0)
                throw new FileNotFoundException($"Asset {assetPath} not found");

            long tocOffset = footer.TocPosition + start * (long)TocEntrySize;
            stream.Seek(tocOffset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                if (TryLoadEntry(assetPath, targetHash, stream, reader, stringCompressor, entry, out byte[] data))
                    return data;
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        private static byte[] GetAssetSorted(string assetPath, int fileCount, FileStream stream, BinaryReader reader, StringCompressor stringCompressor, FooterInfo footer)
        {
            uint targetHash = FastHash(assetPath);
            int left = 0;
            int right = fileCount - 1;

            while (left <= right)
            {
                int mid = left + ((right - left) / 2);
                var midEntry = ReadTocEntryAt(stream, reader, footer.TocPosition, mid);
                if (midEntry.Hash == targetHash)
                {
                    int first = mid;
                    while (first > 0)
                    {
                        var prev = ReadTocEntryAt(stream, reader, footer.TocPosition, first - 1);
                        if (prev.Hash != targetHash)
                            break;
                        first--;
                    }

                    int last = mid;
                    while (last < fileCount - 1)
                    {
                        var next = ReadTocEntryAt(stream, reader, footer.TocPosition, last + 1);
                        if (next.Hash != targetHash)
                            break;
                        last++;
                    }

                    for (int i = first; i <= last; i++)
                    {
                        var entry = ReadTocEntryAt(stream, reader, footer.TocPosition, i);
                        if (TryLoadEntry(assetPath, targetHash, stream, reader, stringCompressor, entry, out byte[] data))
                            return data;
                    }

                    break;
                }

                if (midEntry.Hash < targetHash)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        private static TocEntryData ReadSequentialTocEntry(BinaryReader reader)
            => new(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt32());

        private static TocEntryData ReadTocEntryAt(FileStream stream, BinaryReader reader, long tocPosition, int index)
        {
            long offset = tocPosition + index * (long)TocEntrySize;
            stream.Seek(offset, SeekOrigin.Begin);
            return ReadSequentialTocEntry(reader);
        }

        private static bool TryLoadEntry(string assetPath, uint targetHash, FileStream stream, BinaryReader reader, StringCompressor stringCompressor, TocEntryData entry, out byte[] data)
        {
            if (entry.Hash != targetHash)
            {
                data = [];
                return false;
            }

            string currentPath = stringCompressor.GetString(entry.StringOffset);
            if (!string.Equals(currentPath, assetPath, StringComparison.Ordinal))
            {
                data = [];
                return false;
            }

            stream.Seek(entry.DataOffset, SeekOrigin.Begin);
            byte[] compressedData = reader.ReadBytes(entry.CompressedSize);
            data = Compression.Decompress(compressedData, true);
            return true;
        }

        // Fast 32-bit hash function (xxHash inspired)
        private static uint FastHash(string input)
        {
            uint hash = 5381;
            foreach (char c in input)
                hash = ((hash << 5) + hash) ^ c;
            return hash;
        }
    }
}
