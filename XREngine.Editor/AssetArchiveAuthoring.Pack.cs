using System.IO.Hashing;
using XREngine.Core.Files;
using XREngine.Data;
using static XREngine.Core.Files.AssetPacker;

namespace XREngine.Editor;

public static partial class AssetArchiveAuthoring
{
    /// <summary>Packs the source directory into a cooked asset archive.</summary>
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

        string[] files = XREngine.Data.RuntimeFileDiscoveryServices.Required.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories).ToArray();
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

                        Action<long>? chunkProgress = null;
                        if (progress is not null && DefaultCodec == CompressionCodec.Lzma && data.Length >= LargeFileThreshold)
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

                        byte[] compressed = CompressForArchive(data, chunkProgress, out CompressionCodec codec);

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

    /// <summary>
    /// Encodes one entry with <see cref="DefaultCodec"/>, using chunked parallel LZMA for large
    /// files. When <see cref="StoreIncompressibleEntries"/> is set and the encoded form is not
    /// smaller than the source, the source bytes are returned with <see cref="CompressionCodec.Stored"/>.
    /// </summary>
    private static byte[] CompressForArchive(byte[] data, Action<long>? progress, out CompressionCodec codec)
    {
        codec = DefaultCodec;
        byte[] compressed = codec == CompressionCodec.Lzma && data.Length >= LargeFileThreshold
            ? Compression.CompressChunked(data, progress: progress)
            : Compression.Compress(data, codec);

        if (StoreIncompressibleEntries && compressed.Length >= data.Length)
        {
            codec = CompressionCodec.Stored;
            return data;
        }

        return compressed;
    }
}
