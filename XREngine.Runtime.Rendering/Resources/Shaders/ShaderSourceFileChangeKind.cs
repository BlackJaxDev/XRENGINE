namespace XREngine.Rendering;

/// <summary>
/// File-system changes observed by the shader hot-reload service.
/// </summary>
public enum ShaderSourceFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

