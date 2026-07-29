namespace XREngine.Rendering;

/// <summary>
/// Supplies the probe arrays and bindings shared by deferred, forward, and visibility shading.
/// </summary>
public interface IPbrLightingResourceProvider
{
    XRTexture2DArray? ProbeIrradianceArray { get; }
    XRTexture2DArray? ProbePrefilterArray { get; }
    int ProbeCount { get; }

    bool BindPbrLightingResources(
        XRRenderProgram program,
        bool deferredProbeBufferBindings = false);

    void SyncPbrLightingResourcesForFrame();
}
