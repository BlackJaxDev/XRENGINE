using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Shared state for one Unity project import, including resolution, caches, diagnostics, and output intent.
/// </summary>
public sealed class SourceProjectImportContext
{
    private readonly Dictionary<string, XRMaterial> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XRTexture2D> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, long LastWriteTicks)> _validatedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _modelTexturePathLock = new();
    private Dictionary<string, string?>? _modelTexturePathsByName;
    private readonly HashSet<string> _reportedModelTextureNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SerializedAnimatorRecord, SceneNode> _animatorOwners = [];
    private readonly Dictionary<SerializedAvatarAnimationGraphRecord, SceneNode> _avatarAnimationGraphOwners = [];
    private readonly HashSet<string> _reportedIndexDiagnostics = new(StringComparer.Ordinal);
    private bool _recoveredPartialContent;

    public SourceProjectImportContext(
        string entrySourcePath,
        string? outputDestination = null,
        string? explicitProjectOrAssetsRoot = null,
        CancellationToken cancellationToken = default,
        Action<float, string>? progress = null,
        FbxImportBackend fbxBackend = FbxImportBackend.Assimp,
        bool allowPartialImport = false)
    {
        EntrySourcePath = Path.GetFullPath(entrySourcePath);
        OutputDestination = string.IsNullOrWhiteSpace(outputDestination)
            ? null
            : Path.GetFullPath(outputDestination);
        ProjectLocation = SourceProjectLocator.Locate(EntrySourcePath, explicitProjectOrAssetsRoot);
        GuidIndex = SourceGuidIndex.GetOrCreate(ProjectLocation.ProjectRoot);
        FbxBackend = fbxBackend;
        AllowPartialImport = allowPartialImport;
        Resolver = new SourceAssetResolver(ProjectLocation.ProjectRoot, this);
        CancellationToken = cancellationToken;
        Progress = progress;
        ImportStartedAtUtc = DateTime.UtcNow;
        SynchronizeIndexDiagnostics();
    }

    public string EntrySourcePath { get; }
    public string ProjectRoot => ProjectLocation.ProjectRoot;
    public string? OutputDestination { get; }
    public SourceProjectLocation ProjectLocation { get; }
    public SourceGuidIndex GuidIndex { get; }
    public SourceAssetResolver Resolver { get; }
    public SourceDependencyGraph? DependencyGraph { get; internal set; }
    public List<SourceImportDiagnostic> Diagnostics { get; } = [];
    public List<SerializedAnimatorRecord> Animators { get; } = [];
    public List<SerializedAvatarAnimationGraphRecord> AvatarAnimationGraphs { get; } = [];
    public List<UnsupportedSourceBehaviourMetadata> UnsupportedBehaviours { get; } = [];
    public HashSet<string> ActiveImports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<SourceAssetIdentity, object> ImportCache { get; } = [];
    /// <summary>Parsed source data for the active root import. Returned documents are read-only.</summary>
    internal XREngine.Fbx.FbxSourceDocumentCache FbxSourceDocuments { get; } = new();
    public CancellationToken CancellationToken { get; }
    public Action<float, string>? Progress { get; }
    /// <summary>Selects the backend for FBX model bases resolved during Unity prefab composition.</summary>
    public FbxImportBackend FbxBackend { get; }
    /// <summary>Allows available content to import when visual dependencies are missing.</summary>
    public bool AllowPartialImport { get; }
    /// <summary>Indicates that source content was omitted or replaced during conversion.</summary>
    public bool HasPartialContent => _recoveredPartialContent ||
        DependencyGraph?.UnresolvedEdges.Any(static edge => edge.Kind == SourceImportDependencyKind.RequiredVisual) == true;
    public DateTime ImportStartedAtUtc { get; }

    /// <summary>Checks whether this import already decoded the same texture revision.</summary>
    internal bool IsTextureValidated(string sourcePath, long length, long lastWriteTicks)
        => _validatedTextures.TryGetValue(sourcePath, out var revision) &&
           revision == (length, lastWriteTicks);

    /// <summary>Records successful decoding without retaining image pixels.</summary>
    internal void RecordValidatedTexture(string sourcePath, long length, long lastWriteTicks)
        => _validatedTextures[sourcePath] = (length, lastWriteTicks);

    /// <summary>Finds one exact texture filename in the composed Unity source.</summary>
    internal string? ResolveExactModelTexture(string modelPath, string rawPath)
    {
        string fileName = Path.GetFileName(rawPath.Trim().Trim('"').Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        lock (_modelTexturePathLock)
        {
            _modelTexturePathsByName ??= BuildModelTexturePathIndex();
            if (!_modelTexturePathsByName.TryGetValue(fileName, out string? resolved))
                return null;

            if (_reportedModelTextureNames.Add(fileName))
            {
                AddDiagnostic(
                    resolved is null ? "UNITYMODEL0012" : "UNITYMODEL0011",
                    resolved is null ? SourceImportDiagnosticSeverity.Warning : SourceImportDiagnosticSeverity.Info,
                    SourceImportDiagnosticCategory.TextureImport,
                    resolved is null
                        ? $"FBX texture '{fileName}' has more than one exact filename in the composed Unity source. The binding stays unresolved."
                        : $"FBX texture '{fileName}' resolved to the exact composed Unity asset '{Path.GetRelativePath(ProjectRoot, resolved)}'.",
                    modelPath,
                    rawPath);
            }

            return resolved;
        }
    }

    private Dictionary<string, string?> BuildModelTexturePathIndex()
    {
        Dictionary<string, string?> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceRootName in new[] { "Assets", "Packages" })
        {
            string sourceRoot = Path.Combine(ProjectRoot, sourceRootName);
            if (!Directory.Exists(sourceRoot))
                continue;

            try
            {
                foreach (string candidate in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    if (candidate.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(candidate + ".meta"))
                        continue;

                    string fileName = Path.GetFileName(candidate);
                    if (!paths.TryAdd(fileName, candidate) &&
                        paths[fileName] is { } existing &&
                        !string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
                        paths[fileName] = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddDiagnostic(
                    "UNITYMODEL0013",
                    SourceImportDiagnosticSeverity.Warning,
                    SourceImportDiagnosticCategory.TextureImport,
                    $"Could not inspect composed Unity texture assets under '{sourceRootName}': {ex.Message}",
                    sourceRoot);
            }
        }

        return paths;
    }

    public SourceDependencyGraph DiscoverDependencies()
    {
        Progress?.Invoke(0.02f, "Discovering Unity dependencies");
        SourceDependencyGraph graph = SourceDependencyGraphBuilder.Build(this);
        SynchronizeIndexDiagnostics();

        SourceDependencyEdge[] requiredMissing =
        [
            .. graph.UnresolvedEdges.Where(static edge => edge.Kind == SourceImportDependencyKind.RequiredVisual),
        ];
        if (requiredMissing.Length > 0 && !AllowPartialImport)
        {
            string summary = string.Join(
                "; ",
                requiredMissing.Select(static edge => $"{edge.TargetGuid} ({edge.ReferringProperty})"));
            throw new SourceVisualImportException(
                $"Unity prefab import cannot continue because required visual dependencies are missing: {summary}");
        }

        return graph;
    }

    /// <summary>Records omitted or replaced source content without claiming complete conversion.</summary>
    public void ReportPartialImport(string code, string message, string? sourcePath = null,
        string? propertyPath = null, SourceAssetIdentity? identity = null)
    {
        _recoveredPartialContent = true;
        AddDiagnostic(code, SourceImportDiagnosticSeverity.Warning,
            SourceImportDiagnosticCategory.MaterialDowngrade, message, sourcePath, propertyPath, identity);
        Debug.MeshesWarning(message);
    }

    public T GetOrAddCached<T>(string sourcePath, Func<T> factory) where T : class
    {
        Dictionary<string, T> cache = typeof(T) == typeof(XRMaterial)
            ? (Dictionary<string, T>)(object)_materialCache
            : typeof(T) == typeof(XRTexture2D)
                ? (Dictionary<string, T>)(object)_textureCache
                : throw new NotSupportedException($"No path cache is registered for '{typeof(T).FullName}'.");

        string normalized = Path.GetFullPath(sourcePath);
        if (cache.TryGetValue(normalized, out T? existing))
            return existing;

        T created = factory();
        cache.Add(normalized, created);
        return created;
    }

    public void MarkOutcome(
        string sourcePath,
        SourceImportConversionOutcome outcome,
        string? outputAssetPath = null)
    {
        if (DependencyGraph is not SourceDependencyGraph graph ||
            !graph.Nodes.TryGetValue(Path.GetFullPath(sourcePath), out SourceDependencyNode? node))
            return;

        node.Outcome = outcome;
        node.OutputAssetPath = outputAssetPath;
    }

    internal void IgnoreStalePrefabModification(string sourcePath, long referringObjectFileId)
        => DependencyGraph?.RemovePrefabModificationEdges(
            sourcePath,
            referringObjectFileId,
            EntrySourcePath);

    public void AddDiagnostic(
        string code,
        SourceImportDiagnosticSeverity severity,
        SourceImportDiagnosticCategory category,
        string message,
        string? sourcePath = null,
        string? propertyPath = null,
        SourceAssetIdentity? identity = null)
    {
        Diagnostics.Add(new SourceImportDiagnostic
        {
            Code = code,
            Severity = severity,
            Category = category,
            Message = message,
            SourcePath = sourcePath,
            PropertyPath = propertyPath,
            SourceIdentity = identity,
        });
    }

    internal void RegisterAnimator(SceneNode owner, SerializedAnimatorRecord record)
    {
        Animators.Add(record);
        _animatorOwners.Add(record, owner);
    }

    internal void UnregisterAnimator(SerializedAnimatorRecord record)
    {
        Animators.Remove(record);
        _animatorOwners.Remove(record);
    }

    internal void RegisterAvatarAnimationGraph(
        SceneNode owner,
        SerializedAvatarAnimationGraphRecord record)
    {
        AvatarAnimationGraphs.Add(record);
        _avatarAnimationGraphOwners.Add(record, owner);
    }

    internal void UnregisterAvatarAnimationGraph(SerializedAvatarAnimationGraphRecord record)
    {
        AvatarAnimationGraphs.Remove(record);
        _avatarAnimationGraphOwners.Remove(record);
    }

    public SerializedPrefabImportManifest CreateManifest(SourceImportCompletionTier completionTier)
    {
        SynchronizeIndexDiagnostics();
        UpdateImportEvidencePaths();
        var manifest = new SerializedPrefabImportManifest
        {
            EntrySourcePath = EntrySourcePath,
            SourceProjectRoot = ProjectRoot,
            SourceEditorVersion = ProjectLocation.SourceEditorVersion,
            OutputAssetPath = OutputDestination,
            CompletionTier = completionTier,
            ImportedAtUtc = DateTime.UtcNow,
            Diagnostics = [.. Diagnostics],
            Animators = [.. Animators],
            AvatarAnimationGraphs = [.. AvatarAnimationGraphs],
            UnsupportedBehaviours = [.. UnsupportedBehaviours],
        };

        if (DependencyGraph is null)
            return manifest;

        foreach (SourceDependencyNode node in DependencyGraph.Nodes.Values
            .OrderBy(static node => node.PortablePath, StringComparer.OrdinalIgnoreCase))
        {
            var fileInfo = new FileInfo(node.SourcePath);
            SourceDependencyEdge? inboundEdge = DependencyGraph.Nodes.Values
                .SelectMany(static dependency => dependency.OutgoingEdges)
                .Where(edge =>
                    !string.IsNullOrWhiteSpace(edge.TargetPath) &&
                    string.Equals(
                        Path.GetFullPath(edge.TargetPath),
                        node.SourcePath,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(static edge => edge.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static edge => edge.ReferringProperty, StringComparer.Ordinal)
                .ThenBy(static edge => edge.TargetFileId)
                .FirstOrDefault();
            manifest.Dependencies.Add(new SourceImportDependencyManifestEntry
            {
                SourceGuid = node.SourceGuid,
                LocalFileId = inboundEdge?.TargetFileId,
                NormalizedPath = node.PortablePath,
                Kind = node.Kind,
                ReferringProperty = inboundEdge?.ReferringProperty,
                LastWriteTimeUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
                Length = fileInfo.Exists ? fileInfo.Length : 0,
                Sha256 = fileInfo.Exists ? SourceDependencyGraphBuilder.ComputeSha256(node.SourcePath) : string.Empty,
                OutputAssetPath = node.OutputAssetPath,
                Outcome = node.Outcome == SourceImportConversionOutcome.Pending
                    ? DefaultOutcome(node.Kind)
                    : node.Outcome,
            });
        }

        foreach (SourceDependencyEdge missing in DependencyGraph.UnresolvedEdges
            .OrderBy(static edge => edge.TargetGuid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static edge => edge.TargetFileId))
        {
            manifest.Dependencies.Add(new SourceImportDependencyManifestEntry
            {
                SourceGuid = missing.TargetGuid,
                LocalFileId = missing.TargetFileId,
                NormalizedPath = $"missing://{missing.TargetGuid}/{missing.TargetFileId}",
                Kind = missing.Kind,
                ReferringProperty = missing.ReferringProperty,
                Outcome = SourceImportConversionOutcome.Missing,
            });
        }

        return manifest;
    }

    private void UpdateImportEvidencePaths()
    {
        foreach ((SerializedAnimatorRecord record, SceneNode owner) in _animatorOwners)
            record.SceneNodePath = GetSceneNodePath(owner);

        foreach ((SerializedAvatarAnimationGraphRecord record, SceneNode owner) in _avatarAnimationGraphOwners)
            record.SceneNodePath = GetSceneNodePath(owner);
    }

    private static string GetSceneNodePath(SceneNode node)
    {
        var segments = new Stack<string>();
        for (SceneNode? current = node; current is not null; current = current.Parent)
            segments.Push(current.Name ?? SceneNode.DefaultName);
        return string.Join('/', segments);
    }

    private void SynchronizeIndexDiagnostics()
    {
        foreach (SourceImportDiagnostic diagnostic in GuidIndex.Diagnostics)
        {
            string key = $"{diagnostic.Code}\0{diagnostic.SourcePath}\0{diagnostic.Message}";
            if (_reportedIndexDiagnostics.Add(key))
                Diagnostics.Add(diagnostic);
        }
    }

    private static SourceImportConversionOutcome DefaultOutcome(SourceImportDependencyKind kind)
        => kind is SourceImportDependencyKind.AvatarBehavior or SourceImportDependencyKind.EditorOnly or SourceImportDependencyKind.Unsupported
            ? SourceImportConversionOutcome.IgnoredOptional
            : SourceImportConversionOutcome.Converted;
}
