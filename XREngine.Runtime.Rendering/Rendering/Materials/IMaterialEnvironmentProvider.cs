namespace XREngine.Rendering.Materials;

/// <summary>
/// Bridges world-owned environment lighting into material shader semantics.
/// </summary>
public interface IMaterialEnvironmentProvider
{
    bool TryGetFrame(out MaterialEnvironmentFrame frame);
}
