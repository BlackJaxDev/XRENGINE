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
        /// <exception cref="InvalidDataException">The file is not a valid asset archive or uses another format version.</exception>
        public static ArchiveInfo ReadArchiveInfo(string archiveFilePath)
        {
            using PublishedArchiveHandle handle = PublishedArchiveHandle.Open(archiveFilePath);
            ArchiveEntryInfo[] entries = new ArchiveEntryInfo[handle.EntryCount];
            long totalCompressed = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = handle.GetEntry(i);
                totalCompressed += entries[i].CompressedSize;
            }

            return new ArchiveInfo
            {
                FilePath = handle.FilePath,
                FileSize = handle.FileSize,
                MagicNumber = Magic,
                Version = CurrentVersion,
                Flags = handle.Flags,
                LookupMode = handle.LookupMode,
                FileCount = handle.EntryCount,
                BuildTimestampUtcTicks = handle.BuildTimestampUtcTicks,
                DeadBytes = handle.DeadBytes,
                TocOffset = handle.TocOffset,
                StringTableOffset = handle.StringTableOffset,
                IndexTableOffset = handle.IndexTableOffset,
                TotalCompressedBytes = totalCompressed,
                Entries = entries,
            };
        }
    }
}
