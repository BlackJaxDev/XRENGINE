using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace XREngine.UnityScriptHost.Admission.PackageSources;

/// <summary>Copies a verified source project and pinned local package sources into a new project directory.</summary>
public sealed class UnitySourcePackageResolver
{
    private const int MaxPinnedPackages = 1024;

    /// <summary>Returns the composed inventory after every copied file matches its inspected hash.</summary>
    public UnitySourceInventory Compose(string projectRoot, UnitySourceCompositionRequest request,
        string requestDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (request.SchemaVersion != UnitySourceCompositionRequest.CurrentSchemaVersion ||
            !IsHash(request.SourceContentSha256) || request.EntryPath is null ||
            request.RootName is null || request.Packages is null || request.Packages.Length > MaxPinnedPackages)
            throw new InvalidDataException("The source composition request is invalid or exceeds its package limit.");

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string requestRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestDirectory));
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(requestRoot);
        UnitySourceInventory sourceInventory = new UnitySourceInventoryBuilder().Inspect(source,
            request.EntryPath, request.RootName, cancellationToken);
        if (!sourceInventory.SourceContentSha256.Equals(request.SourceContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source project does not match the pinned inventory digest.");

        string outputParent = Path.GetDirectoryName(output) ??
            throw new InvalidDataException("The output parent directory is missing.");
        string outputParentIdentity = UnitySourcePath.GetDirectoryIdentity(outputParent);
        string sourceIdentity = UnitySourcePath.GetDirectoryIdentity(source);
        if (IsInside(outputParentIdentity, sourceIdentity))
            throw new InvalidDataException("Composition output must be outside the source project.");
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Composition output already exists. Choose a new directory.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnitySourceEntry entry in sourceInventory.Entries)
            if (entry.Kind == "Directory" && entry.Path.StartsWith("Packages/", StringComparison.Ordinal) &&
                entry.Path.Count(static character => character == '/') == 1)
                names.Add(entry.Path["Packages/".Length..]);
        foreach (UnitySourcePackage manifest in sourceInventory.Packages)
            if (manifest.IsEmbeddedPackage)
                names.Add(manifest.Name);

        var pinned = new List<(string Source, UnityPackageSourceInventory Inventory)>();
        long totalBytes = sourceInventory.TotalFileBytes;
        int entryCount = sourceInventory.Entries.Length;
        foreach (UnityPinnedPackageSource package in request.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package is null || !UnityPackageSourceInspector.IsValidPackageName(package.Name) ||
                string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 2048 ||
                string.IsNullOrWhiteSpace(package.Path) || package.Path.Length > 2048 ||
                Path.IsPathRooted(package.Path) || package.Path.Contains(':') || !IsHash(package.ContentSha256))
                throw new InvalidDataException("A pinned package needs a bounded name, version, relative path, and SHA-256 digest.");
            if (!names.Add(package.Name))
                throw new InvalidDataException($"A package name or destination directory is already present: '{package.Name}'.");
            string packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Path, requestRoot));
            if (IsInside(UnitySourcePath.GetDirectoryIdentity(packageRoot), sourceIdentity))
                throw new InvalidDataException("A pinned package source must be outside the source project.");
            if (IsInside(outputParentIdentity, UnitySourcePath.GetDirectoryIdentity(packageRoot)))
                throw new InvalidDataException("Composition output must be outside each package source directory.");
            UnityPackageSourceInventory inventory = new UnityPackageSourceInspector().Inspect(packageRoot, cancellationToken);
            if (inventory.Name != package.Name || inventory.Version != package.Version ||
                !inventory.ContentSha256.Equals(package.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Pinned package '{package.Name}' does not match its manifest and content digest.");
            if (inventory.TotalFileBytes > 32L * 1024 * 1024 * 1024 - totalBytes ||
                inventory.Entries.Length + 1 > 100_000 - entryCount)
                throw new InvalidDataException("The composed project exceeds the source byte or entry limit.");
            totalBytes += inventory.TotalFileBytes;
            entryCount += inventory.Entries.Length + 1;
            foreach (UnitySourceEntry entry in inventory.Entries)
                if ($"Packages/{inventory.Name}/{entry.Path}".Length > 2048)
                    throw new InvalidDataException("A composed package path exceeds the source path limit.");
            pinned.Add((packageRoot, inventory));
        }

        // Output is created only after all source identities and package pins pass preflight.
        Directory.CreateDirectory(output);
        CopyEntries(source, output, sourceInventory.Entries, string.Empty, cancellationToken);
        foreach ((string packageRoot, UnityPackageSourceInventory inventory) in pinned)
        {
            string destination = "Packages/" + inventory.Name;
            Directory.CreateDirectory(Path.Combine(output, destination.Replace('/', Path.DirectorySeparatorChar)));
            CopyEntries(packageRoot, output, inventory.Entries, destination + "/", cancellationToken);
        }

        // Detect changes after the copy, including edits that occur during composition.
        string currentSourceDigest = new UnitySourceInventoryBuilder().Inspect(source,
            request.EntryPath, request.RootName, cancellationToken).SourceContentSha256;
        if (!currentSourceDigest.Equals(sourceInventory.SourceContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source project changed during composition.");
        foreach ((string packageRoot, UnityPackageSourceInventory inventory) in pinned)
            if (new UnityPackageSourceInspector().Inspect(packageRoot, cancellationToken).ContentSha256 != inventory.ContentSha256)
                throw new InvalidDataException($"Pinned package '{inventory.Name}' changed during composition.");
        UnitySourceInventory composed = new UnitySourceInventoryBuilder().Inspect(output,
            request.EntryPath, request.RootName, cancellationToken);
        ValidateComposedEntries(sourceInventory, pinned, composed);
        return composed;
    }

    private static void ValidateComposedEntries(UnitySourceInventory sourceInventory,
        List<(string Source, UnityPackageSourceInventory Inventory)> pinned, UnitySourceInventory composed)
    {
        var expected = new Dictionary<string, UnitySourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (UnitySourceEntry entry in sourceInventory.Entries)
            if (!expected.TryAdd(entry.Path, entry))
                throw new InvalidDataException("Source inventory paths collide under Windows comparison.");
        if (pinned.Count != 0 && !expected.ContainsKey("Packages"))
            expected.Add("Packages", new("Packages", "Directory", 0, string.Empty));
        foreach ((string _, UnityPackageSourceInventory inventory) in pinned)
        {
            string root = "Packages/" + inventory.Name;
            if (!expected.TryAdd(root, new(root, "Directory", 0, string.Empty)))
                throw new InvalidDataException("A pinned package destination is duplicated.");
            foreach (UnitySourceEntry entry in inventory.Entries)
            {
                string path = root + "/" + entry.Path;
                if (!expected.TryAdd(path, new(path, entry.Kind, entry.Length, entry.Sha256)))
                    throw new InvalidDataException("A pinned package source path is duplicated.");
            }
        }
        if (composed.Entries.Length != expected.Count)
            throw new InvalidDataException("Composed project entries differ from the pinned source union.");
        foreach (UnitySourceEntry actual in composed.Entries)
        {
            if (!expected.TryGetValue(actual.Path, out UnitySourceEntry? selected) ||
                actual.Length != selected.Length ||
                !actual.Sha256.Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase) ||
                (selected.Kind == "Directory") != (actual.Kind == "Directory"))
                throw new InvalidDataException($"Composed project content changed: '{actual.Path}'.");
        }
    }

    private static void CopyEntries(string sourceRoot, string output, UnitySourceEntry[] entries,
        string prefix, CancellationToken cancellationToken)
    {
        foreach (UnitySourceEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = prefix + entry.Path;
            ValidateRelativePath(relative);
            string destination = Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Kind == "Directory")
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            string source = Path.Combine(sourceRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            UnitySourceInventoryBuilder.EnsureNoReparsePoints(source);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            if (input.Length != entry.Length)
                throw new InvalidDataException($"Source length changed during composition: '{relative}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var copied = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            string digest = CopyAndHash(input, copied, entry.Length, cancellationToken);
            if (!digest.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source bytes changed during composition: '{relative}'.");
        }
    }

    private static string CopyAndHash(Stream input, FileStream output, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > length)
                    throw new IOException("Source length changed during composition.");
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            if (total != length || input.ReadByte() != -1)
                throw new IOException("Source length changed during composition.");
            output.Flush(flushToDisk: true);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (path.Length is < 1 or > 4096 || Path.IsPathRooted(path) || path.Contains(':') ||
            path.Contains('\\') || path.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException("An inventoried source path is not a safe relative path.");
    }

    private static bool IsHash(string value)
        => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);

    private static bool IsInside(string candidate, string root)
        => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
