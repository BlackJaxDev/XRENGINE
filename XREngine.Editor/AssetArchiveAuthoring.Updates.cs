using System.IO.Hashing;
using XREngine.Core.Files;
using XREngine.Data;
using static XREngine.Core.Files.AssetPacker;

namespace XREngine.Editor;

public static partial class AssetArchiveAuthoring
{
    /// <summary>Adds or removes files and keeps the archive format and existing entry codecs.</summary>
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
                        throw new InvalidOperationException(DescribeUnsupportedVersion(archiveFilePath, version));

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

        foreach (string filePath in XREngine.Data.RuntimeFileDiscoveryServices.Required.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizePath(Path.GetRelativePath(inputDir, filePath));
            byte[] rawData = File.ReadAllBytes(filePath);
            ulong contentHash = XxHash64.HashToUInt64(rawData);
            long sourceTimestamp = File.GetLastWriteTimeUtc(filePath).Ticks;

            byte[] compressed = CompressForArchive(rawData, progress: null, out CompressionCodec codec);

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
                        throw new InvalidOperationException(DescribeUnsupportedVersion(archiveFilePath, version));

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
                throw new InvalidOperationException(DescribeUnsupportedVersion(archiveFilePath, version));

            _ = (ArchiveFlags)reader.ReadInt32();
            _ = (TocLookupMode)reader.ReadInt32();
            int fileCount = reader.ReadInt32();
            reader.ReadInt64(); // build timestamp
            reader.ReadInt64(); // dead bytes

            var footer = ReadFooter(reader);
            reader.Position = ResolveDictionaryOffset(footer);
            var stringCompressor = new StringCompressor(reader);

            reader.Position = footer.TocPosition;
            IRuntimeHostFileReadBackend? hostFileReads = null;
            for (int i = 0; i < fileCount; i++)
            {
                var entry = ReadSequentialTocEntry(reader);
                string path = NormalizePath(stringCompressor.GetString(entry.StringOffset));
                string fullPath = Path.Combine(sourceDir, path.Replace('/', Path.DirectorySeparatorChar));

                hostFileReads ??= RuntimeFileDiscoveryServices.CaptureHostFileReadBackend();
                if (!hostFileReads.FileExists(fullPath))
                {
                    stale.Add(path);
                    continue;
                }

                // Compare XXH64 content hash
                byte[] sourceData = hostFileReads.ReadAllBytes(fullPath);
                ulong sourceHash = XxHash64.HashToUInt64(sourceData);
                if (sourceHash != entry.ContentHash)
                    stale.Add(path);
            }
        }

        return stale;
    }
}
