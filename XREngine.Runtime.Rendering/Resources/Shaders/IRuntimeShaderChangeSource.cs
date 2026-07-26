namespace XREngine.Rendering;

/// <summary>
/// Optional runtime service capability that publishes asset-root file changes.
/// </summary>
public interface IRuntimeShaderChangeSource
{
    event Action<ShaderSourceFileChange>? ShaderSourceFileChanged;
}

