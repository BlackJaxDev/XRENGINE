namespace XREngine.Rendering.Commands;

/// <summary>Keeps a mutable resource generation alive while a publication snapshot exposes it.</summary>
public interface IAdvancedGpuPublicationSourceLifetime
{
    bool TryRetainPublication();
    /// <summary>
    /// Retains the exact content revision captured by the source encoder. Immutable
    /// lifetime objects may use the default implementation; reusable textures must
    /// reject a stale revision atomically with writer admission.
    /// </summary>
    bool TryRetainPublication(ulong expectedContentGeneration) => TryRetainPublication();
    void ReleasePublication();
}
