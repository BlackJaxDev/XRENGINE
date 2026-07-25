namespace XREngine.Rendering;

/// <summary>
/// Describes a file-system change that can invalidate a loaded shader source graph.
/// </summary>
public readonly record struct ShaderSourceFileChange(
    string Path,
    ShaderSourceFileChangeKind Kind,
    string? PreviousPath = null);

