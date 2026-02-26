using XREngine.Data;

namespace XREngine.Core.Files
{
    public static partial class AssetPacker
    {
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
            /// <summary>Archive format version.</summary>
            public int Version { get; init; }
            /// <summary>Format flags.</summary>
            public ArchiveFlags Flags { get; init; }
            /// <summary>TOC lookup mode.</summary>
            public TocLookupMode LookupMode { get; init; }
            /// <summary>Number of files stored in the archive.</summary>
            public int FileCount { get; init; }
            /// <summary>UTC ticks when the archive was built.</summary>
            public long BuildTimestampUtcTicks { get; init; }
            /// <summary>Total dead (orphaned) bytes in the data region.</summary>
            public long DeadBytes { get; init; }
            /// <summary>Absolute byte offset where the TOC starts.</summary>
            public long TocOffset { get; init; }
            /// <summary>Absolute byte offset where the string table starts.</summary>
            public long StringTableOffset { get; init; }
            /// <summary>Absolute byte offset where the bucket/index table starts (0 if absent).</summary>
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
                if (version != CurrentVersion)
                    throw new InvalidOperationException($"Unsupported archive version '{version}'. Only V{CurrentVersion} is supported.");

                var flags = (ArchiveFlags)reader.ReadInt32();
                var lookupMode = (TocLookupMode)reader.ReadInt32();
                int fileCount = reader.ReadInt32();
                long buildTimestamp = reader.ReadInt64();
                long headerDeadBytes = reader.ReadInt64();

                var footer = ReadFooter(reader);
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
                    entries[i] = new ArchiveEntryInfo(path, toc.Hash, toc.DataOffset, toc.CompressedSize,
                        toc.UncompressedSize, toc.ContentHash, toc.SourceTimestampUtcTicks, toc.Codec);
                    totalCompressed += toc.CompressedSize;
                }

                return new ArchiveInfo
                {
                    FilePath = archiveFilePath,
                    FileSize = fileSize,
                    MagicNumber = magic,
                    Version = version,
                    Flags = flags,
                    LookupMode = lookupMode,
                    FileCount = fileCount,
                    BuildTimestampUtcTicks = buildTimestamp,
                    DeadBytes = footer.DeadBytes,
                    TocOffset = footer.TocPosition,
                    StringTableOffset = footer.StringTableOffset,
                    IndexTableOffset = footer.IndexTableOffset,
                    TotalCompressedBytes = totalCompressed,
                    Entries = entries,
                };
            }
        }
    }
}
