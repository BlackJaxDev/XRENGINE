using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Networking;

namespace XREngine.ControlPlane;

public static class WorldPackageManifestBuilder
{
    /// <summary>Validates the immutable manifest identity and safe file declarations before any download is attempted.</summary>
    public static WorldPackageVerificationResult VerifyManifest(WorldPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        WorldPackageVerificationResult result = new();
        ValidateManifestIdentity(manifest, result, requireAssetContentHashMatch: true);
        return result;
    }

    public static WorldPackageManifest CreateFromDirectory(
        string rootDirectory,
        WorldAssetIdentity asset,
        string? packageId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? worldEntryPoint = null,
        string? gameBootstrapId = null,
        string? buildVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(asset);

        string root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        List<WorldPackageFile> files = [];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            FileInfo info = new(file);
            files.Add(new WorldPackageFile
            {
                RelativePath = relative,
                Length = info.Length,
                Sha256 = ComputeFileSha256(file),
            });
        }

        WorldAssetIdentity manifestAsset = InMemoryControlPlane.CloneWorldAsset(asset);
        string effectivePackageId = string.IsNullOrWhiteSpace(packageId) ? asset.WorldId : packageId!;
        string effectiveBuildVersion = string.IsNullOrWhiteSpace(buildVersion) ? asset.RequiredBuildVersion : buildVersion!;
        string effectiveWorldEntryPoint = string.IsNullOrWhiteSpace(worldEntryPoint)
            ? files.FirstOrDefault(file => string.Equals(file.RelativePath, "World.asset", StringComparison.OrdinalIgnoreCase))?.RelativePath ?? string.Empty
            : worldEntryPoint!;
        string effectiveGameBootstrapId = string.IsNullOrWhiteSpace(gameBootstrapId) ? "world-v1" : gameBootstrapId!;
        IReadOnlyDictionary<string, string> effectiveMetadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = files.Sum(static file => file.Length);
        string manifestHash = ComputeManifestHash(files, effectivePackageId, manifestAsset, effectiveWorldEntryPoint, effectiveGameBootstrapId, effectiveBuildVersion, effectiveMetadata, totalBytes, schemaVersion: 1);
        // Managed admission binds to the immutable package revision, never an advisory asset-only hash.
        manifestAsset.ContentHash = $"sha256:{manifestHash}";

        return new WorldPackageManifest
        {
            PackageId = effectivePackageId,
            Asset = manifestAsset,
            WorldEntryPoint = effectiveWorldEntryPoint,
            GameBootstrapId = effectiveGameBootstrapId,
            BuildVersion = effectiveBuildVersion,
            RootPath = root,
            TotalBytes = totalBytes,
            ManifestHash = $"sha256:{manifestHash}",
            Files = files,
            Metadata = new Dictionary<string, string>(effectiveMetadata, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static WorldPackageVerificationResult Verify(
        WorldPackageManifest manifest,
        string? rootDirectory = null,
        CancellationToken cancellationToken = default,
        bool requireAssetContentHashMatch = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? manifest.RootPath : rootDirectory);
        WorldPackageVerificationResult result = new();

        if (!ValidateManifestIdentity(manifest, result, requireAssetContentHashMatch))
            return result;

        foreach (WorldPackageFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetContainedFilePath(root, file.RelativePath, out string path) || IsReparsePoint(path))
            {
                result.UnsafePaths.Add(file.RelativePath);
                continue;
            }
            if (!File.Exists(path))
            {
                result.MissingFiles.Add(file.RelativePath);
                continue;
            }

            FileInfo info = new(path);
            if (info.Length != file.Length)
                result.LengthMismatches.Add(file.RelativePath);

            string actualHash = ComputeFileSha256(path, cancellationToken);
            if (!string.Equals(actualHash, WorldAssetIdentity.NormalizeHash(file.Sha256), StringComparison.OrdinalIgnoreCase))
                result.HashMismatches.Add(file.RelativePath);
        }

        VerifyNoUnexpectedFiles(manifest, root, result);
        return result;
    }

    public static void Mirror(
        WorldPackageManifest manifest,
        string targetDirectory,
        string? sourceRootDirectory = null,
        CancellationToken cancellationToken = default,
        IProgress<WorldPackageStagingProgress>? progress = null,
        bool requireAssetContentHashMatch = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        string sourceRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(sourceRootDirectory) ? manifest.RootPath : sourceRootDirectory);
        string targetRoot = Path.GetFullPath(targetDirectory);

        WorldPackageVerificationResult sourceVerification = Verify(manifest, sourceRoot, cancellationToken, requireAssetContentHashMatch);
        if (!sourceVerification.Success)
            throw new InvalidOperationException($"World package source failed verification: {DescribeVerificationFailure(sourceVerification)}");

        long bytesTransferred = 0;
        foreach (WorldPackageFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetContainedFilePath(sourceRoot, file.RelativePath, out string source)
                || !TryGetContainedFilePath(targetRoot, file.RelativePath, out string target))
                throw new InvalidOperationException($"Package path '{file.RelativePath}' escapes its root.");
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            CopyFile(source, target, file.RelativePath, ref bytesTransferred, manifest.TotalBytes, cancellationToken, progress);
        }
    }

    /// <summary>Copies, verifies, then publishes a package directory without exposing partially staged content.</summary>
    public static string StageVerified(
        WorldPackageManifest manifest,
        string targetDirectory,
        string? sourceRootDirectory = null,
        CancellationToken cancellationToken = default,
        IProgress<WorldPackageStagingProgress>? progress = null,
        bool requireAssetContentHashMatch = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        string targetRoot = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(targetRoot))
        {
            WorldPackageVerificationResult cached = Verify(manifest, targetRoot, cancellationToken, requireAssetContentHashMatch);
            if (cached.Success)
                return targetRoot;
            throw new IOException($"Target staging directory exists but does not match the requested package: {targetRoot}");
        }
        if (File.Exists(targetRoot))
            throw new IOException($"Target staging path is a file: {targetRoot}");

        string? parent = Path.GetDirectoryName(targetRoot);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("Target staging directory must have a parent.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(targetRoot)}.{Guid.NewGuid():N}.staging");
        try
        {
            Mirror(manifest, temporary, sourceRootDirectory, cancellationToken, progress, requireAssetContentHashMatch);
            WorldPackageVerificationResult verification = Verify(manifest, temporary, cancellationToken, requireAssetContentHashMatch);
            if (!verification.Success)
                throw new InvalidOperationException($"Staged world package failed verification: {DescribeVerificationFailure(verification)}");
            Directory.Move(temporary, targetRoot);
            return targetRoot;
        }
        catch
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private static string ComputeFileSha256(string path, CancellationToken cancellationToken = default)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    private static string ComputeManifestHash(IEnumerable<WorldPackageFile> files, string packageId, WorldAssetIdentity asset, string worldEntryPoint, string gameBootstrapId, string buildVersion, IReadOnlyDictionary<string, string>? metadata, long totalBytes, int schemaVersion)
    {
        StringBuilder builder = new();
        AppendCanonical(builder, schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(builder, packageId);
        AppendCanonical(builder, asset.WorldId);
        AppendCanonical(builder, asset.RevisionId);
        AppendCanonical(builder, asset.AssetSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(builder, asset.RequiredBuildVersion);
        AppendCanonical(builder, worldEntryPoint);
        AppendCanonical(builder, gameBootstrapId);
        AppendCanonical(builder, buildVersion);
        AppendCanonical(builder, totalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (metadata is not null)
        {
            foreach ((string key, string value) in metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                AppendCanonical(builder, key);
                AppendCanonical(builder, value);
            }
        }
        foreach (WorldPackageFile file in files)
        {
            AppendCanonical(builder, file.RelativePath);
            AppendCanonical(builder, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, WorldAssetIdentity.NormalizeHash(file.Sha256));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool ValidateManifestIdentity(WorldPackageManifest manifest, WorldPackageVerificationResult result, bool requireAssetContentHashMatch)
    {
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.PackageId) || manifest.Asset is null || string.IsNullOrWhiteSpace(manifest.Asset.WorldId)
            || string.IsNullOrWhiteSpace(manifest.Asset.RevisionId) || string.IsNullOrWhiteSpace(manifest.WorldEntryPoint)
            || string.IsNullOrWhiteSpace(manifest.GameBootstrapId) || string.IsNullOrWhiteSpace(manifest.BuildVersion)
            || !string.Equals(manifest.Asset.RequiredBuildVersion, manifest.BuildVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.ManifestHash))
        {
            result.InvalidManifest = true;
            return false;
        }

        long totalBytes = 0;
        try
        {
            foreach (WorldPackageFile file in manifest.Files)
            {
                if (file.Length < 0 || string.IsNullOrWhiteSpace(file.Sha256) || !TryGetContainedFilePath(Path.GetTempPath(), file.RelativePath, out _))
                {
                    result.InvalidManifest = true;
                    return false;
                }
                totalBytes = checked(totalBytes + file.Length);
            }
        }
        catch (OverflowException)
        {
            result.InvalidManifest = true;
            return false;
        }

        if (totalBytes != manifest.TotalBytes || manifest.Files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
        {
            result.InvalidManifest = true;
            return false;
        }
        if (!manifest.Files.Any(file => string.Equals(file.RelativePath, manifest.WorldEntryPoint.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            result.InvalidManifest = true;
            return false;
        }

        string expected = $"sha256:{ComputeManifestHash(manifest.Files, manifest.PackageId, manifest.Asset, manifest.WorldEntryPoint, manifest.GameBootstrapId, manifest.BuildVersion, manifest.Metadata, manifest.TotalBytes, manifest.SchemaVersion)}";
        if (!string.Equals(expected, manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            result.ManifestHashMismatch = true;
            return false;
        }
        if (requireAssetContentHashMatch && !string.Equals(WorldAssetIdentity.NormalizeHash(manifest.Asset.ContentHash), WorldAssetIdentity.NormalizeHash(manifest.ManifestHash), StringComparison.OrdinalIgnoreCase))
        {
            result.AssetContentHashMismatch = true;
            return false;
        }
        return true;
    }

    private static void AppendCanonical(StringBuilder builder, string? value)
    {
        string text = value ?? string.Empty;
        builder.Append(Encoding.UTF8.GetByteCount(text).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(text).Append('\n');
    }

    private static void VerifyNoUnexpectedFiles(WorldPackageManifest manifest, string root, WorldPackageVerificationResult result)
    {
        HashSet<string> expected = new(manifest.Files.Select(file => file.RelativePath.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (string.Equals(relative, "world-package.json", StringComparison.OrdinalIgnoreCase))
            {
                if (IsMatchingPackageDescriptor(path, manifest))
                    continue;
                result.ExtraFiles.Add(relative);
                continue;
            }
            if (!expected.Contains(relative) || IsReparsePoint(path))
                result.ExtraFiles.Add(relative);
        }
    }

    private static bool IsMatchingPackageDescriptor(string path, WorldPackageManifest manifest)
    {
        try
        {
            WorldPackageManifest? descriptor = JsonSerializer.Deserialize(File.ReadAllText(path), XreControlPlaneJsonContext.Default.WorldPackageManifest);
            return descriptor is not null
                && string.Equals(descriptor.ManifestHash, manifest.ManifestHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(descriptor.PackageId, manifest.PackageId, StringComparison.Ordinal)
                && descriptor.SchemaVersion == manifest.SchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string DescribeVerificationFailure(WorldPackageVerificationResult result)
    {
        List<string> details = [];
        if (result.InvalidManifest)
            details.Add("invalid manifest");
        if (result.ManifestHashMismatch)
            details.Add("manifest hash mismatch");
        if (result.AssetContentHashMismatch)
            details.Add("asset content hash mismatch");
        details.AddRange(result.MissingFiles.Select(file => $"missing:{file}"));
        details.AddRange(result.LengthMismatches.Select(file => $"length:{file}"));
        details.AddRange(result.HashMismatches.Select(file => $"hash:{file}"));
        details.AddRange(result.UnsafePaths.Select(file => $"unsafe:{file}"));
        details.AddRange(result.ExtraFiles.Select(file => $"extra:{file}"));
        return details.Count == 0 ? "unknown verification failure" : string.Join(", ", details);
    }

    private static void CopyFile(string source, string target, string relativePath, ref long bytesTransferred, long totalBytes, CancellationToken cancellationToken, IProgress<WorldPackageStagingProgress>? progress)
    {
        using FileStream input = File.OpenRead(source);
        using FileStream output = File.Create(target);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            bytesTransferred += read;
            progress?.Report(new WorldPackageStagingProgress { RelativePath = relativePath, BytesTransferred = bytesTransferred, TotalBytes = totalBytes });
        }
    }

    private static bool TryGetContainedFilePath(string root, string relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        path = candidate;
        return true;
    }

    private static bool IsReparsePoint(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return true;
        DirectoryInfo? current = new FileInfo(path).Directory;
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            current = current.Parent;
        }
        return false;
    }
}
