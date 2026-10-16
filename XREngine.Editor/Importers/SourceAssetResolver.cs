using System.Runtime.CompilerServices;

namespace XREngine.Scene.Importers;

/// <summary>
/// Resolves Unity GUID references to project asset paths and importer metadata.
/// </summary>
public sealed class SourceAssetResolver
{
    private static readonly ConditionalWeakTable<SourceGuidIndex, OriginalShaderEvidence> OriginalShaders = new();
    private readonly SourceGuidIndex _index;

    private sealed class OriginalShaderEvidence
    {
        public int Generation { get; set; } = -1;
        public Dictionary<string, (string Name, string SourcePath)> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Conflicts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// Finds an original Poiyomi shader name from materials in the same Unity project.
    /// Conflicting names for one GUID do not provide shader family evidence.
    /// </summary>
    public bool TryFindOriginalShaderName(string? shaderGuid, out string name, out string sourcePath)
    {
        name = string.Empty;
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(shaderGuid))
            return false;

        _index.Resolve(shaderGuid);
        OriginalShaderEvidence evidence = OriginalShaders.GetValue(_index, static _ => new OriginalShaderEvidence());
        lock (evidence)
        {
            if (evidence.Generation != _index.Generation)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int generation = _index.Generation;
                    evidence.Names.Clear();
                    evidence.Conflicts.Clear();
                    CollectOriginalShaderNames(evidence);
                    _index.Resolve(shaderGuid);
                    if (_index.Generation != generation)
                        continue;

                    evidence.Generation = generation;
                    break;
                }
            }

            if (evidence.Generation != _index.Generation ||
                !evidence.Names.TryGetValue(shaderGuid, out (string Name, string SourcePath) found))
                return false;

            name = found.Name;
            sourcePath = found.SourcePath;
            return true;
        }
    }

    private void CollectOriginalShaderNames(OriginalShaderEvidence evidence)
    {
        foreach (string rootName in new[] { "Assets", "Packages" })
        {
            string root = Path.Combine(ProjectRoot, rootName);
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (string materialPath in Directory.EnumerateFiles(root, "*.mat", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        string yaml = File.ReadAllText(materialPath);
                        if (!yaml.Contains("OriginalShader:", StringComparison.Ordinal) ||
                            !yaml.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase))
                            continue;

                        SerializedMaterialDocument material = SerializedMaterialDocumentParser.Parse(yaml, materialPath);
                        if (!material.OverrideTags.TryGetValue("OriginalShader", out string? originalName) ||
                            !originalName.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase))
                            continue;

                        AddShaderNameEvidence(evidence, material.Shader.Guid, originalName, materialPath);
                        if (material.OverrideTags.TryGetValue("OriginalShaderGUID", out string? originalGuid))
                            AddShaderNameEvidence(evidence, originalGuid, originalName, materialPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or YamlDotNet.Core.YamlException)
                    {
                        // An unrelated material must not stop an import.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The current import still uses direct shader and material evidence.
            }
        }
    }

    private static void AddShaderNameEvidence(
        OriginalShaderEvidence evidence,
        string? guid,
        string name,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(guid) || evidence.Conflicts.Contains(guid))
            return;

        if (evidence.Names.TryGetValue(guid, out (string Name, string SourcePath) existing) &&
            !string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            evidence.Names.Remove(guid);
            evidence.Conflicts.Add(guid);
            return;
        }

        evidence.Names.TryAdd(guid, (name, Path.GetFullPath(sourcePath)));
    }

    public SerializedTextureImportDocument? ResolveTextureImportDocument(SourceAssetReference reference)
    {
        SourceResolvedAsset resolved = Resolve(reference);
        return resolved.AssetPath is null
            ? null
            : SerializedTextureImportDocumentParser.ParseFile(resolved.AssetPath);
    }

}
