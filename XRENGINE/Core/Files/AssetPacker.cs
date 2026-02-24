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
        /// <summary>Indicates what stage of packing a progress report refers to.</summary>
        public enum PackPhase
        {
            /// <summary>About to start compressing the file.</summary>
            Compressing,
            /// <summary>LZMA progress update during compression of a large file (bytes processed so far).</summary>
            CompressingLargeFile,
            /// <summary>File has been compressed and written to the staging area.</summary>
            Staged,
        }

        public readonly record struct PackProgress(
            PackPhase Phase,
            int ProcessedFiles,
            int TotalFiles,
            string RelativePath,
            long SourceBytes,
            long CompressedBytes,
            long TotalSourceBytes,
            long TotalCompressedBytes,
            long GrandTotalBytes);

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

        private sealed class PackedAsset
        {
            public string Path { get; init; } = string.Empty;
            public uint Hash { get; init; }
            public int CompressedSize { get; init; }
            public byte[]? CompressedData { get; init; }
            public long ExistingDataOffset { get; init; }
            public long DataOffset { get; set; }
            public bool FromExisting => CompressedData is null;
        }

        private sealed class BucketLayout(int bucketCount, int[] starts, int[] counts)
        {
            public int BucketCount { get; } = bucketCount;
            public int[] Starts { get; } = starts;
            public int[] Counts { get; } = counts;
        }

        /// <summary>
        /// Adapter that forwards SevenZip LZMA encoder progress to a simple callback.
        /// </summary>
        private sealed class LzmaProgressAdapter(Action<long> onProgress) : SevenZip.ICodeProgress
        {
            public void SetProgress(long inSize, long outSize) => onProgress(inSize);
        }

        private struct TocEntry
        {
            public string Path;
            public long DataOffset;
            public int CompressedSize;
            public uint Hash;
        }

        public static void Repack(string archiveFilePath, string inputDirToAddFiles, params string[] assetPathsToRemove)
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Archive '{archiveFilePath}' not found.", archiveFilePath);

            string tempPath = Path.GetTempFileName();
            try
            {
                using FileMap sourceMap = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                unsafe
                {
                    using var reader = new CookedBinaryReader((byte*)sourceMap.Address, sourceMap.Length);
                    if (reader.ReadInt32() != Magic)
                        throw new InvalidOperationException("Invalid asset archive format.");

                    int version = reader.ReadInt32();
                    switch (version)
                    {
                        case VersionV1:
                            RepackV1(reader, sourceMap, tempPath, inputDirToAddFiles, assetPathsToRemove);
                            break;
                        case VersionV2:
                            RepackV2(reader, sourceMap, tempPath, inputDirToAddFiles, assetPathsToRemove);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported archive version '{version}'.");
                    }
                }

                File.Copy(tempPath, archiveFilePath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            }
        }

        private static unsafe void RepackV1(CookedBinaryReader reader, FileMap sourceMap, string destinationPath, string inputDirToAddFiles, string[] assetPathsToRemove)
        {
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(reader, VersionV1);
            HashSet<string>? removals = CreateRemovalSet(assetPathsToRemove);
            List<PackedAsset> assets = LoadExistingAssets(reader, footer, fileCount, removals);
            AppendNewFiles(assets, inputDirToAddFiles);
            if (assets.Count == 0)
                throw new InvalidOperationException("Cannot repack archive without any assets.");

            WriteArchive(destinationPath, VersionV1, TocLookupMode.HashBuckets, assets, sourceMap);
        }

        private static unsafe void RepackV2(CookedBinaryReader reader, FileMap sourceMap, string destinationPath, string inputDirToAddFiles, string[] assetPathsToRemove)
        {
            var lookupMode = (TocLookupMode)reader.ReadInt32();
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(reader, VersionV2);
            HashSet<string>? removals = CreateRemovalSet(assetPathsToRemove);
            List<PackedAsset> assets = LoadExistingAssets(reader, footer, fileCount, removals);
            AppendNewFiles(assets, inputDirToAddFiles);
            if (assets.Count == 0)
                throw new InvalidOperationException("Cannot repack archive without any assets.");

            WriteArchive(destinationPath, VersionV2, lookupMode, assets, sourceMap);
        }

        /// <summary>
        /// Default RAM budget for parallel compression (512 MB).
        /// During packing, source files are batched so that each parallel chunk's
        /// total source size stays under this limit.
        /// </summary>
        public const long DefaultMaxMemoryBytes = 512L * 1024 * 1024;

        /// <summary>
        /// Files larger than this threshold (10 MB) use chunked parallel LZMA
        /// compression for significantly faster packing.
        /// </summary>
        public const long LargeFileThreshold = 10L * 1024 * 1024;

        /// <summary>
        /// Result of compressing a single file, produced in parallel.
        /// </summary>
        private readonly struct CompressedFileResult
        {
            public required string RelativePath { get; init; }
            public required uint Hash { get; init; }
            public required byte[] CompressedData { get; init; }
            public required int SourceLength { get; init; }
        }

        /// <param name="inputDir">Directory whose contents will be packed.</param>
        /// <param name="outputFile">Destination .pak file path.</param>
        /// <param name="lookupMode">TOC lookup strategy (default: hash buckets).</param>
        /// <param name="maxMemoryBytes">
        /// Approximate RAM budget for parallel compression.  Files are batched so
        /// that each chunk's total source size stays under this limit.  Pass 0 or
        /// a negative value to use <see cref="DefaultMaxMemoryBytes"/>.
        /// </param>
        /// <param name="progress">Optional callback invoked after each file is staged.</param>
        public static void Pack(
            string inputDir,
            string outputFile,
            TocLookupMode? lookupMode = null,
            long maxMemoryBytes = 0,
            Action<PackProgress>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
                throw new DirectoryNotFoundException($"Input directory '{inputDir}' does not exist.");

            string[] files = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new InvalidOperationException("No files found to pack.");

            if (maxMemoryBytes <= 0)
                maxMemoryBytes = DefaultMaxMemoryBytes;

            string? outputDirectory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            // Create an empty output file immediately so callers can see it exists.
            using (new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { }

            // Pre-scan file sizes so we can partition into RAM-limited chunks.
            long[] fileSizes = new long[files.Length];
            for (int i = 0; i < files.Length; i++)
                fileSizes[i] = new FileInfo(files[i]).Length;

            long grandTotalBytes = 0;
            for (int i = 0; i < fileSizes.Length; i++)
                grandTotalBytes += fileSizes[i];

            string stagingPath = Path.Combine(Path.GetTempPath(), $"xre_assetpack_staging_{Guid.NewGuid():N}.bin");
            try
            {
                List<PackedAsset> assets = new(files.Length);
                long totalSourceBytes = 0;
                long totalCompressedBytes = 0;
                int filesWritten = 0;

                using (FileStream stagingStream = new(stagingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 64, FileOptions.SequentialScan))
                {
                    int chunkStart = 0;
                    while (chunkStart < files.Length)
                    {
                        // --- Build a chunk that fits within the RAM budget --------
                        int chunkEnd = chunkStart;
                        long chunkBytes = 0;
                        while (chunkEnd < files.Length)
                        {
                            long next = fileSizes[chunkEnd];
                            // Always include at least one file per chunk even if it
                            // exceeds the budget on its own.
                            if (chunkEnd > chunkStart && chunkBytes + next > maxMemoryBytes)
                                break;
                            chunkBytes += next;
                            chunkEnd++;
                        }

                        int chunkLen = chunkEnd - chunkStart;
                        var chunkResults = new CompressedFileResult[chunkLen];

                        // --- Report what we're about to compress ----------------
                        if (progress is not null)
                        {
                            for (int j = 0; j < chunkLen; j++)
                            {
                                int fileIndex = chunkStart + j;
                                string relativePath = NormalizePath(Path.GetRelativePath(inputDir, files[fileIndex]));
                                progress.Invoke(new PackProgress(
                                    PackPhase.Compressing,
                                    filesWritten + j + 1,
                                    files.Length,
                                    relativePath,
                                    fileSizes[fileIndex],
                                    0,
                                    totalSourceBytes,
                                    totalCompressedBytes,
                                    grandTotalBytes));
                            }
                        }

                        // --- Compress this chunk in parallel ---------------------
                        Parallel.For(0, chunkLen, j =>
                        {
                            int fileIndex = chunkStart + j;
                            string filePath = files[fileIndex];
                            string relativePath = NormalizePath(Path.GetRelativePath(inputDir, filePath));
                            byte[] data = File.ReadAllBytes(filePath);

                            byte[] compressed;
                            if (data.Length >= LargeFileThreshold)
                            {
                                // Large file: split into chunks and compress each
                                // chunk in parallel for much faster throughput.
                                Action<long>? chunkProgress = null;
                                if (progress is not null)
                                {
                                    long totalSrc = totalSourceBytes;   // capture
                                    long totalComp = totalCompressedBytes;
                                    long grand = grandTotalBytes;       // capture
                                    chunkProgress = bytesCompressed =>
                                    {
                                        progress.Invoke(new PackProgress(
                                            PackPhase.CompressingLargeFile,
                                            filesWritten + j + 1,
                                            files.Length,
                                            relativePath,
                                            data.Length,
                                            bytesCompressed,
                                            totalSrc,
                                            totalComp,
                                            grand));
                                    };
                                }

                                compressed = Compression.CompressChunked(data, progress: chunkProgress);
                            }
                            else
                            {
                                compressed = Compression.Compress(data, true);
                            }

                            chunkResults[j] = new CompressedFileResult
                            {
                                RelativePath = relativePath,
                                Hash = FastHash(relativePath),
                                CompressedData = compressed,
                                SourceLength = data.Length,
                            };
                        });

                        // --- Flush chunk to staging sequentially -----------------
                        for (int j = 0; j < chunkLen; j++)
                        {
                            ref readonly CompressedFileResult r = ref chunkResults[j];

                            long stagingOffset = stagingStream.Position;
                            stagingStream.Write(r.CompressedData, 0, r.CompressedData.Length);

                            assets.Add(new PackedAsset
                            {
                                Path = r.RelativePath,
                                Hash = r.Hash,
                                CompressedSize = r.CompressedData.Length,
                                ExistingDataOffset = stagingOffset,
                            });

                            totalSourceBytes += r.SourceLength;
                            totalCompressedBytes += r.CompressedData.Length;
                            filesWritten++;

                            progress?.Invoke(new PackProgress(
                                PackPhase.Staged,
                                filesWritten,
                                files.Length,
                                r.RelativePath,
                                r.SourceLength,
                                r.CompressedData.Length,
                                totalSourceBytes,
                                totalCompressedBytes,
                                grandTotalBytes));
                        }

                        chunkStart = chunkEnd;
                    }
                }

                using FileMap sourceMap = FileMap.FromFile(stagingPath, FileMapProtect.Read);
                WriteArchive(outputFile, CurrentVersion, lookupMode ?? DefaultLookupMode, assets, sourceMap);
            }
            finally
            {
                try { File.Delete(stagingPath); } catch { }
            }
        }

        private static unsafe void WriteArchive(string destinationPath, int version, TocLookupMode mode, List<PackedAsset> assets, FileMap? sourceMap)
        {
            if (assets.Count == 0)
                throw new InvalidOperationException("Archive must contain at least one asset.");

            long headerSize = version >= VersionV2 ? sizeof(int) * 4L : sizeof(int) * 3L;
            long tocSize = assets.Count * (long)TocEntrySize;
            long currentOffset = headerSize + tocSize;
            foreach (var asset in assets)
            {
                asset.DataOffset = currentOffset;
                currentOffset += asset.CompressedSize;
            }

            long dataLength = currentOffset - (headerSize + tocSize);
            var tocEntries = assets.Select(static a => new TocEntry
            {
                Path = a.Path,
                DataOffset = a.DataOffset,
                CompressedSize = a.CompressedSize,
                Hash = a.Hash,
            }).ToList();

            var stringCompressor = new StringCompressor(tocEntries.Select(static e => e.Path));
            byte[] stringTable = stringCompressor.BuildStringTable();
            long stringTableLength = stringTable.Length;
            long stringTableOffset = headerSize + tocSize + dataLength;

            var arranged = version >= VersionV2
                ? ArrangeTocEntries(tocEntries, mode)
                : (tocEntries, null);

            long indexSize = version >= VersionV2 && mode == TocLookupMode.HashBuckets && arranged.Buckets is not null
                ? sizeof(int) + arranged.Buckets.BucketCount * sizeof(int) * 2L
                : 0;

            long dictionaryOffset = stringTableOffset + stringCompressor.DictionaryOffset;
            long footerOffset = stringTableOffset + stringTableLength + indexSize;
            long footerSize = version >= VersionV2 ? FooterSizeV2 : FooterSizeV1;
            long totalSize = footerOffset + footerSize;

            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using FileStream stream = new(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
            stream.SetLength(totalSize);
            using FileMap map = FileMap.FromStream(stream, FileMapProtect.ReadWrite, 0, totalSize);
            using var writer = new CookedBinaryWriter((byte*)map.Address, totalSize, map);

            writer.Write(Magic);
            writer.Write(version);
            if (version >= VersionV2)
                writer.Write((int)mode);
            writer.Write(assets.Count);

            long tocPosition = writer.Position;
            WriteTocEntries(writer, arranged.Entries, stringCompressor);

            writer.Position = headerSize + tocSize;
            foreach (var asset in assets)
                WriteAssetData(writer, asset, sourceMap);

            long stringTablePosition = writer.Position;
            writer.Write(stringTable);

            long indexOffset = 0;
            if (indexSize > 0 && arranged.Buckets is not null)
            {
                indexOffset = writer.Position;
                WriteBucketTable(writer, arranged.Buckets);
            }

            if (version == VersionV1)
                WriteFooterV1(writer, tocPosition, stringTablePosition, dictionaryOffset);
            else
                WriteFooterV2(writer, tocPosition, stringTablePosition, dictionaryOffset, indexOffset);
        }

        private static List<PackedAsset> LoadExistingAssets(CookedBinaryReader reader, FooterInfo footer, int fileCount, HashSet<string>? removals)
        {
            reader.Position = ResolveDictionaryOffset(footer);
            var stringTable = new StringCompressor(reader);

            reader.Position = footer.TocPosition;
            List<PackedAsset> assets = new(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                string normalizedPath = NormalizePath(stringTable.GetString(entry.StringOffset));
                if (removals?.Contains(normalizedPath) == true)
                    continue;

                assets.Add(new PackedAsset
                {
                    Path = normalizedPath,
                    Hash = entry.Hash,
                    CompressedSize = entry.CompressedSize,
                    ExistingDataOffset = entry.DataOffset,
                });
            }

            return assets;
        }

        private static string NormalizePath(string path)
            => path.Replace('\\', '/');

        private static HashSet<string>? CreateRemovalSet(string[] assetPaths)
        {
            if (assetPaths is not { Length: > 0 })
                return null;

            HashSet<string> set = new(StringComparer.Ordinal);
            foreach (string assetPath in assetPaths)
            {
                if (!string.IsNullOrWhiteSpace(assetPath))
                    set.Add(NormalizePath(assetPath));
            }

            return set;
        }

        private static void AppendNewFiles(List<PackedAsset> assets, string? inputDir)
        {
            if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
                return;

            Dictionary<string, int> lookup = new(StringComparer.Ordinal);
            for (int i = 0; i < assets.Count; i++)
                lookup[assets[i].Path] = i;

            foreach (string filePath in Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = NormalizePath(Path.GetRelativePath(inputDir, filePath));
                byte[] rawData = File.ReadAllBytes(filePath);
                byte[] compressed = rawData.Length >= LargeFileThreshold
                    ? Compression.CompressChunked(rawData)
                    : Compression.Compress(rawData, true);

                PackedAsset asset = new()
                {
                    Path = relativePath,
                    Hash = FastHash(relativePath),
                    CompressedSize = compressed.Length,
                    CompressedData = compressed,
                };

                if (lookup.TryGetValue(relativePath, out int index))
                {
                    assets[index] = asset;
                }
                else
                {
                    lookup[relativePath] = assets.Count;
                    assets.Add(asset);
                }
            }
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
                    return ([.. entries], null);
            }
        }

        private static (List<TocEntry> Entries, BucketLayout Buckets) ArrangeWithBuckets(List<TocEntry> entries)
        {
            int bucketCount = CalculateBucketCount(Math.Max(1, entries.Count));
            var buckets = new List<TocEntry>[bucketCount];

            foreach (var entry in entries)
            {
                int bucketIndex = (int)(entry.Hash & (bucketCount - 1));
                buckets[bucketIndex] ??= [];
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

        private static void WriteTocEntries(CookedBinaryWriter writer, List<TocEntry> entries, StringCompressor stringCompressor)
        {
            foreach (var entry in entries)
            {
                writer.Write(entry.Hash);
                writer.Write(stringCompressor.GetStringOffset(entry.Path));
                writer.Write(entry.DataOffset);
                writer.Write(entry.CompressedSize);
            }
        }

        private static long WriteBucketTable(CookedBinaryWriter writer, BucketLayout layout)
        {
            long offset = writer.Position;
            writer.Write(layout.BucketCount);
            for (int i = 0; i < layout.BucketCount; i++)
            {
                writer.Write(layout.Starts[i]);
                writer.Write(layout.Counts[i]);
            }
            return offset;
        }

        private static void WriteFooterV1(CookedBinaryWriter writer, long tocPosition, long stringTableOffset, long dictionaryOffset)
        {
            writer.Write(tocPosition);
            writer.Write(stringTableOffset);
            writer.Write(dictionaryOffset);
        }

        private static void WriteFooterV2(CookedBinaryWriter writer, long tocPosition, long stringTableOffset, long dictionaryOffset, long indexOffset)
        {
            writer.Write(tocPosition);
            writer.Write(stringTableOffset);
            writer.Write(dictionaryOffset);
            writer.Write(indexOffset);
        }

        private static unsafe void WriteAssetData(CookedBinaryWriter writer, PackedAsset asset, FileMap? sourceMap)
        {
            if (asset.FromExisting)
            {
                if (sourceMap is null)
                    throw new InvalidOperationException("Existing asset data requires a source archive.");

                var span = new ReadOnlySpan<byte>((byte*)sourceMap.Address + asset.ExistingDataOffset, asset.CompressedSize);
                writer.WriteBytes(span);
            }
            else
            {
                writer.Write(asset.CompressedData!);
            }
        }

        public static byte[] GetAsset(string archiveFilePath, string assetPath)
        {
            unsafe
            {
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
                if (reader.ReadInt32() != Magic)
                    throw new InvalidOperationException("Invalid asset archive format.");

                int version = reader.ReadInt32();
                return version switch
                {
                    VersionV1 => GetAssetV1(assetPath, reader),
                    VersionV2 => GetAssetV2(assetPath, reader),
                    _ => throw new InvalidOperationException($"Unsupported archive version '{version}'."),
                };
            }
        }

        private static byte[] GetAssetV1(string assetPath, CookedBinaryReader reader)
        {
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(reader, VersionV1);
            reader.Position = ResolveDictionaryOffset(footer);
            var stringCompressor = new StringCompressor(reader);
            return GetAssetLinear(assetPath, fileCount, reader, stringCompressor, footer.TocPosition);
        }

        private static byte[] GetAssetV2(string assetPath, CookedBinaryReader reader)
        {
            var mode = (TocLookupMode)reader.ReadInt32();
            int fileCount = reader.ReadInt32();
            var footer = ReadFooter(reader, VersionV2);

            reader.Position = ResolveDictionaryOffset(footer);
            var stringCompressor = new StringCompressor(reader);

            return mode switch
            {
                TocLookupMode.HashBuckets => GetAssetFromBuckets(assetPath, fileCount, reader, stringCompressor, footer),
                TocLookupMode.SortedByHash => GetAssetSorted(assetPath, fileCount, reader, stringCompressor, footer),
                _ => GetAssetLinear(assetPath, fileCount, reader, stringCompressor, footer.TocPosition),
            };
        }

        private static FooterInfo ReadFooter(CookedBinaryReader reader, int version)
        {
            long footerSize = version >= VersionV2 ? FooterSizeV2 : FooterSizeV1;
            long saved = reader.Position;
            reader.Position = reader.Length - footerSize;

            long tocPosition = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long dictionaryOffset = reader.ReadInt64();
            long indexOffset = version >= VersionV2 ? reader.ReadInt64() : 0;

            reader.Position = saved;
            return new FooterInfo(tocPosition, stringTableOffset, dictionaryOffset, indexOffset);
        }

        private static long ResolveDictionaryOffset(FooterInfo footer)
            => footer.DictionaryOffset < footer.StringTableOffset
                ? footer.StringTableOffset + footer.DictionaryOffset
                : footer.DictionaryOffset;

        private static byte[] GetAssetLinear(string assetPath, int fileCount, CookedBinaryReader reader, StringCompressor stringCompressor, long tocPosition)
        {
            string normalized = NormalizePath(assetPath);
            uint targetHash = FastHash(normalized);
            reader.Position = tocPosition;

            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                if (TryLoadEntry(normalized, targetHash, reader, stringCompressor, entry, out byte[] data))
                    return data;
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        private static byte[] GetAssetFromBuckets(string assetPath, int fileCount, CookedBinaryReader reader, StringCompressor stringCompressor, FooterInfo footer)
        {
            if (footer.IndexTableOffset == 0)
                return GetAssetLinear(assetPath, fileCount, reader, stringCompressor, footer.TocPosition);

            reader.Position = footer.IndexTableOffset;
            int bucketCount = reader.ReadInt32();
            if (bucketCount <= 0 || (bucketCount & (bucketCount - 1)) != 0)
                return GetAssetLinear(assetPath, fileCount, reader, stringCompressor, footer.TocPosition);

            string normalized = NormalizePath(assetPath);
            uint targetHash = FastHash(normalized);
            int bucketIndex = (int)(targetHash & (bucketCount - 1));
            long bucketEntryOffset = footer.IndexTableOffset + sizeof(int) + bucketIndex * sizeof(int) * 2L;
            reader.Position = bucketEntryOffset;
            int start = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count <= 0)
                throw new FileNotFoundException($"Asset {assetPath} not found");

            long tocOffset = footer.TocPosition + start * (long)TocEntrySize;
            reader.Position = tocOffset;
            for (int i = 0; i < count; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                if (TryLoadEntry(normalized, targetHash, reader, stringCompressor, entry, out byte[] data))
                    return data;
            }

            throw new FileNotFoundException($"Asset {assetPath} not found");
        }

        private static byte[] GetAssetSorted(string assetPath, int fileCount, CookedBinaryReader reader, StringCompressor stringCompressor, FooterInfo footer)
        {
            string normalized = NormalizePath(assetPath);
            uint targetHash = FastHash(normalized);
            int left = 0;
            int right = fileCount - 1;

            while (left <= right)
            {
                int mid = left + ((right - left) / 2);
                var midEntry = ReadTocEntryAt(reader, footer.TocPosition, mid);
                if (midEntry.Hash == targetHash)
                {
                    int first = mid;
                    while (first > 0)
                    {
                        var prev = ReadTocEntryAt(reader, footer.TocPosition, first - 1);
                        if (prev.Hash != targetHash)
                            break;
                        first--;
                    }

                    int last = mid;
                    while (last < fileCount - 1)
                    {
                        var next = ReadTocEntryAt(reader, footer.TocPosition, last + 1);
                        if (next.Hash != targetHash)
                            break;
                        last++;
                    }

                    for (int i = first; i <= last; i++)
                    {
                        var entry = ReadTocEntryAt(reader, footer.TocPosition, i);
                        if (TryLoadEntry(normalized, targetHash, reader, stringCompressor, entry, out byte[] data))
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

        private static bool TryLoadEntry(string normalizedPath, uint targetHash, CookedBinaryReader reader, StringCompressor stringCompressor, TocEntryData entry, out byte[] data)
        {
            if (entry.Hash != targetHash)
            {
                data = [];
                return false;
            }

            string currentPath = NormalizePath(stringCompressor.GetString(entry.StringOffset));
            if (!string.Equals(currentPath, normalizedPath, StringComparison.Ordinal))
            {
                data = [];
                return false;
            }

            long saved = reader.Position;
            reader.Position = entry.DataOffset;
            byte[] compressedData = reader.ReadBytes(entry.CompressedSize);
            reader.Position = saved;
            data = Compression.Decompress(compressedData, true);
            return true;
        }

        private static TocEntryData ReadSequentialTocEntry(CookedBinaryReader reader)
            => new(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt32());

        private static TocEntryData ReadTocEntryAt(CookedBinaryReader reader, long tocPosition, int index)
        {
            long offset = tocPosition + index * (long)TocEntrySize;
            long saved = reader.Position;
            reader.Position = offset;
            var entry = ReadSequentialTocEntry(reader);
            reader.Position = saved;
            return entry;
        }

        private static uint FastHash(string input)
        {
            uint hash = 5381;
            foreach (char c in input)
                hash = ((hash << 5) + hash) ^ c;
            return hash;
        }
    }
}
