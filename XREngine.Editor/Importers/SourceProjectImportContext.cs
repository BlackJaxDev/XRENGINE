using XREngine.Rendering;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Shared state for one Unity project import, including resolution, caches, diagnostics, and output intent.
/// </summary>
public sealed class SourceProjectImportContext
{
    private readonly Dictionary<string, XRMaterial> _materialCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XRTexture2D> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedIndexDiagnostics = new(StringComparer.Ordinal);

    public SourceProjectImportContext(
        string entrySourcePath,
        string? outputDestination = null,
        string? explicitProjectOrAssetsRoot = null,
        CancellationToken cancellationToken = default,
        Action<float, string>? progress = null)
    {
        EntrySourcePath = Path.GetFullPath(entrySourcePath);
        OutputDestination = string.IsNullOrWhiteSpace(outputDestination)
            ? null
            : Path.GetFullPath(outputDestination);
        ProjectLocation = SourceProjectLocator.Locate(EntrySourcePath, explicitProjectOrAssetsRoot);
        GuidIndex = SourceGuidIndex.GetOrCreate(ProjectLocation.ProjectRoot);
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
    public List<UnsupportedSourceBehaviourMetadata> UnsupportedBehaviours { get; } = [];
    public HashSet<string> ActiveImports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<SourceAssetIdentity, object> ImportCache { get; } = [];
    public CancellationToken CancellationToken { get; }
    public Action<float, string>? Progress { get; }
    public DateTime ImportStartedAtUtc { get; }

    public SourceDependencyGraph DiscoverDependencies()
    {
        Progress?.Invoke(0.02f, "Discovering Unity dependencies");
        SourceDependencyGraph graph = SourceDependencyGraphBuilder.Build(this);
        SynchronizeIndexDiagnostics();

        SourceDependencyEdge[] requiredMissing =
        [
            .. graph.UnresolvedEdges.Where(static edge => edge.Kind == SourceImportDependencyKind.RequiredVisual),
        ];
        if (requiredMissing.Length > 0)
        {
            string summary = string.Join(
                "; ",
                requiredMissing.Select(static edge => $"{edge.TargetGuid} ({edge.ReferringProperty})"));
            throw new SourceVisualImportException(
                $"Unity prefab import cannot continue because required visual dependencies are missing: {summary}");
        }

        return graph;
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

    public SerializedPrefabImportManifest CreateManifest(SourceImportCompletionTier completionTier)
    {
        SynchronizeIndexDiagnostics();
        var manifest = new SerializedPrefabImportManifest
        {
            EntrySourcePath = EntrySourcePath,
            SourceProjectRoot = ProjectRoot,
            SourceEditorVersion = ProjectLocation.SourceEditorVersion,
            OutputAssetPath = OutputDestination,
            CompletionTier = completionTier,
            ImportedAtUtc = DateTime.UtcNow,
            Diagnostics = [.. Diagnostics],
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
