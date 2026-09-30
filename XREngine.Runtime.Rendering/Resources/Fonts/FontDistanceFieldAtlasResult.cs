namespace XREngine.Rendering;

/// <summary>Process outcome and diagnostics for a generated distance-field atlas.</summary>
public readonly record struct FontDistanceFieldAtlasResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Diagnostic);
