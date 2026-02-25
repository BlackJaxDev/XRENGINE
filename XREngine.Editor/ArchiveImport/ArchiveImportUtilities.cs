using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace XREngine.Editor;

internal sealed class ArchiveEntryNode
{
    public ArchiveEntryNode(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; private set; }
    public long Size { get; private set; }
    public List<ArchiveEntryNode> Children { get; } = new();

    public void PromoteToDirectory() => IsDirectory = true;
    public void SetSize(long size) => Size = size;
}

internal readonly record struct ArchiveExtractionProgress(float Progress, string Message, string? CurrentItem);

internal sealed record UnityPackageAssetRecord(string InternalFolder, string FinalPath, bool HasMeta, long AssetSize);

internal sealed class ArchiveTreeResult
{
    public ArchiveTreeResult(ArchiveEntryNode root, bool isUnityPackage, Dictionary<string, UnityPackageAssetRecord>? unityEntries)
    {
        Root = root;
        IsUnityPackage = isUnityPackage;
        UnityEntries = unityEntries;
    }

    public ArchiveEntryNode Root { get; }
    public bool IsUnityPackage { get; }
    public Dictionary<string, UnityPackageAssetRecord>? UnityEntries { get; }
}

internal static class ArchiveImportUtilities
{
    public static ArchiveTreeResult BuildArchiveTree(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path must be provided.", nameof(archivePath));

        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found.", archivePath);

        return IsUnityPackage(archivePath)
            ? BuildUnityPackageTree(archivePath)
            : BuildStandardArchiveTree(archivePath);
    }

    public static IEnumerable<ArchiveExtractionProgress> ExtractSelectedEntries(
        string archivePath,
        IReadOnlyCollection<string> selectedEntries,
        string destinationRoot,
        ArchiveTreeResult? context,
        CancellationToken cancellationToken = default)
        => context?.IsUnityPackage == true && context.UnityEntries is not null
            ? ExtractUnityPackageEntries(archivePath, selectedEntries, destinationRoot, context.UnityEntries, cancellationToken)
            : ExtractStandardEntries(archivePath, selectedEntries, destinationRoot, cancellationToken);

    private static ArchiveTreeResult BuildStandardArchiveTree(string archivePath)
    {
        var root = new ArchiveEntryNode("/", string.Empty, true);

        using var archive = ArchiveFactory.OpenArchive(archivePath, new SharpCompress.Readers.ReaderOptions());
        foreach (var entry in archive.Entries)
        {
            if (entry is null)
                continue;

            var normalizedKey = NormalizeKey(entry.Key);
            if (string.IsNullOrEmpty(normalizedKey))
                continue;

            AddNode(root, normalizedKey, entry.IsDirectory, entry.Size);
        }

        SortTree(root);
        return new ArchiveTreeResult(root, false, null);
    }

    private static ArchiveTreeResult BuildUnityPackageTree(string archivePath)
    {
        var root = new ArchiveEntryNode("/", string.Empty, true);
        var builders = new Dictionary<string, UnityPackageEntryBuilder>(StringComparer.OrdinalIgnoreCase);

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzipStream = new GZipInputStream(fileStream);
        using var tarStream = new TarInputStream(gzipStream, Encoding.Default);

        TarEntry? entry;
        while ((entry = tarStream.GetNextEntry()) != null)
        {
            if (entry.IsDirectory)
            {
                DrainEntry(tarStream);
                continue;
            }

            string normalized = NormalizeKey(entry.Name);
            if (string.IsNullOrEmpty(normalized))
            {
                DrainEntry(tarStream);
                continue;
            }

            string? folder = GetTopLevelFolder(normalized);
            if (string.IsNullOrEmpty(folder))
            {
                DrainEntry(tarStream);
                continue;
            }

            if (!builders.TryGetValue(folder, out var builder))
            {
                builder = new UnityPackageEntryBuilder();
                builders[folder] = builder;
            }

            if (normalized.EndsWith("/pathname", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                tarStream.CopyEntryContents(ms);
                builder.Pathname = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                continue;
            }

            if (normalized.EndsWith("/asset.meta", StringComparison.OrdinalIgnoreCase))
            {
                builder.HasMeta = true;
                DrainEntry(tarStream);
                continue;
            }

            if (normalized.EndsWith("/asset", StringComparison.OrdinalIgnoreCase))
            {
                builder.AssetSize = entry.Size;
                DrainEntry(tarStream);
                continue;
            }

            DrainEntry(tarStream);
        }

        var unityEntries = new Dictionary<string, UnityPackageAssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, builder) in builders)
        {
            if (string.IsNullOrWhiteSpace(builder.Pathname))
                continue;

            string normalizedPath = NormalizeKey(builder.Pathname);
            if (string.IsNullOrEmpty(normalizedPath))
                continue;

            AddNode(root, normalizedPath, false, builder.AssetSize);
            unityEntries[normalizedPath] = new UnityPackageAssetRecord(folder, normalizedPath, builder.HasMeta, builder.AssetSize);
        }

        SortTree(root);
        return new ArchiveTreeResult(root, true, unityEntries);
    }

    private static IEnumerable<ArchiveExtractionProgress> ExtractStandardEntries(
        string archivePath,
        IReadOnlyCollection<string> selectedEntries,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        if (selectedEntries is null)
            throw new ArgumentNullException(nameof(selectedEntries));
        if (selectedEntries.Count == 0)
            yield break;

        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("Destination path must be provided.", nameof(destinationRoot));

        var normalizedDestination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(normalizedDestination);

        var selection = new HashSet<string>(selectedEntries.Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
        int processed = 0;
        int total = selection.Count;

        using var archive = ArchiveFactory.OpenArchive(archivePath, new SharpCompress.Readers.ReaderOptions());
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                continue;

            var key = NormalizeKey(entry.Key);
            if (!selection.Contains(key))
                continue;

            string destinationPath = Path.Combine(normalizedDestination, key.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            yield return new ArchiveExtractionProgress(
                processed / (float)Math.Max(total, 1),
                $"Extracting {key}",
                key);

            using (var output = File.Create(destinationPath))
            {
                entry.WriteTo(output);
            }

            processed++;
            yield return new ArchiveExtractionProgress(
                processed / (float)Math.Max(total, 1),
                $"Imported {key}",
                key);
        }

        yield return new ArchiveExtractionProgress(1f, "Import complete.", null);
    }

    private static IEnumerable<ArchiveExtractionProgress> ExtractUnityPackageEntries(
        string archivePath,
        IReadOnlyCollection<string> selectedEntries,
        string destinationRoot,
        IReadOnlyDictionary<string, UnityPackageAssetRecord> records,
        CancellationToken cancellationToken)
    {
        if (selectedEntries is null)
            throw new ArgumentNullException(nameof(selectedEntries));
        if (selectedEntries.Count == 0)
            yield break;

        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("Destination path must be provided.", nameof(destinationRoot));

        var normalizedDestination = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(normalizedDestination);

        var selection = new HashSet<string>(selectedEntries.Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
        var targets = records
            .Where(kvp => selection.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Value.InternalFolder, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        if (targets.Count == 0)
            yield break;

        int processed = 0;
        int total = targets.Count;

        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzipStream = new GZipInputStream(fileStream);
        using var tarStream = new TarInputStream(gzipStream, Encoding.Default);

        TarEntry? entry;
        while ((entry = tarStream.GetNextEntry()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalized = NormalizeKey(entry.Name);
            if (string.IsNullOrEmpty(normalized))
            {
                DrainEntry(tarStream);
                continue;
            }

            string? folder = GetTopLevelFolder(normalized);
            if (folder is null || !targets.TryGetValue(folder, out var target))
            {
                DrainEntry(tarStream);
                continue;
            }

            string remainder = normalized.Length > folder.Length
                ? normalized[(folder.Length + 1)..]
                : string.Empty;

            if (string.Equals(remainder, "asset", StringComparison.OrdinalIgnoreCase))
            {
                string destinationPath = Path.Combine(normalizedDestination, target.FinalPath.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                yield return new ArchiveExtractionProgress(
                    processed / (float)Math.Max(total, 1),
                    $"Extracting {target.FinalPath}",
                    target.FinalPath);

                using (var output = File.Create(destinationPath))
                {
                    tarStream.CopyEntryContents(output);
                }

                processed++;
                yield return new ArchiveExtractionProgress(
                    processed / (float)Math.Max(total, 1),
                    $"Imported {target.FinalPath}",
                    target.FinalPath);
                continue;
            }

            if (string.Equals(remainder, "asset.meta", StringComparison.OrdinalIgnoreCase) && target.HasMeta)
            {
                string metaPath = Path.Combine(normalizedDestination, target.FinalPath.Replace('/', Path.DirectorySeparatorChar) + ".meta");
                var directory = Path.GetDirectoryName(metaPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var output = File.Create(metaPath))
                {
                    tarStream.CopyEntryContents(output);
                }
                continue;
            }

            DrainEntry(tarStream);
        }

        yield return new ArchiveExtractionProgress(1f, "Import complete.", null);
    }

    private static void AddNode(ArchiveEntryNode root, string key, bool isDirectory, long size)
    {
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var parent = root;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;
            bool childIsDirectory = last ? isDirectory : true;

            var child = FindOrCreateChild(parent, part, childIsDirectory);
            if (last && !child.IsDirectory)
                child.SetSize(size);

            parent = child;
        }
    }

    private static ArchiveEntryNode FindOrCreateChild(ArchiveEntryNode parent, string name, bool isDirectory)
    {
        foreach (var child in parent.Children)
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (isDirectory)
                    child.PromoteToDirectory();
                return child;
            }
        }

        string childPath = string.IsNullOrEmpty(parent.FullPath)
            ? name
            : string.Concat(parent.FullPath, "/", name);

        var created = new ArchiveEntryNode(name, childPath, isDirectory);
        parent.Children.Add(created);
        return created;
    }

    private static void SortTree(ArchiveEntryNode node)
    {
        node.Children.Sort(static (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        });

        foreach (var child in node.Children)
            SortTree(child);
    }

    private static bool IsUnityPackage(string path)
        => path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var normalized = key.Replace('\\', '/').Trim('/');
        return normalized;
    }

    private static string? GetTopLevelFolder(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        int slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }

    private static void DrainEntry(TarInputStream stream)
    {
        var buffer = new byte[8192];
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    private sealed class UnityPackageEntryBuilder
    {
        public string? Pathname { get; set; }
        public bool HasMeta { get; set; }
        public long AssetSize { get; set; }
    }
}
