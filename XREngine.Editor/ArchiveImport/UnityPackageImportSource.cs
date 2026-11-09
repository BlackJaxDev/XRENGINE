using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Editor;

/// <summary>
/// Extracts a Unity package into a complete, reusable source project cache.
/// </summary>
internal static partial class UnityPackageImportSource
{
    private const string CacheFormat = "unity-package-v2";
    private const string CompletionFile = ".unitypackage-complete";
    private const int MaxArchiveEntries = 100_000;
    private const long MaxArchiveEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly object CacheLock = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Gets a Unity source project from a package without changing game assets.
    /// </summary>
    public static string Prepare(string packagePath, string cacheRoot, Action<float, string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        string sourcePath = Path.GetFullPath(packagePath);
        string cacheParent = Path.Combine(Path.GetFullPath(cacheRoot), "UnityPackages");
        RejectReparsePoints(cacheParent);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{CacheFormat}:{sourceHash}"))).ToLowerInvariant();
        string target = Path.Combine(cacheParent, hash);

        lock (CacheLock)
        {
            if (IsComplete(target, hash))
            {
                progress?.Invoke(1f, "Unity package cache ready.");
                return target;
            }

            if (Directory.Exists(target) || File.Exists(target))
                throw new InvalidDataException("The Unity package cache entry is incomplete. Remove that cache entry before retrying.");

            Directory.CreateDirectory(cacheParent);
            RejectReparsePoints(cacheParent);
            string staging = Path.Combine(cacheParent, $".{hash}.{Guid.NewGuid():N}.staging");
            Directory.CreateDirectory(staging);
            try
            {
                RejectReparsePoints(staging);
                var records = ReadManifest(sourcePath);
                ValidateDestinations(records);
                Directory.CreateDirectory(Path.Combine(staging, "Assets"));
                Extract(sourcePath, staging, records, progress);
                File.WriteAllText(Path.Combine(staging, CompletionFile), $"{CacheFormat}:{sourceHash}");

                try
                {
                    PublishCacheDirectory(staging, target);
                }
                catch (IOException) when (IsComplete(target, hash))
                {
                    // Another process published the same package first.
                }

                progress?.Invoke(1f, "Unity package cache ready.");
                return target;
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static bool IsComplete(string root, string hash)
    {
        RejectReparsePoints(root);
        RejectReparsePoints(Path.Combine(root, "Assets"));
        RejectReparsePoints(Path.Combine(root, "Packages"));
        string marker = Path.Combine(root, CompletionFile);
        RejectReparsePoints(marker);
        if (!Directory.Exists(Path.Combine(root, "Assets")) ||
            !File.Exists(marker) || new FileInfo(marker).Length > 128 ||
            HasGlobalProjectManifest(root))
            return false;

        ValidateCachedRoots(root);
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(marker)))).ToLowerInvariant(),
            hash,
            StringComparison.Ordinal);
    }

    private static Dictionary<string, PackageRecord> ReadManifest(string packagePath)
    {
        var records = new Dictionary<string, PackageRecord>(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        long expandedBytes = 0;
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipInputStream(file);
        using var tar = new TarInputStream(gzip, Encoding.UTF8);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            CheckArchiveEntry(entry.Name, entry.Size, ref entryCount, ref expandedBytes);
            string? part = GetEntryPart(entry, out string folder);
            if (part is null)
                continue;

            if (!records.TryGetValue(folder, out var record))
            {
                record = new PackageRecord();
                records.Add(folder, record);
            }

            switch (part)
            {
                case "pathname":
                    if (record.Path is not null || entry.Size <= 0 || entry.Size > 16 * 1024)
                        throw new InvalidDataException("The Unity package has an invalid pathname record.");
                    using (var buffer = new MemoryStream((int)entry.Size))
                    {
                        long written = CopyLimited(tar, buffer, 16 * 1024);
                        if (written != entry.Size)
                            throw new InvalidDataException($"The Unity package pathname record '{entry.Name}' is incomplete.");
                        record.Path = ValidateAssetPath(StrictUtf8.GetString(buffer.GetBuffer().AsSpan(0, (int)written)).TrimStart('\uFEFF').TrimEnd('\0', '\r', '\n'));
                    }
                    break;
                case "asset":
                    if (record.HasAsset)
                        throw new InvalidDataException("The Unity package has a duplicate asset record.");
                    record.HasAsset = true;
                    break;
                case "asset.meta":
                    if (record.HasMeta)
                        throw new InvalidDataException("The Unity package has a duplicate metadata record.");
                    record.HasMeta = true;
                    break;
            }
        }

        if (records.Count == 0 || !records.Values.Any(static record => record.HasAsset))
            throw new InvalidDataException("The Unity package has no assets.");

        foreach (var record in records.Values)
        {
            if (record.Path is null || (!record.HasAsset && !record.HasMeta))
                throw new InvalidDataException("The Unity package has an incomplete asset record.");
        }

        foreach (string folder in records
            .Where(static pair => IsProjectManifest(pair.Value.Path))
            .Select(static pair => pair.Key)
            .ToArray())
            records.Remove(folder);

        if (records.Count == 0 || !records.Values.Any(static record => record.HasAsset))
            throw new InvalidDataException("The Unity package has no importable assets.");

        return records;
    }

    private static bool IsProjectManifest(string? path)
        => path is not null &&
           (path.Equals("Packages/manifest.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("Packages/manifest.json.meta", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("Packages/packages-lock.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("Packages/packages-lock.json.meta", StringComparison.OrdinalIgnoreCase));

    private static bool HasGlobalProjectManifest(string root)
    {
        string packages = Path.Combine(root, "Packages");
        return File.Exists(Path.Combine(packages, "manifest.json")) ||
               File.Exists(Path.Combine(packages, "manifest.json.meta")) ||
               File.Exists(Path.Combine(packages, "packages-lock.json")) ||
               File.Exists(Path.Combine(packages, "packages-lock.json.meta"));
    }

    private static void ValidateCachedRoots(string root)
    {
        // Check each cached path before another importer reads the project.
        foreach (string name in new[] { "Assets", "Packages" })
        {
            string directory = Path.Combine(root, name);
            foreach (string _ in EnumerateSafeEntries(directory))
            {
            }
        }
    }

    private static void CheckArchiveEntry(string name, long size, ref int count, ref long total)
    {
        if (++count > MaxArchiveEntries || size < 0 || size > MaxArchiveEntryBytes || size > MaxExpandedBytes - total)
            throw new InvalidDataException($"Archive entry '{name}' exceeds the import size or entry limit.");
        total += size;
    }

    private static long CopyLimited(Stream input, Stream output, long remaining)
    {
        long copied = 0;
        // Archives contain many small entries. Reuse one bounded transfer buffer
        // instead of allocating a large array for each pathname and asset.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, 64 * 1024)) > 0)
            {
                if (read > remaining - copied)
                    throw new InvalidDataException("Archive content exceeds the import size limit.");
                output.Write(buffer, 0, read);
                copied += read;
            }
            return copied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateDestinations(Dictionary<string, PackageRecord> records)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets" };
        foreach (var record in records.Values)
        {
            string path = record.Path!;
            if (!explicitPaths.Add(path))
                throw new InvalidDataException($"The Unity package has duplicate asset path '{path}'.");

            if (record.HasAsset)
            {
                if (!files.Add(path))
                    throw new InvalidDataException($"The Unity package has duplicate asset path '{path}'.");
            }
            else
            {
                directories.Add(path);
            }

            if (record.HasMeta && !files.Add(path + ".meta"))
                throw new InvalidDataException($"The Unity package has duplicate metadata path '{path}.meta'.");

            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                directories.Add(path[..slash]);
                slash = path.IndexOf('/', slash + 1);
            }
        }

        if (files.Overlaps(directories))
            throw new InvalidDataException("The Unity package uses a path as both a file and a folder.");
    }

    private static void Extract(string packagePath, string root, Dictionary<string, PackageRecord> records, Action<float, string>? progress)
    {
        int copied = 0;
        int expected = records.Values.Sum(static record => (record.HasAsset ? 1 : 0) + (record.HasMeta ? 1 : 0));
        int entryCount = 0;
        long expandedBytes = 0;
        long writtenBytes = 0;
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipInputStream(file);
        using var tar = new TarInputStream(gzip, Encoding.UTF8);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            CheckArchiveEntry(entry.Name, entry.Size, ref entryCount, ref expandedBytes);
            string? part = GetEntryPart(entry, out string folder);
            if (part is null || !records.TryGetValue(folder, out var record))
                continue;

            if (part == "asset" && !record.HasAsset)
                continue;
            if (part == "asset.meta" && !record.HasMeta)
                continue;
            if (part is not ("asset" or "asset.meta"))
                continue;

            string relative = record.Path! + (part == "asset.meta" ? ".meta" : "");
            string destination = GetDestination(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long written = CopyLimited(tar, output, Math.Min(MaxArchiveEntryBytes, MaxExpandedBytes - writtenBytes));
                if (written != entry.Size)
                    throw new InvalidDataException($"Unity package entry '{entry.Name}' is incomplete.");
                writtenBytes += written;
            }

            copied++;
            progress?.Invoke(0.95f * copied / expected, $"Extracted {relative}");
        }

        if (copied != expected)
            throw new InvalidDataException("The Unity package changed during extraction.");

        foreach (var record in records.Values)
        {
            if (!record.HasAsset)
                Directory.CreateDirectory(GetDestination(root, record.Path!));
        }
    }

    private static string? GetEntryPart(TarEntry entry, out string folder)
    {
        folder = string.Empty;
        byte type = entry.TarHeader.TypeFlag;
        if (type != 0 && type != (byte)'0' && type != (byte)'5')
            throw new InvalidDataException("The Unity package contains an unsupported TAR entry type.");

        string name = entry.Name.Replace('\\', '/');
        ValidateSegments(name, allowTrailingSlash: entry.IsDirectory);
        var parts = name.TrimEnd('/').Split('/');
        if (parts.Length == 1 && entry.IsDirectory && IsGuidFolder(parts[0]))
            return null;
        if (parts.Length != 2 || !IsGuidFolder(parts[0]))
            throw new InvalidDataException($"The Unity package has an invalid entry path '{name}'.");

        folder = parts[0];
        if (entry.IsDirectory)
            return null;
        return parts[1];
    }

    private static bool IsGuidFolder(string name)
    {
        if (name.Length != 32)
            return false;
        foreach (char ch in name)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }
        return true;
    }

    private static string ValidateAssetPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        ValidateSegments(normalized, allowTrailingSlash: false);
        if (normalized != "Assets" && normalized != "Packages" &&
            !normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
            !normalized.StartsWith("Packages/", StringComparison.Ordinal))
            throw new InvalidDataException($"The Unity package asset path '{normalized}' must be under Assets or Packages.");
        return normalized;
    }

    private static void ValidateSegments(string path, bool allowTrailingSlash)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || Path.IsPathRooted(path) || path.Contains('\0'))
            throw new InvalidDataException($"Archive path '{path}' is unsafe.");

        string[] segments = (allowTrailingSlash ? path.TrimEnd('/') : path).Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(segment))
                throw new InvalidDataException($"Archive path '{path}' is unsafe.");
        }
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    private static string GetDestination(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Unity package path escapes the cache.");
        return path;
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The Unity package cache path '{current}' uses a reparse point.");
        }
    }

    private sealed class PackageRecord
    {
        public string? Path { get; set; }
        public bool HasAsset { get; set; }
        public bool HasMeta { get; set; }
    }
}
