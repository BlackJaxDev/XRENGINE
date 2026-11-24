using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

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
    /// Finds an original source toon shader name from materials in the same Unity project.
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

    private const string ShaderEvidenceCacheHeader = "#xrengine-unity-original-shader-evidence-v1";

    private void CollectOriginalShaderNames(OriginalShaderEvidence evidence)
    {
        string assetsRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Assets"));
        string packagesRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Packages"));

        // Collect all .mat paths from the already-built GUID index instead of re-walking disk.
        List<string> materialPaths = [];
        foreach (string assetPath in _index.SnapshotIndexedAssetPaths())
        {
            if (!assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!assetPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith(packagesRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            materialPaths.Add(assetPath);
        }

        materialPaths.Sort(StringComparer.OrdinalIgnoreCase);

        if (TryLoadShaderEvidenceFromDisk(materialPaths.Count, evidence))
            return;

        var discovered = new ConcurrentBag<(string Guid, string Name, string SourcePath)>();
        Parallel.ForEach(
            materialPaths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) },
            materialPath =>
            {
                try
                {
                    if (!File.Exists(materialPath))
                        return;

                    string yaml = File.ReadAllText(materialPath);
                    if (!yaml.Contains("OriginalShader:", StringComparison.Ordinal) ||
                        !yaml.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    SerializedMaterialDocument material = SerializedMaterialDocumentParser.Parse(yaml, materialPath);
                    if (!material.OverrideTags.TryGetValue("OriginalShader", out string? originalName) ||
                        !originalName.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(material.Shader.Guid))
                        discovered.Add((material.Shader.Guid, originalName, materialPath));
                    if (material.OverrideTags.TryGetValue("OriginalShaderGUID", out string? originalGuid) &&
                        !string.IsNullOrWhiteSpace(originalGuid))
                    {
                        discovered.Add((originalGuid, originalName, materialPath));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or YamlDotNet.Core.YamlException)
                {
                    // An unrelated material must not stop an import.
                }
            });

        foreach ((string guid, string name, string sourcePath) in discovered
            .OrderBy(static item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            AddShaderNameEvidence(evidence, guid, name, sourcePath);
        }

        TrySaveShaderEvidenceToDisk(materialPaths.Count, evidence);
    }

    private bool TryLoadShaderEvidenceFromDisk(int materialCount, OriginalShaderEvidence evidence)
    {
        string? cachePath = _index.TryResolveProjectSidecarCachePath("shaders.v1.tsv");
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
            return false;

        try
        {
            string expectedHeader = $"{ShaderEvidenceCacheHeader}\t{ProjectRoot}\t{materialCount}";
            using var reader = new StreamReader(cachePath, Encoding.UTF8);
            string? header = reader.ReadLine();
            if (!string.Equals(header, expectedHeader, StringComparison.Ordinal))
                return false;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                    continue;

                string[] parts = line.Split('\t');
                if (parts.Length == 2 && parts[0] == "C")
                {
                    evidence.Conflicts.Add(parts[1]);
                }
                else if (parts.Length == 4 && parts[0] == "N")
                {
                    evidence.Names[parts[1]] = (parts[2], parts[3]);
                }
                else
                {
                    evidence.Names.Clear();
                    evidence.Conflicts.Clear();
                    return false;
                }
            }

            Debug.Meshes($"[UnityGuidIndex] Loaded {evidence.Names.Count} cached Poiyomi shader evidence entries for '{ProjectRoot}'.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            evidence.Names.Clear();
            evidence.Conflicts.Clear();
            return false;
        }
    }

    private void TrySaveShaderEvidenceToDisk(int materialCount, OriginalShaderEvidence evidence)
    {
        string? cachePath = _index.TryResolveProjectSidecarCachePath("shaders.v1.tsv");
        if (string.IsNullOrWhiteSpace(cachePath))
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
            {
                writer.WriteLine($"{ShaderEvidenceCacheHeader}\t{ProjectRoot}\t{materialCount}");
                foreach (string conflictGuid in evidence.Conflicts)
                    writer.WriteLine($"C\t{conflictGuid}");
                foreach ((string guid, (string name, string sourcePath)) in evidence.Names)
                    writer.WriteLine($"N\t{guid}\t{name}\t{sourcePath}");
            }

            File.Move(tempPath, cachePath, overwrite: true);
            Debug.Meshes($"[UnityGuidIndex] Saved {evidence.Names.Count} Poiyomi shader evidence entries to disk cache for '{ProjectRoot}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk cache writes are opportunistic and never fail an import.
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
