namespace XREngine.Scene.Importers;

/// <summary>
/// Resolves Unity GUID references to project asset paths and importer metadata.
/// </summary>
public sealed class SourceAssetResolver
{
    private readonly SourceGuidIndex _index;

    public SourceAssetResolver(string projectRoot)
        : this(projectRoot, context: null)
    {
    }

    internal SourceAssetResolver(string projectRoot, SourceProjectImportContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _index = SourceGuidIndex.GetOrCreate(projectRoot);
        ImportContext = context;
    }

    public string ProjectRoot => _index.ProjectRoot;
    public SourceGuidIndex GuidIndex => _index;
    internal SourceProjectImportContext? ImportContext { get; }

    public string? Resolve(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        return _index.ResolvePath(guid);
    }

    public SourceResolvedAsset Resolve(SourceAssetReference reference)
    {
        string? assetPath = Resolve(reference.Guid);
        return new SourceResolvedAsset
        {
            Reference = reference,
            AssetPath = assetPath,
            MetaPath = assetPath is null ? null : assetPath + ".meta",
        };
    }

    public SerializedTextureImportDocument? ResolveTextureImportDocument(SourceAssetReference reference)
    {
        SourceResolvedAsset resolved = Resolve(reference);
        return resolved.AssetPath is null
            ? null
            : SerializedTextureImportDocumentParser.ParseFile(resolved.AssetPath);
    }

}
