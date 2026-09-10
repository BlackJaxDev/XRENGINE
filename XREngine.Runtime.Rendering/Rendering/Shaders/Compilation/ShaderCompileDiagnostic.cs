namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// A frontend diagnostic mapped to its original source location where available.
/// </summary>
public readonly record struct ShaderCompileDiagnostic(string? OriginalPath, int? Line, string Message, int? Column = null, string? Severity = null);
