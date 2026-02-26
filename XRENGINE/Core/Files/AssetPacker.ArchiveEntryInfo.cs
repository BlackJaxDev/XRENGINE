using XREngine.Data;

namespace XREngine.Core.Files
{
    public static partial class AssetPacker
    {
        /// <summary>
        /// Describes a single entry in an asset archive's table of contents.
        /// </summary>
        public readonly struct ArchiveEntryInfo(
            string path,
            uint hash,
            long dataOffset,
            int compressedSize,
            long uncompressedSize = 0,
            ulong contentHash = 0,
            long sourceTimestampUtcTicks = 0,
            CompressionCodec codec = CompressionCodec.Lzma)
        {
            /// <summary>Relative path of the asset within the archive.</summary>
            public string Path { get; } = path;
            /// <summary>DJB2 hash of the normalized path.</summary>
            public uint Hash { get; } = hash;
            /// <summary>Absolute byte offset of the compressed data within the archive file.</summary>
            public long DataOffset { get; } = dataOffset;
            /// <summary>Size in bytes of the compressed payload.</summary>
            public int CompressedSize { get; } = compressedSize;
            /// <summary>Size in bytes of the uncompressed data.</summary>
            public long UncompressedSize { get; } = uncompressedSize;
            /// <summary>XXH64 hash of the original uncompressed content.</summary>
            public ulong ContentHash { get; } = contentHash;
            /// <summary>Source file last-write timestamp as UTC ticks.</summary>
            public long SourceTimestampUtcTicks { get; } = sourceTimestampUtcTicks;
            /// <summary>Compression algorithm used to encode this entry.</summary>
            public CompressionCodec Codec { get; } = codec;
        }
        
        /// <summary>
        /// Decompresses a single entry from an already-opened archive. This reads the compressed bytes
        /// from disk and returns the decompressed content.
        /// </summary>
        /// <param name="archiveFilePath">Path to the <c>.pak</c> file.</param>
        /// <param name="entry">The entry to decompress (from <see cref="ArchiveInfo.Entries"/>).</param>
        /// <returns>The decompressed bytes.</returns>
        public static byte[] DecompressEntry(string archiveFilePath, ArchiveEntryInfo entry)
        {
            unsafe
            {
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
                ReadOnlySpan<byte> compressedSpan = reader.GetSpan(entry.DataOffset, entry.CompressedSize);
                return Compression.Decompress(compressedSpan, entry.Codec, (int)entry.UncompressedSize);
            }
        }
    }
}
