namespace XREngine.Scene.Importers;

/// <summary>
/// Resolves Unity GUID references to project asset paths and importer metadata.
/// </summary>
public sealed class UnityAssetResolver
{
    private readonly UnityGuidIndex _index;

    public UnityAssetResolver(string projectRoot)
        : this(projectRoot, context: null)
    {
    }

    internal UnityAssetResolver(string projectRoot, UnityProjectImportContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _index = UnityGuidIndex.GetOrCreate(projectRoot);
        ImportContext = context;
    }

    public string ProjectRoot => _index.ProjectRoot;
    public UnityGuidIndex GuidIndex => _index;
    internal UnityProjectImportContext? ImportContext { get; }

    public string? Resolve(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        return _index.ResolvePath(guid);
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

}
