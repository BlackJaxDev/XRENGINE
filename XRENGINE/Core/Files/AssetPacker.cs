using System.IO.Hashing;
using System.Text;
using XREngine.Data;

// Keep usings visible for CompressionCodec.

namespace XREngine.Core.Files
{
    /// <summary>Format feature flags stored in archive headers.</summary>
    [Flags]
    public enum ArchiveFlags : int
    {
        None = 0,
        /// <summary>TOC entries contain XXH64 content hashes.</summary>
        HasContentHashes = 1 << 0,
        /// <summary>TOC entries contain source file timestamps.</summary>
        HasSourceTimestamps = 1 << 1,
        /// <summary>TOC entries contain uncompressed sizes.</summary>
        HasUncompressedSizes = 1 << 2,
        /// <summary>Archive may have dead (orphaned) data from append-only updates.</summary>
        AppendOnly = 1 << 3,
    }

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

        #region Constants

        private const int HashSize = 4; // Using 32-bit hash for path lookup
        private const int DataOffsetSize = 8; // 64-bit offset to data
        private const int CompressedSizeSize = 4; // Compressed data size
        private const int Magic = 0x4652454B; // "FREK"
        private static readonly Encoding StringEncoding = Encoding.UTF8;
        private const int CurrentVersion = 4;

        // TOC entry: Hash(4) + StringOffset(4) + DataOffset(8) + CompressedSize(4) + UncompressedSize(8) + ContentXXH64(8) + SourceTimestamp(8) + Codec(1) + Reserved(3) = 48
        private const int TocEntrySize = HashSize + 4 + DataOffsetSize + CompressedSizeSize + sizeof(long) + sizeof(ulong) + sizeof(long) + sizeof(byte) + 3;

        // Footer: Toc(8) + StringTable(8) + Dictionary(8) + Index(8) + DeadBytes(8) = 40
        private const int FooterSize = sizeof(long) * 5;

        // Header: Magic(4) + Version(4) + Flags(4) + LookupMode(4) + FileCount(4) + BuildTimestamp(8) + DeadBytes(8) = 36
        private const int HeaderSize = sizeof(int) * 5 + sizeof(long) * 2;

        /// <summary>All V3 format flags that Pack sets by default.</summary>
        private const ArchiveFlags DefaultV3Flags =
            ArchiveFlags.HasContentHashes |
            ArchiveFlags.HasSourceTimestamps |
            ArchiveFlags.HasUncompressedSizes;

        /// <summary>
        /// Default RAM budget for parallel compression (512 MB).
        /// During packing, source files are batched so that each parallel chunk's
        /// total source size stays under this limit.
        /// </summary>
        public const long DefaultMaxMemoryBytes = 512L * 1024 * 1024;

        /// <summary>
        /// Default size (1 MB) of the temporary buffer used to stream-copy existing archive
        /// payload bytes during repack/compact operations.
        /// </summary>
        public const int DefaultArchiveCopyBufferBytes = 1024 * 1024;

        /// <summary>Lower bound (64 KB) for <see cref="ArchiveCopyBufferBytes"/>.</summary>
        public const int MinArchiveCopyBufferBytes = 64 * 1024;

        /// <summary>Upper bound (16 MB) for <see cref="ArchiveCopyBufferBytes"/>.</summary>
        public const int MaxArchiveCopyBufferBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Files larger than this threshold (10 MB) use chunked parallel LZMA
        /// compression for significantly faster packing.
        /// </summary>
        public const long LargeFileThreshold = 10L * 1024 * 1024;

        #endregion

        #region Configuration

        public static TocLookupMode DefaultLookupMode { get; set; } = TocLookupMode.HashBuckets;

        /// <summary>
        /// Default compression codec used when packing new archives.
        /// Existing entries keep their original codec during repack.
        /// </summary>
        public static CompressionCodec DefaultCodec { get; set; } = CompressionCodec.Lzma;

        private static int _archiveCopyBufferBytes = DefaultArchiveCopyBufferBytes;

        /// <summary>
        /// Buffer size used when copying existing compressed payload data from one archive file
        /// into another during repack/compact. This bounds RAM use for those operations.
        /// </summary>
        public static int ArchiveCopyBufferBytes
        {
            get => _archiveCopyBufferBytes;
            set => _archiveCopyBufferBytes = Math.Clamp(value, MinArchiveCopyBufferBytes, MaxArchiveCopyBufferBytes);
        }

        #endregion

        #region Nested Types

        private sealed class PackedAsset
        {
            public string Path { get; init; } = string.Empty;
            public uint Hash { get; init; }
            public int CompressedSize { get; init; }
            public byte[]? CompressedData { get; init; }
            public long ExistingDataOffset { get; init; }
            public long DataOffset { get; set; }
            public bool FromExisting => CompressedData is null;
            public long UncompressedSize { get; init; }
            public ulong ContentHash { get; init; }
            public long SourceTimestampUtcTicks { get; init; }
            public CompressionCodec Codec { get; init; }
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

            public long UncompressedSize;
            public ulong ContentHash;
            public long SourceTimestampUtcTicks;
            public CompressionCodec Codec;
        }

        private struct CompressedFileResult
        {
            public string RelativePath;
            public uint Hash;
            public byte[] CompressedData;
            public long SourceLength;
            public ulong ContentHash;
            public long SourceTimestampUtcTicks;
            public CompressionCodec Codec;
        }

        public static void Repack(string archiveFilePath, string inputDirToAddFiles, params string[] assetPathsToRemove)
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Archive '{archiveFilePath}' not found.", archiveFilePath);

            string tempPath = Path.GetTempFileName();
            try
            {
                using (FileMap sourceMap = FileMap.FromFile(archiveFilePath, FileMapProtect.Read))
                {
                    unsafe
                    {
                        using var reader = new CookedBinaryReader((byte*)sourceMap.Address, sourceMap.Length);
                        if (reader.ReadInt32() != Magic)
                            throw new InvalidOperationException("Invalid asset archive format.");

                        int version = reader.ReadInt32();
                        if (version != CurrentVersion)
                            throw new InvalidOperationException($"Unsupported archive version '{version}'. Only V{CurrentVersion} is supported.");

                        RepackV3(reader, sourceMap, tempPath, inputDirToAddFiles, assetPathsToRemove, archiveFilePath);
                    }
                }

                File.Copy(tempPath, archiveFilePath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            }
        }

        /// <summary>
        /// Append-only repack: existing data stays in place, new/changed data is appended,
        /// superseded entries become dead space tracked in the footer.
        /// Layout: [Header][Data (original + appended)][TOC][StringTable][Index][Footer]
        /// </summary>
        private static unsafe void RepackV3(CookedBinaryReader reader, FileMap sourceMap, string destinationPath, string inputDirToAddFiles, string[] assetPathsToRemove, string sourceArchivePath)
        {
            var flags = (ArchiveFlags)reader.ReadInt32();
            var lookupMode = (TocLookupMode)reader.ReadInt32();
            int fileCount = reader.ReadInt32();
            reader.ReadInt64(); // build timestamp (will be refreshed)
            long existingDeadBytes = reader.ReadInt64();

            var footer = ReadFooter(reader);

            HashSet<string>? removals = CreateRemovalSet(assetPathsToRemove);

            // Load all existing entries, tracking dead bytes from removals
            reader.Position = ResolveDictionaryOffset(footer);
            var stringTable = new StringCompressor(reader);
            reader.Position = footer.TocPosition;

            List<PackedAsset> assets = new(fileCount);
            long deadBytes = existingDeadBytes;

            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                string normalizedPath = NormalizePath(stringTable.GetString(entry.StringOffset));
                if (removals?.Contains(normalizedPath) == true)
                {
                    deadBytes += entry.CompressedSize;
                    continue;
                }

                assets.Add(new PackedAsset
                {
                    Path = normalizedPath,
                    Hash = entry.Hash,
                    CompressedSize = entry.CompressedSize,
                    ExistingDataOffset = entry.DataOffset,
                    UncompressedSize = entry.UncompressedSize,
                    ContentHash = entry.ContentHash,
                    SourceTimestampUtcTicks = entry.SourceTimestampUtcTicks,
                    Codec = entry.Codec,
                });
            }

            // Compress new/replacement files, tracking superseded entry sizes
            AppendNewFilesV3(assets, inputDirToAddFiles, ref deadBytes);

            if (assets.Count == 0)
                throw new InvalidOperationException("Cannot repack archive without any assets.");

            // Set flag if we have accumulated dead space
            if (deadBytes > 0)
                flags |= ArchiveFlags.AppendOnly;

            WriteArchive(destinationPath, lookupMode, assets, sourceMap, flags, deadBytes, sourceArchivePath);
        }

        /// <summary>
        /// Like <see cref="AppendNewFiles"/> but computes XXH64 hashes and source timestamps,
        /// and accumulates dead bytes from superseded entries.
        /// </summary>
        private static void AppendNewFilesV3(List<PackedAsset> assets, string? inputDir, ref long deadBytes)
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
                ulong contentHash = XxHash64.HashToUInt64(rawData);
                long sourceTimestamp = File.GetLastWriteTimeUtc(filePath).Ticks;

                CompressionCodec codec = DefaultCodec;
                byte[] compressed;
                if (codec == CompressionCodec.Lzma && rawData.Length >= LargeFileThreshold)
                    compressed = Compression.CompressChunked(rawData);
                else
                    compressed = Compression.Compress(rawData, codec);

                PackedAsset asset = new()
                {
                    Path = relativePath,
                    Hash = FastHash(relativePath),
                    CompressedSize = compressed.Length,
                    CompressedData = compressed,
                    UncompressedSize = rawData.Length,
                    ContentHash = contentHash,
                    SourceTimestampUtcTicks = sourceTimestamp,
                    Codec = codec,
                };

                if (lookup.TryGetValue(relativePath, out int index))
                {
                    deadBytes += assets[index].CompressedSize; // old data becomes dead
                    assets[index] = asset;
                }
                else
                {
                    lookup[relativePath] = assets.Count;
                    assets.Add(asset);
                }
            }
        }

        /// <summary>
        /// Rewrites a V3 archive to reclaim dead space left by append-only updates.
        /// All live entries are compacted into a fresh sequential layout.
        /// </summary>
        /// <param name="archiveFilePath">Path to the V3 archive to compact.</param>
        public static void Compact(string archiveFilePath)
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Archive '{archiveFilePath}' not found.", archiveFilePath);

            string tempPath = Path.GetTempFileName();
            try
            {
                using (FileMap sourceMap = FileMap.FromFile(archiveFilePath, FileMapProtect.Read))
                {
                    unsafe
                    {
                        using var reader = new CookedBinaryReader((byte*)sourceMap.Address, sourceMap.Length);
                        if (reader.ReadInt32() != Magic)
                            throw new InvalidOperationException("Invalid asset archive format.");

                        int version = reader.ReadInt32();
                        if (version != CurrentVersion)
                            throw new InvalidOperationException($"Compact requires a V{CurrentVersion} archive (found V{version}).");

                        var flags = (ArchiveFlags)reader.ReadInt32();
                        var lookupMode = (TocLookupMode)reader.ReadInt32();
                        int fileCount = reader.ReadInt32();
                        reader.ReadInt64(); // build timestamp
                        reader.ReadInt64(); // dead bytes

                        var footer = ReadFooter(reader);
                        List<PackedAsset> assets = LoadExistingAssets(reader, footer, fileCount, removals: null);

                        // Rewrite with zero dead bytes and clear the AppendOnly flag
                        flags &= ~ArchiveFlags.AppendOnly;
                        WriteArchive(tempPath, lookupMode, assets, sourceMap, flags, deadBytes: 0, sourceArchivePath: archiveFilePath);
                    }
                }

                File.Copy(tempPath, archiveFilePath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            }
        }

        /// <summary>
        /// Inspects a V3 archive and returns the asset paths whose source files have changed
        /// since the archive was built, based on embedded content hashes (XXH64).
        /// </summary>
        /// <param name="archiveFilePath">Path to the V3 archive.</param>
        /// <param name="sourceDir">Source directory to compare against.</param>
        /// <returns>
        /// List of normalized asset paths that are stale (content hash mismatch or missing from source).
        /// Assets present in the archive but absent on disk are also considered stale.
        /// </returns>
        public static IReadOnlyList<string> GetStalePaths(string archiveFilePath, string sourceDir)
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Archive '{archiveFilePath}' not found.", archiveFilePath);

            List<string> stale = [];
            unsafe
            {
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
                if (reader.ReadInt32() != Magic)
                    throw new InvalidOperationException("Invalid asset archive format.");

                int version = reader.ReadInt32();
                if (version != CurrentVersion)
                    throw new InvalidOperationException($"GetStalePaths requires a V{CurrentVersion} archive (found V{version}).");

                _ = (ArchiveFlags)reader.ReadInt32();
                _ = (TocLookupMode)reader.ReadInt32();
                int fileCount = reader.ReadInt32();
                reader.ReadInt64(); // build timestamp
                reader.ReadInt64(); // dead bytes

                var footer = ReadFooter(reader);
                reader.Position = ResolveDictionaryOffset(footer);
                var stringCompressor = new StringCompressor(reader);

                reader.Position = footer.TocPosition;
                for (int i = 0; i < fileCount; i++)
                {
                    var entry = ReadSequentialTocEntry(reader);
                    string path = NormalizePath(stringCompressor.GetString(entry.StringOffset));
                    string fullPath = Path.Combine(sourceDir, path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(fullPath))
                    {
                        stale.Add(path);
                        continue;
                    }

                    // Compare XXH64 content hash
                    byte[] sourceData = File.ReadAllBytes(fullPath);
                    ulong sourceHash = XxHash64.HashToUInt64(sourceData);
                    if (sourceHash != entry.ContentHash)
                        stale.Add(path);
                }
            }

            return stale;
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

                            // V3: compute XXH64 content hash of uncompressed data
                            ulong contentHash = XxHash64.HashToUInt64(data);
                            long sourceTimestamp = File.GetLastWriteTimeUtc(filePath).Ticks;

                            CompressionCodec codec = DefaultCodec;
                            byte[] compressed;
                            if (codec == CompressionCodec.Lzma && data.Length >= LargeFileThreshold)
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
                                compressed = Compression.Compress(data, codec);
                            }

                            chunkResults[j] = new CompressedFileResult
                            {
                                RelativePath = relativePath,
                                Hash = FastHash(relativePath),
                                CompressedData = compressed,
                                SourceLength = data.Length,
                                ContentHash = contentHash,
                                SourceTimestampUtcTicks = sourceTimestamp,
                                Codec = codec,
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
                                UncompressedSize = r.SourceLength,
                                ContentHash = r.ContentHash,
                                SourceTimestampUtcTicks = r.SourceTimestampUtcTicks,
                                Codec = r.Codec,
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
                WriteArchive(outputFile, lookupMode ?? DefaultLookupMode, assets, sourceMap, DefaultV3Flags);
            }
            finally
            {
                try { File.Delete(stagingPath); } catch { }
            }
        }

        #endregion

        #region Archive Writing

        /// <summary>
        /// Writes an archive with layout: [Header][Data][TOC][StringTable][Index][Footer].
        /// Data-before-TOC enables append-only repacking (existing data offsets stay valid).
        /// </summary>
        private static unsafe void WriteArchive(
            string destinationPath, TocLookupMode mode,
            List<PackedAsset> assets, FileMap? sourceMap,
            ArchiveFlags flags = ArchiveFlags.None, long deadBytes = 0,
            string? sourceArchivePath = null)
        {
            if (assets.Count == 0)
                throw new InvalidOperationException("Archive must contain at least one asset.");

            // --- Compute data offsets (data starts right after the fixed header) ---
            long currentOffset = HeaderSize;
            foreach (var asset in assets)
            {
                asset.DataOffset = currentOffset;
                currentOffset += asset.CompressedSize;
            }
            long dataEndOffset = currentOffset;

            // --- Build TOC entries ---
            var tocEntries = assets.Select(static a => new TocEntry
            {
                Path = a.Path,
                DataOffset = a.DataOffset,
                CompressedSize = a.CompressedSize,
                Hash = a.Hash,
                UncompressedSize = a.UncompressedSize,
                ContentHash = a.ContentHash,
                SourceTimestampUtcTicks = a.SourceTimestampUtcTicks,
                Codec = a.Codec,
            }).ToList();

            var stringCompressor = new StringCompressor(tocEntries.Select(static e => e.Path));
            byte[] stringTable = stringCompressor.BuildStringTable();
            var arranged = ArrangeTocEntries(tocEntries, mode);

            long tocOffset = dataEndOffset;
            long tocSize = assets.Count * (long)TocEntrySize;
            long stringTableOffset = tocOffset + tocSize;
            long dictionaryOffset = stringTableOffset + stringCompressor.DictionaryOffset;

            long indexSize = mode == TocLookupMode.HashBuckets && arranged.Buckets is not null
                ? sizeof(int) + arranged.Buckets.BucketCount * sizeof(int) * 2L
                : 0;

            long footerOffset = stringTableOffset + stringTable.Length + indexSize;
            long totalSize = footerOffset + FooterSize;

            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using FileStream stream = new(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
            stream.SetLength(totalSize);
            using FileMap map = FileMap.FromStream(stream, FileMapProtect.ReadWrite, 0, totalSize);
            using var writer = new CookedBinaryWriter((byte*)map.Address, totalSize, map);
            using FileStream? sourceArchiveStream = sourceArchivePath is null
                ? null
                : new FileStream(sourceArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.RandomAccess | FileOptions.SequentialScan);
            byte[]? copyBuffer = sourceArchiveStream is null ? null : new byte[ArchiveCopyBufferBytes];

            // --- Header (36 bytes) ---
            writer.Write(Magic);
            writer.Write(CurrentVersion);
            writer.Write((int)flags);
            writer.Write((int)mode);
            writer.Write(assets.Count);
            writer.Write(DateTime.UtcNow.Ticks);  // build timestamp
            writer.Write(deadBytes);

            // --- Data ---
            foreach (var asset in assets)
                WriteAssetData(writer, asset, sourceMap, sourceArchiveStream, copyBuffer);

            // --- TOC ---
            long tocPosition = writer.Position;
            WriteTocEntries(writer, arranged.Entries, stringCompressor);

            // --- String table ---
            long stringTablePosition = writer.Position;
            writer.Write(stringTable);

            // --- Bucket index ---
            long indexOffset = 0;
            if (indexSize > 0 && arranged.Buckets is not null)
            {
                indexOffset = writer.Position;
                WriteBucketTable(writer, arranged.Buckets);
            }

            // --- Footer (40 bytes) ---
            WriteFooter(writer, tocPosition, stringTablePosition, dictionaryOffset, indexOffset, deadBytes);
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
                    UncompressedSize = entry.UncompressedSize,
                    ContentHash = entry.ContentHash,
                    SourceTimestampUtcTicks = entry.SourceTimestampUtcTicks,
                    Codec = entry.Codec,
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
                writer.Write(entry.UncompressedSize);
                writer.Write(entry.ContentHash);
                writer.Write(entry.SourceTimestampUtcTicks);
                writer.Write((byte)entry.Codec);
                writer.Write((byte)0); // reserved
                writer.Write((byte)0); // reserved
                writer.Write((byte)0); // reserved
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

        private static void WriteFooter(CookedBinaryWriter writer, long tocPosition, long stringTableOffset, long dictionaryOffset, long indexOffset, long deadBytes)
        {
            writer.Write(tocPosition);
            writer.Write(stringTableOffset);
            writer.Write(dictionaryOffset);
            writer.Write(indexOffset);
            writer.Write(deadBytes);
        }

        private static unsafe void WriteAssetData(CookedBinaryWriter writer, PackedAsset asset, FileMap? sourceMap, FileStream? sourceArchiveStream, byte[]? copyBuffer)
        {
            if (asset.FromExisting)
            {
                if (sourceArchiveStream is not null && copyBuffer is not null)
                {
                    sourceArchiveStream.Position = asset.ExistingDataOffset;
                    int remaining = asset.CompressedSize;
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(copyBuffer.Length, remaining);
                        int read = sourceArchiveStream.Read(copyBuffer, 0, toRead);
                        if (read <= 0)
                            throw new EndOfStreamException("Unexpected end of source archive while copying existing asset data.");

                        writer.WriteBytes(new ReadOnlySpan<byte>(copyBuffer, 0, read));
                        remaining -= read;
                    }
                    return;
                }

                if (sourceMap is not null)
                {
                    var span = new ReadOnlySpan<byte>((byte*)sourceMap.Address + asset.ExistingDataOffset, asset.CompressedSize);
                    writer.WriteBytes(span);
                    return;
                }

                throw new InvalidOperationException("Existing asset data requires a source archive.");
            }
            else
            {
                writer.Write(asset.CompressedData!);
            }
        }

        #endregion

        #region Public API — Read

        public static byte[] GetAsset(string archiveFilePath, string assetPath)
        {
            unsafe
            {
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
                if (reader.ReadInt32() != Magic)
                    throw new InvalidOperationException("Invalid asset archive format.");

                int version = reader.ReadInt32();
                if (version != CurrentVersion)
                    throw new InvalidOperationException($"Unsupported archive version '{version}'. Only V{CurrentVersion} is supported.");

                _ = (ArchiveFlags)reader.ReadInt32();  // flags
                var mode = (TocLookupMode)reader.ReadInt32();
                int fileCount = reader.ReadInt32();
                reader.ReadInt64(); // build timestamp
                reader.ReadInt64(); // dead bytes
                var footer = ReadFooter(reader);

                reader.Position = ResolveDictionaryOffset(footer);
                var stringCompressor = new StringCompressor(reader);

                return mode switch
                {
                    TocLookupMode.HashBuckets => GetAssetFromBuckets(assetPath, fileCount, reader, stringCompressor, footer),
                    TocLookupMode.SortedByHash => GetAssetSorted(assetPath, fileCount, reader, stringCompressor, footer),
                    _ => GetAssetLinear(assetPath, fileCount, reader, stringCompressor, footer.TocPosition),
                };
            }
        }

        public static IReadOnlyList<string> GetAssetPaths(string archiveFilePath)
        {
            unsafe
            {
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
                if (reader.ReadInt32() != Magic)
                    throw new InvalidOperationException("Invalid asset archive format.");

                int version = reader.ReadInt32();
                if (version != CurrentVersion)
                    throw new InvalidOperationException($"Unsupported archive version '{version}'. Only V{CurrentVersion} is supported.");

                _ = (ArchiveFlags)reader.ReadInt32();  // flags
                _ = (TocLookupMode)reader.ReadInt32(); // lookup mode
                int fileCount = reader.ReadInt32();
                reader.ReadInt64(); // build timestamp
                reader.ReadInt64(); // dead bytes
                var footer = ReadFooter(reader);
                reader.Position = ResolveDictionaryOffset(footer);
                var stringCompressor = new StringCompressor(reader);
                return ReadAssetPaths(reader, stringCompressor, fileCount, footer.TocPosition);
            }
        }

        private static IReadOnlyList<string> ReadAssetPaths(CookedBinaryReader reader, StringCompressor stringCompressor, int fileCount, long tocPosition)
        {
            List<string> paths = new(fileCount);
            reader.Position = tocPosition;
            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                string path = NormalizePath(stringCompressor.GetString(entry.StringOffset));
                paths.Add(path);
            }

            return paths;
        }

        #endregion

        #region Internal I/O Helpers

        private static FooterInfo ReadFooter(CookedBinaryReader reader)
        {
            long saved = reader.Position;
            reader.Position = reader.Length - FooterSize;

            long tocPosition = reader.ReadInt64();
            long stringTableOffset = reader.ReadInt64();
            long dictionaryOffset = reader.ReadInt64();
            long indexOffset = reader.ReadInt64();
            long deadBytes = reader.ReadInt64();

            reader.Position = saved;
            return new FooterInfo(tocPosition, stringTableOffset, dictionaryOffset, indexOffset, deadBytes);
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

            // Zero-copy: build a span directly over the memory-mapped compressed data
            // instead of allocating + copying via ReadBytes.
            ReadOnlySpan<byte> compressedSpan = reader.GetSpan(entry.DataOffset, entry.CompressedSize);
            data = Compression.Decompress(compressedSpan, entry.Codec, (int)entry.UncompressedSize);
            return true;
        }

        private static TocEntryData ReadSequentialTocEntry(CookedBinaryReader reader)
        {
            uint hash = reader.ReadUInt32();
            int stringOffset = reader.ReadInt32();
            long dataOffset = reader.ReadInt64();
            int compressedSize = reader.ReadInt32();
            long uncompressedSize = reader.ReadInt64();
            ulong contentHash = reader.ReadUInt64();
            long sourceTimestamp = reader.ReadInt64();
            var codec = (CompressionCodec)reader.ReadByte();
            reader.ReadByte(); // reserved
            reader.ReadByte(); // reserved
            reader.ReadByte(); // reserved
            return new TocEntryData(hash, stringOffset, dataOffset, compressedSize, uncompressedSize, contentHash, sourceTimestamp, codec);
        }

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

        #endregion
    }
}
