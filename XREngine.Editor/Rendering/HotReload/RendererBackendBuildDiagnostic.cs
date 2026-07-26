namespace XREngine.Editor.HotReload;

public sealed record RendererBackendBuildDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? File,
    int? Line,
    int? Column);
