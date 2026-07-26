namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Bridges world-owned environment lighting into Poiyomi material semantics.
/// </summary>
public interface IPoiyomiEnvironmentProvider
{
    bool TryGetFrame(out PoiyomiEnvironmentFrame frame);
}
