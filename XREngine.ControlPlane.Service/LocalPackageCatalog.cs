using System.Text.Json;

namespace XREngine.ControlPlane.Service;

/// <summary>Resolves only packages registered by the operator, never a client-supplied local path.</summary>
internal sealed class LocalPackageCatalog(LocalServiceOptions options)
{
    /// <summary>
    /// Loads the manifest for the specified package from the local catalog.
    /// </summary>
    /// <param name="packageId">The ID of the package to load.</param>
    /// <returns>The manifest of the specified package.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if the package is not in the configured catalog.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the package manifest is unavailable, invalid, or does not match the catalog key.</exception>
    public WorldPackageManifest Load(string packageId)
    {
        if (!options.Packages.TryGetValue(packageId, out string? path))
            throw new KeyNotFoundException("The package is not in the configured catalog.");
        
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 4 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The configured package manifest is unavailable or invalid.");
        
        WorldPackageManifest package = JsonSerializer.Deserialize<WorldPackageManifest>(File.ReadAllText(path), ServiceJson.Options)
            ?? throw new InvalidOperationException("The package manifest is empty.");
        
        if (!string.Equals(package.PackageId, packageId, StringComparison.Ordinal))
            throw new InvalidOperationException("The configured catalog key does not match the package identity.");
        
        package.RootPath = Path.GetDirectoryName(path)!;
        return package;
    }

    /// <summary>
    /// Lists all packages in the local catalog.
    /// </summary>
    /// <returns>An enumerable of objects representing the packages in the local catalog.</returns>
    public IEnumerable<object> List()
    {
        // Iterate through all package IDs in the local catalog, ordered by their string representation.
        foreach (string id in options.Packages.Keys.Order(StringComparer.Ordinal))
        {
            // Load the package manifest for the current package ID.
            WorldPackageManifest package = Load(id);

            // Yield an anonymous object representing the package, including its ID, asset details, and total bytes.
            yield return new
            {
                packageId = id,
                asset = new { package.Asset.WorldId, package.Asset.RevisionId, package.Asset.ContentHash,
                    package.Asset.AssetSchemaVersion, package.Asset.RequiredBuildVersion },
                bytes = package.TotalBytes,
            };
        }
    }
}
