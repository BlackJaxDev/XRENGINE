using XREngine.Core.Files;
using XREngine.Data;
using static XREngine.Core.Files.AssetPacker;

namespace XREngine.Editor;

/// <summary>Creates and updates cooked asset archives for the Editor.</summary>
public static partial class AssetArchiveAuthoring
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

    public static TocLookupMode DefaultLookupMode { get; set; } = TocLookupMode.HashBuckets;

    /// <summary>
    /// Default compression codec used when packing new archives.
    /// Existing entries keep their original codec during repack.
    /// </summary>
    public static CompressionCodec DefaultCodec { get; set; } = CompressionCodec.Lzma;

    /// <summary>
    /// When enabled, entries whose compressed form is not smaller than the source are written
    /// with <see cref="CompressionCodec.Stored"/>, so the published reader can return a span over
    /// the mapped archive instead of decompressing. Off by default so authoring archives keep a
    /// single codec; the published cook enables it.
    /// </summary>
    public static bool StoreIncompressibleEntries { get; set; }

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
}
