using XREngine.Core.Files;
using XREngine.Data;
using static XREngine.Core.Files.AssetPacker;

namespace XREngine.Editor;

public static partial class AssetArchiveAuthoring
{
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
}
