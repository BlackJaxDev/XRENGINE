using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XREngine.Editor;

internal static partial class UnityPackageImportSource
{
    private const string CombinedCompletionFile = ".unityproject-complete";
    private const string CombinedFormat = "unity-project-v2";

    /// <summary>
    /// Builds a cached source project with companion packages. Hierarchy paths are relative to ProjectRoot.
    /// </summary>
    public static UnityPackageImportProject PrepareProject(
        string packagePath,
        string cacheRoot,
        IReadOnlyList<string> dependencyPaths,
        Action<float, string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(dependencyPaths);

        string primaryRoot = Prepare(packagePath, cacheRoot, progress);
        IReadOnlyList<string> hierarchies = FindPrimaryHierarchies(primaryRoot);
        if (dependencyPaths.Count == 0)
            return new UnityPackageImportProject(primaryRoot, hierarchies);

        var dependencies = new List<DependencySource>(dependencyPaths.Count);
        foreach (string path in dependencyPaths)
            dependencies.Add(InspectDependency(path, cacheRoot));

        string combinedHash = ComputeCombinedHash(Path.GetFileName(primaryRoot), dependencies);
        string parent = Path.Combine(Path.GetFullPath(cacheRoot), "UnityProjects");
        string target = Path.Combine(parent, combinedHash);

        lock (CacheLock)
        {
            RejectReparsePoints(parent);
            if (IsCombinedComplete(target, combinedHash))
                return new UnityPackageImportProject(target, hierarchies);
            if (Directory.Exists(target) || File.Exists(target))
                throw new InvalidDataException($"The combined Unity project cache entry '{combinedHash}' is incomplete.");

            Directory.CreateDirectory(parent);
            RejectReparsePoints(parent);
            string staging = Path.Combine(parent, $".{combinedHash}.{Guid.NewGuid():N}.staging");
            Directory.CreateDirectory(staging);
            try
            {
                RejectReparsePoints(staging);
                var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CopyProjectRoots(primaryRoot, staging, $"{packagePath} (primary)", origins, canonicalPaths);
                for (int i = 0; i < dependencies.Count; i++)
                {
                    DependencySource dependency = dependencies[i];
                    progress?.Invoke(0.95f * i / dependencies.Count, $"Adding companion source {i + 1} of {dependencies.Count}.");
                    AddDependency(dependency, staging, origins, canonicalPaths, i + 1);
                    if (dependency.Kind == DependencyKind.Folder &&
                        !string.Equals(ComputeFolderHash(dependency.Path, dependency.IsProject), dependency.Hash, StringComparison.Ordinal))
                        throw new IOException($"Companion source '{dependency.Path}' changed during import.");
                    if (dependency.Kind == DependencyKind.Zip &&
                        !string.Equals(ComputeFileHash(dependency.Path), dependency.Hash, StringComparison.Ordinal))
                        throw new IOException($"Companion archive '{dependency.Path}' changed during import.");
                }

                ValidateGuids(staging, origins);
                File.WriteAllText(Path.Combine(staging, CombinedCompletionFile), combinedHash);
                try
                {
                    Directory.Move(staging, target);
                }
                catch (IOException) when (IsCombinedComplete(target, combinedHash))
                {
                    // Another process published the same project first.
                }

                progress?.Invoke(1f, "Unity companion sources are ready.");
                return new UnityPackageImportProject(target, hierarchies);
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static bool IsCombinedComplete(string root, string hash)
    {
        RejectReparsePoints(root);
        RejectReparsePoints(Path.Combine(root, "Assets"));
        RejectReparsePoints(Path.Combine(root, "Packages"));
        string marker = Path.Combine(root, CombinedCompletionFile);
        RejectReparsePoints(marker);
        if (!Directory.Exists(Path.Combine(root, "Assets")) ||
            !File.Exists(marker) || new FileInfo(marker).Length > 128 ||
            HasGlobalProjectManifest(root))
            return false;

        ValidateCachedRoots(root);
        return string.Equals(File.ReadAllText(marker), hash, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> FindPrimaryHierarchies(string root)
        => EnumerateSafeFiles(Path.Combine(root, "Assets"))
            .Concat(Directory.Exists(Path.Combine(root, "Packages"))
                ? EnumerateSafeFiles(Path.Combine(root, "Packages"))
                : Enumerable.Empty<string>())
            .Where(static path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                                  path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

    private static DependencySource InspectDependency(string path, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            if (fullPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                string root = Prepare(fullPath, cacheRoot);
                return new DependencySource(fullPath, DependencyKind.UnityPackage, Path.GetFileName(root), root, null, false);
            }

            if (fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string name = ReadZipPackageName(fullPath, out _);
                return new DependencySource(fullPath, DependencyKind.Zip, ComputeFileHash(fullPath), null, name, false);
            }

            throw new InvalidDataException($"Unsupported companion source '{fullPath}'.");
        }

        if (!Directory.Exists(fullPath))
            throw new FileNotFoundException("Companion source was not found.", fullPath);

        RejectReparsePoints(fullPath);
        if (File.Exists(Path.Combine(fullPath, "package.json")))
        {
            string name = ReadPackageName(Path.Combine(fullPath, "package.json"));
            return new DependencySource(fullPath, DependencyKind.Folder, ComputeFolderHash(fullPath, false), null, name, false);
        }

        if (Path.GetFileName(fullPath).Equals("Assets", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(Path.Combine(fullPath, "Assets")))
            throw new InvalidDataException($"Companion folder '{fullPath}' has no package.json or Assets folder.");

        return new DependencySource(fullPath, DependencyKind.Folder, ComputeFolderHash(fullPath, true), null, null, true);
    }

    private static string ComputeCombinedHash(string primaryHash, List<DependencySource> dependencies)
    {
        var descriptor = new StringBuilder(CombinedFormat).Append('\n').Append(primaryHash).Append('\n');
        foreach (DependencySource dependency in dependencies)
            descriptor.Append((int)dependency.Kind).Append(':').Append(dependency.Hash).Append(':')
                .Append(dependency.Name).Append(':').Append(dependency.IsProject).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString()))).ToLowerInvariant();
    }

    private static void AddDependency(
        DependencySource dependency,
        string staging,
        Dictionary<string, string> origins,
        Dictionary<string, string> canonicalPaths,
        int ordinal)
    {
        string sourceLabel = $"{dependency.Path} (companion {ordinal})";
        switch (dependency.Kind)
        {
            case DependencyKind.UnityPackage:
                CopyProjectRoots(dependency.Root!, staging, sourceLabel, origins, canonicalPaths);
                return;
            case DependencyKind.Folder when dependency.IsProject:
                CopyProjectRoots(dependency.Path, staging, sourceLabel, origins, canonicalPaths, skipPackageManifest: true);
                return;
            case DependencyKind.Folder:
                CopyDirectory(dependency.Path, Path.Combine(staging, "Packages", dependency.Name!), sourceLabel, origins, canonicalPaths);
                return;
            case DependencyKind.Zip:
                AddZipDependency(dependency, staging, sourceLabel, origins, canonicalPaths);
                return;
        }
    }

    private static void AddZipDependency(
        DependencySource dependency,
        string staging,
        string sourceLabel,
        Dictionary<string, string> origins,
        Dictionary<string, string> canonicalPaths)
    {
        string temporary = Path.Combine(staging, $".dependency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            using var source = new FileStream(dependency.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);
            string name = ReadZipPackageName(zip, out string prefix);
            if (!string.Equals(name, dependency.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"Companion archive '{dependency.Path}' changed its package name.");

            long writtenBytes = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string relative = ValidateZipEntry(entry);
                if (relative.Equals(prefix.TrimEnd('/'), StringComparison.Ordinal) && IsZipDirectory(entry))
                    continue;
                if (!relative.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException($"Companion archive entry '{entry.FullName}' is outside its package root.");
                relative = relative[prefix.Length..];
                if (relative.Length == 0 || IsZipDirectory(entry))
                    continue;

                string outputPath = GetDestination(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = entry.Open();
                using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = CopyLimited(input, output, Math.Min(MaxArchiveEntryBytes, MaxExpandedBytes - writtenBytes));
                if (written != entry.Length)
                    throw new InvalidDataException($"Companion archive entry '{entry.FullName}' is incomplete.");
                writtenBytes += written;
            }

            CopyDirectory(temporary, Path.Combine(staging, "Packages", name), sourceLabel, origins, canonicalPaths);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string ReadZipPackageName(string path, out string prefix)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        return ReadZipPackageName(zip, out prefix);
    }

    private static string ReadZipPackageName(ZipArchive zip, out string prefix)
    {
        ZipArchiveEntry? manifest = null;
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        long expandedBytes = 0;
        prefix = string.Empty;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            CheckArchiveEntry(entry.FullName, entry.Length, ref entryCount, ref expandedBytes);
            string relative = ValidateZipEntry(entry);
            RegisterPathCase(relative, canonicalPaths);
            if (!relative.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                !relative.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string candidatePrefix = relative[..^"package.json".Length];
            if (candidatePrefix.Count(static c => c == '/') > 1)
                continue;

            if (manifest is not null)
                throw new InvalidDataException("The companion archive has multiple package.json files.");
            manifest = entry;
            prefix = candidatePrefix;
        }

        if (manifest is null || manifest.Length > 1024 * 1024)
            throw new InvalidDataException("The companion archive needs one small package.json at its root or one wrapper folder.");
        using var stream = manifest.Open();
        using var buffer = new MemoryStream((int)manifest.Length);
        if (CopyLimited(stream, buffer, 1024 * 1024) != manifest.Length)
            throw new InvalidDataException("The companion archive package.json is incomplete.");
        buffer.Position = 0;
        return ReadPackageName(buffer);
    }

    private static string ValidateZipEntry(ZipArchiveEntry entry)
    {
        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
            throw new InvalidDataException($"Companion archive entry '{entry.FullName}' is a link or unsupported file type.");
        if (((entry.ExternalAttributes >> 16) & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Companion archive entry '{entry.FullName}' is a reparse point.");

        string relative = entry.FullName.Replace('\\', '/');
        ValidateSegments(relative, IsZipDirectory(entry));
        return relative.TrimEnd('/');
    }

    private static bool IsZipDirectory(ZipArchiveEntry entry)
        => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') ||
           ((entry.ExternalAttributes >> 16) & 0xF000) == 0x4000;

    private static string ReadPackageName(string manifestPath)
    {
        RejectReparsePoints(manifestPath);
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException($"Companion manifest '{manifestPath}' is too large.");
        return ReadPackageName(stream);
    }

    private static string ReadPackageName(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Companion package.json has no package name.");
        string? name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Split('.').Any(static part => part.Length == 0 ||
                part.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_')))
            throw new InvalidDataException($"Companion package name '{name}' is invalid.");
        ValidateSegments(name, allowTrailingSlash: false);
        return name;
    }

    private static void CopyProjectRoots(
        string source,
        string target,
        string sourceLabel,
        Dictionary<string, string> origins,
        Dictionary<string, string> canonicalPaths,
        bool skipPackageManifest = false)
    {
        CopyDirectory(Path.Combine(source, "Assets"), Path.Combine(target, "Assets"), sourceLabel, origins, canonicalPaths);
        string packages = Path.Combine(source, "Packages");
        if (Directory.Exists(packages))
            CopyDirectory(packages, Path.Combine(target, "Packages"), sourceLabel, origins, canonicalPaths, skipPackageManifest);

        foreach (string rootName in new[] { "Assets", "Packages" })
        {
            string relative = rootName + ".meta";
            string metadata = Path.Combine(source, relative);
            if (!File.Exists(metadata))
                continue;
            RejectReparsePoints(metadata);
            CheckDuplicateAsset(source, target, relative, sourceLabel, origins);
            string output = Path.Combine(target, relative);
            RegisterPathCase(output, canonicalPaths);
            if (!File.Exists(output))
            {
                File.Copy(metadata, output);
                origins[output] = sourceLabel;
            }
        }
    }

    private static void CopyDirectory(
        string source,
        string target,
        string sourceLabel,
        Dictionary<string, string> origins,
        Dictionary<string, string> canonicalPaths,
        bool skipPackageManifest = false)
    {
        RejectReparsePoints(source);
        if (!Directory.Exists(source))
            return;
        if (File.Exists(target))
            throw new InvalidDataException($"Companion folder '{source}' conflicts with a file from '{origins.GetValueOrDefault(target, "an earlier source")}'.");
        RegisterPathCase(target, canonicalPaths);
        Directory.CreateDirectory(target);
        foreach (string path in EnumerateSafeEntries(source))
        {
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (skipPackageManifest && IsPackageManifestFile(relative))
                continue;

            string output = GetDestination(target, relative);
            RegisterPathCase(output, canonicalPaths);
            if (Directory.Exists(path))
            {
                if (File.Exists(output))
                    throw new InvalidDataException($"Companion folder '{relative}' from '{sourceLabel}' conflicts with a file from '{origins.GetValueOrDefault(output, "an earlier source")}'.");
                Directory.CreateDirectory(output);
            }
            else
            {
                CheckDuplicateAsset(source, target, relative, sourceLabel, origins);
                if (!File.Exists(output))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(outputStream);
                    origins[output] = sourceLabel;
                }
            }
        }
    }

    private static void CheckDuplicateAsset(
        string source,
        string target,
        string relative,
        string sourceLabel,
        Dictionary<string, string> origins)
    {
        string assetRelative = relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? relative[..^5]
            : relative;
        string sourceAsset = GetDestination(source, assetRelative);
        string targetAsset = GetDestination(target, assetRelative);
        string sourceMeta = sourceAsset + ".meta";
        string targetMeta = targetAsset + ".meta";
        bool targetPresent = File.Exists(targetAsset) || File.Exists(targetMeta);
        string occupied = File.Exists(targetAsset) ? targetAsset : targetMeta;
        if (targetPresent && origins.TryGetValue(occupied, out string? owner) &&
            string.Equals(owner, sourceLabel, StringComparison.Ordinal))
            return;
        if (!targetPresent)
        {
            if (Directory.Exists(targetAsset) && File.Exists(sourceAsset))
                throw new InvalidDataException($"Companion asset '{relative}' from '{sourceLabel}' conflicts with an existing folder.");
            return;
        }

        if (File.Exists(sourceAsset) != File.Exists(targetAsset) ||
            File.Exists(sourceMeta) != File.Exists(targetMeta) ||
            (File.Exists(sourceAsset) && !File.Exists(sourceMeta)) ||
            (File.Exists(sourceAsset) && !FilesEqual(sourceAsset, targetAsset)) ||
            (File.Exists(sourceMeta) && !FilesEqual(sourceMeta, targetMeta)))
        {
            string earlier = origins.GetValueOrDefault(File.Exists(targetAsset) ? targetAsset : targetMeta, "an earlier source");
            throw new InvalidDataException($"Companion asset '{assetRelative}' from '{sourceLabel}' conflicts with '{earlier}'.");
        }
    }

    private static void RegisterPathCase(string path, Dictionary<string, string> canonicalPaths)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        string[] parts = normalized.Split('/');
        var current = new StringBuilder();
        foreach (string part in parts)
        {
            if (current.Length > 0)
                current.Append('/');
            current.Append(part);
            string key = current.ToString();
            if (canonicalPaths.TryGetValue(key, out string? existing) &&
                !string.Equals(existing, key, StringComparison.Ordinal))
                throw new InvalidDataException($"The paths '{existing}' and '{key}' differ only by case.");
            canonicalPaths.TryAdd(key, key);
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        return firstInfo.Length == secondInfo.Length &&
            string.Equals(ComputeFileHash(first), ComputeFileHash(second), StringComparison.Ordinal);
    }

    private static void ValidateGuids(string root, Dictionary<string, string> origins)
    {
        var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectRoot in new[] { "Assets", "Packages" })
        {
            string directory = Path.Combine(root, projectRoot);
            string rootMeta = Path.Combine(root, projectRoot + ".meta");
            IEnumerable<string> files = Directory.Exists(directory)
                ? EnumerateSafeFiles(directory)
                : Enumerable.Empty<string>();
            if (File.Exists(rootMeta))
            {
                RejectReparsePoints(rootMeta);
                files = files.Append(rootMeta);
            }
            foreach (string file in files)
            {
                if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? guid = null;
                foreach (string line in File.ReadLines(file))
                {
                    if (line.StartsWith("guid:", StringComparison.Ordinal))
                    {
                        guid = line[5..].Trim();
                        break;
                    }
                }
                if (guid is null)
                    continue;
                if (guid.Length != 32 || !guid.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Unity metadata '{file}' has an invalid GUID.");
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (guids.TryGetValue(guid, out string? prior) && !prior.Equals(relative, StringComparison.OrdinalIgnoreCase))
                {
                    string priorSource = origins.GetValueOrDefault(Path.Combine(root, prior.Replace('/', Path.DirectorySeparatorChar)), "an earlier source");
                    string currentSource = origins.GetValueOrDefault(file, "a companion source");
                    throw new InvalidDataException($"Unity GUID {guid} is used by '{prior}' from '{priorSource}' and '{relative}' from '{currentSource}'.");
                }
                guids[guid] = relative;
            }
        }
    }

    private static string ComputeFolderHash(string root, bool isProject)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IEnumerable<string> roots = isProject
            ? new[] { Path.Combine(root, "Assets"), Path.Combine(root, "Packages") }.Where(Directory.Exists)
            : new[] { root };
        foreach (string folder in roots)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, folder).Replace('\\', '/')));
            hash.AppendData([0]);
            foreach (string path in EnumerateSafeEntries(folder))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (isProject && relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) &&
                    IsPackageManifestFile(relative["Packages/".Length..]))
                    continue;
                hash.AppendData([Directory.Exists(path) ? (byte)'D' : (byte)'F']);
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                if (File.Exists(path))
                    hash.AppendData(Convert.FromHexString(ComputeFileHash(path)));
            }
        }
        if (isProject)
        {
            foreach (string rootName in new[] { "Assets", "Packages" })
            {
                string metadata = Path.Combine(root, rootName + ".meta");
                if (!File.Exists(metadata))
                    continue;
                hash.AppendData(Encoding.UTF8.GetBytes(rootName + ".meta"));
                hash.AppendData([0]);
                hash.AppendData(Convert.FromHexString(ComputeFileHash(metadata)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeFileHash(string path)
    {
        RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsPackageManifestFile(string relative)
        => relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("manifest.json.meta", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("packages-lock.json", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("packages-lock.json.meta", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSafeFiles(string root)
        => EnumerateSafeEntries(root).Where(File.Exists);

    private static IEnumerable<string> EnumerateSafeEntries(string root)
    {
        RejectReparsePoints(root);
        if (!Directory.Exists(root))
            yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            string[] entries = Directory.GetFileSystemEntries(directory);
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (string path in entries)
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                ValidateSegments(relative, allowTrailingSlash: false);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Companion source '{relative}' is a link or reparse point.");
                yield return path;
                if (Directory.Exists(path))
                    stack.Push(path);
            }
        }
    }

    private enum DependencyKind { UnityPackage, Zip, Folder }

    private sealed record DependencySource(
        string Path,
        DependencyKind Kind,
        string Hash,
        string? Root,
        string? Name,
        bool IsProject);
}
