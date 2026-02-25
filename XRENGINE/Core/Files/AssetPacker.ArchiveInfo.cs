using XREngine.Data;

namespace XREngine.Core.Files
{
    public static partial class AssetPacker
    {
        /// <summary>
        /// Describes a single entry in an asset archive's table of contents.
        /// </summary>
        public readonly struct ArchiveEntryInfo(string path, uint hash, long dataOffset, int compressedSize)
        {
            /// <summary>Relative path of the asset within the archive.</summary>
            public string Path { get; } = path;
            /// <summary>DJB2 hash of the normalized path.</summary>
            public uint Hash { get; } = hash;
            /// <summary>Absolute byte offset of the compressed data within the archive file.</summary>
            public long DataOffset { get; } = dataOffset;
            /// <summary>Size in bytes of the LZMA-compressed payload.</summary>
            public int CompressedSize { get; } = compressedSize;
        }

        /// <summary>
        /// Complete metadata for an asset archive (.pak) file, including header fields and all TOC entries.
        /// </summary>
        public sealed class ArchiveInfo
        {
            /// <summary>Archive file path on disk.</summary>
            public string FilePath { get; init; } = string.Empty;
            /// <summary>Total file size in bytes.</summary>
            public long FileSize { get; init; }
            /// <summary>Magic number read from the header (expected <c>0x4652454B</c> / "FREK").</summary>
            public int MagicNumber { get; init; }
            /// <summary>Archive format version (1 or 2).</summary>
            public int Version { get; init; }
            /// <summary>TOC lookup mode (only meaningful for V2 archives).</summary>
            public TocLookupMode LookupMode { get; init; }
            /// <summary>Number of files stored in the archive.</summary>
            public int FileCount { get; init; }
            /// <summary>Absolute byte offset where the TOC starts.</summary>
            public long TocOffset { get; init; }
            /// <summary>Absolute byte offset where the string table starts.</summary>
            public long StringTableOffset { get; init; }
            /// <summary>Absolute byte offset where the bucket/index table starts (V2 only, 0 if absent).</summary>
            public long IndexTableOffset { get; init; }
            /// <summary>Sum of all compressed entry sizes.</summary>
            public long TotalCompressedBytes { get; init; }
            /// <summary>All TOC entries with their paths resolved from the string table.</summary>
            public ArchiveEntryInfo[] Entries { get; init; } = [];
        }

        /// <summary>
        /// Reads the complete metadata and table of contents from an asset archive without decompressing any data.
        /// </summary>
        /// <param name="archiveFilePath">Path to the <c>.pak</c> file.</param>
        /// <returns>An <see cref="ArchiveInfo"/> describing the archive.</returns>
        /// <exception cref="InvalidOperationException">The file is not a valid asset archive.</exception>
        public static ArchiveInfo ReadArchiveInfo(string archiveFilePath)
        {
            unsafe
            {
                long fileSize = new FileInfo(archiveFilePath).Length;
                using FileMap map = FileMap.FromFile(archiveFilePath, FileMapProtect.Read);
                using var reader = new CookedBinaryReader((byte*)map.Address.Pointer, map.Length);

                int magic = reader.ReadInt32();
                if (magic != Magic)
                    throw new InvalidOperationException("Invalid asset archive format — magic number mismatch.");

                int version = reader.ReadInt32();

                TocLookupMode lookupMode = TocLookupMode.Linear;
                int fileCount;

                if (version >= VersionV2)
                {
                    lookupMode = (TocLookupMode)reader.ReadInt32();
                    fileCount = reader.ReadInt32();
                }
                else if (version == VersionV1)
                {
                    fileCount = reader.ReadInt32();
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported archive version '{version}'.");
                }

                var footer = ReadFooter(reader, version);
                reader.Position = ResolveDictionaryOffset(footer);
                var stringCompressor = new StringCompressor(reader);

                // Read all TOC entries.
                reader.Position = footer.TocPosition;
                var entries = new ArchiveEntryInfo[fileCount];
                long totalCompressed = 0;

                for (int i = 0; i < fileCount; i++)
                {
                    var toc = ReadSequentialTocEntry(reader);
                    string path = stringCompressor.GetString(toc.StringOffset);
                    entries[i] = new ArchiveEntryInfo(path, toc.Hash, toc.DataOffset, toc.CompressedSize);
                    totalCompressed += toc.CompressedSize;
                }

                return new ArchiveInfo
                {
                    FilePath = archiveFilePath,
                    FileSize = fileSize,
                    MagicNumber = magic,
                    Version = version,
                    LookupMode = lookupMode,
                    FileCount = fileCount,
                    TocOffset = footer.TocPosition,
                    StringTableOffset = footer.StringTableOffset,
                    IndexTableOffset = footer.IndexTableOffset,
                    TotalCompressedBytes = totalCompressed,
                    Entries = entries,
                };
            }
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
                reader.Position = entry.DataOffset;
                byte[] compressedData = reader.ReadBytes(entry.CompressedSize);
                return Compression.Decompress(compressedData, true);
            }
        }
    }
}
