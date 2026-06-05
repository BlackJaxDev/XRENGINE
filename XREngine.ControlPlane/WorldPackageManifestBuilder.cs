using System.Security.Cryptography;
using System.Text;
using XREngine.Networking;

namespace XREngine.ControlPlane;

public static class WorldPackageManifestBuilder
{
    public static WorldPackageManifest CreateFromDirectory(
        string rootDirectory,
        WorldAssetIdentity asset,
        string? packageId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
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

        string manifestHash = ComputeManifestHash(files);
        WorldAssetIdentity manifestAsset = InMemoryControlPlane.CloneWorldAsset(asset);
        if (string.IsNullOrWhiteSpace(manifestAsset.ContentHash))
            manifestAsset.ContentHash = $"sha256:{manifestHash}";

        return new WorldPackageManifest
        {
            PackageId = string.IsNullOrWhiteSpace(packageId) ? manifestAsset.WorldId : packageId!,
            Asset = manifestAsset,
            RootPath = root,
            TotalBytes = files.Sum(static file => file.Length),
            ManifestHash = $"sha256:{manifestHash}",
            Files = files,
            Metadata = metadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static WorldPackageVerificationResult Verify(WorldPackageManifest manifest, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory) ? manifest.RootPath : rootDirectory);
        WorldPackageVerificationResult result = new();

        foreach (WorldPackageFile file in manifest.Files)
        {
            string path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                result.MissingFiles.Add(file.RelativePath);
                continue;
            }

            FileInfo info = new(path);
            if (info.Length != file.Length)
                result.LengthMismatches.Add(file.RelativePath);

            string actualHash = ComputeFileSha256(path);
            if (!string.Equals(actualHash, WorldAssetIdentity.NormalizeHash(file.Sha256), StringComparison.OrdinalIgnoreCase))
                result.HashMismatches.Add(file.RelativePath);
        }

        return result;
    }

    public static void Mirror(WorldPackageManifest manifest, string targetDirectory, string? sourceRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        string sourceRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(sourceRootDirectory) ? manifest.RootPath : sourceRootDirectory);
        string targetRoot = Path.GetFullPath(targetDirectory);

        foreach (WorldPackageFile file in manifest.Files)
        {
            string source = Path.Combine(sourceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(targetRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.Copy(source, target, overwrite: true);
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string ComputeManifestHash(IEnumerable<WorldPackageFile> files)
    {
        StringBuilder builder = new();
        foreach (WorldPackageFile file in files)
        {
            builder.Append(file.RelativePath)
                .Append('|')
                .Append(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('|')
                .Append(WorldAssetIdentity.NormalizeHash(file.Sha256))
                .Append('\n');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
