using XREngine.Data;

namespace XREngine.Core.Files
{
    public static partial class AssetPacker
    {
        /// <summary>
        /// TOC entry data read from an archive.
        /// </summary>
        private readonly struct TocEntryData(
            uint hash, int stringOffset, long dataOffset, int compressedSize,
            long uncompressedSize = 0, ulong contentHash = 0, long sourceTimestampUtcTicks = 0,
            CompressionCodec codec = CompressionCodec.Lzma)
        {
            public uint Hash { get; } = hash;
            public int StringOffset { get; } = stringOffset;
            public long DataOffset { get; } = dataOffset;
            public int CompressedSize { get; } = compressedSize;
            /// <summary>Size of uncompressed data in bytes.</summary>
            public long UncompressedSize { get; } = uncompressedSize;
            /// <summary>XXH64 hash of uncompressed content.</summary>
            public ulong ContentHash { get; } = contentHash;
            /// <summary>Source file last-write UTC ticks.</summary>
            public long SourceTimestampUtcTicks { get; } = sourceTimestampUtcTicks;
            /// <summary>Compression algorithm used to encode this entry's data.</summary>
            public CompressionCodec Codec { get; } = codec;
        }
    }
}
