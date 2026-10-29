using System.Text;
using XREngine.Data;

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

    /// <summary>Reads cooked asset archives and owns their shared format helpers.</summary>
    public static partial class AssetPacker
    {
        internal const int Magic = 0x4652454B; // "FREK"
        private static readonly Encoding StringEncoding = Encoding.UTF8;
        internal const int CurrentVersion = 5;
        internal const int TocEntrySize = sizeof(uint) + sizeof(int) + sizeof(long) + sizeof(int)
            + sizeof(long) + sizeof(ulong) + sizeof(long) + sizeof(byte) + 3;
        internal const int FooterSize = sizeof(long) * 5;
        internal const int HeaderSize = sizeof(int) * 5 + sizeof(long) * 2;

        /// <summary>Describes an archive version that this reader cannot use.</summary>
        internal static string DescribeUnsupportedVersion(string archiveFilePath, int version)
            => $"Archive '{archiveFilePath}' uses format version {version}, but this build reads version {CurrentVersion}. Re-cook the content with the current editor build; earlier archives are not readable.";

        internal static string NormalizePath(string path)
            => path.Replace('\\', '/');

        #region Public API — Read

        /// <summary>
        /// One-shot read for tooling: opens a temporary handle, decompresses the entry into a new
        /// array, and closes the archive. Runtime loads go through <see cref="PublishedArchiveRegistry"/>.
        /// </summary>
        public static byte[] GetAsset(string archiveFilePath, string assetPath)
        {
            using PublishedArchiveHandle handle = PublishedArchiveHandle.Open(archiveFilePath);
            return handle.ReadAssetBytes(assetPath);
        }

        /// <summary>One-shot listing of every asset path in the archive.</summary>
        public static IReadOnlyList<string> GetAssetPaths(string archiveFilePath)
        {
            using PublishedArchiveHandle handle = PublishedArchiveHandle.Open(archiveFilePath);
            return handle.GetAssetPaths();
        }

        #endregion

        #region Internal I/O Helpers

        internal static FooterInfo ReadFooter(CookedBinaryReader reader)
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

        internal static long ResolveDictionaryOffset(FooterInfo footer)
            => footer.DictionaryOffset < footer.StringTableOffset
                ? footer.StringTableOffset + footer.DictionaryOffset
                : footer.DictionaryOffset;

        internal static TocEntryData ReadSequentialTocEntry(CookedBinaryReader reader)
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

        internal static uint FastHash(string input)
        {
            uint hash = 5381;
            foreach (char c in input)
                hash = ((hash << 5) + hash) ^ c;
            return hash;
        }

        #endregion
    }
}
