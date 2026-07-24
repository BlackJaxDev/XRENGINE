namespace XREngine.Scene.Importers;

/// <summary>
/// Resolves Unity GUID references to project asset paths and importer metadata.
/// </summary>
public sealed class UnityAssetResolver
{
    private readonly string _projectRoot;
    private readonly Dictionary<string, string> _assetPathsByGuid = new(StringComparer.OrdinalIgnoreCase);
    private bool _indexInitialized;

    public UnityAssetResolver(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    public string ProjectRoot => _projectRoot;

    public string? Resolve(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        EnsureGuidIndex();
        return _assetPathsByGuid.TryGetValue(guid, out string? path) ? path : null;
    }

    public UnityResolvedAsset Resolve(UnityAssetReference reference)
    {
        string? assetPath = Resolve(reference.Guid);
        return new UnityResolvedAsset
        {
            Reference = reference,
            AssetPath = assetPath,
            MetaPath = assetPath is null ? null : assetPath + ".meta",
        };
    }

    public UnityTextureImportDocument? ResolveTextureImportDocument(UnityAssetReference reference)
    {
        UnityResolvedAsset resolved = Resolve(reference);
        return resolved.AssetPath is null
            ? null
            : UnityTextureImportDocumentParser.ParseFile(resolved.AssetPath);
    }

    private void EnsureGuidIndex()
    {
        if (_indexInitialized)
            return;

        foreach (string root in EnumerateUnitySearchRoots())
        {
            foreach (string metaPath in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories))
            {
                string? guid = TryReadGuid(metaPath);
                if (string.IsNullOrWhiteSpace(guid))
                    continue;

                string assetPath = metaPath[..^5];
                if (File.Exists(assetPath))
                    _assetPathsByGuid.TryAdd(guid, assetPath);
            }
        }

        _indexInitialized = true;
    }

    private IEnumerable<string> EnumerateUnitySearchRoots()
    {
        string assetsRoot = Path.Combine(_projectRoot, "Assets");
        if (Directory.Exists(assetsRoot))
            yield return assetsRoot;

        string packagesRoot = Path.Combine(_projectRoot, "Packages");
        if (Directory.Exists(packagesRoot))
            yield return packagesRoot;

        if (!Directory.Exists(assetsRoot) && !Directory.Exists(packagesRoot) && Directory.Exists(_projectRoot))
            yield return _projectRoot;
    }

    private static string? TryReadGuid(string metaPath)
    {
        foreach (string line in File.ReadLines(metaPath))
        {
            const string prefix = "guid: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }
}
