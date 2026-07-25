using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

public sealed record RendererBackendBuildResult(
    bool Succeeded,
    RendererBackendId BackendId,
    long Generation,
    string? ManifestPath,
    string Output,
    IReadOnlyList<RendererBackendBuildDiagnostic> Diagnostics,
    TimeSpan Duration,
    bool Cancelled = false);
