using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Contains the resources used by the anti-aliasing passes of the default render pipeline.
/// </summary>
internal static class RenderPipelineAntiAliasingResources
{
    /// <summary>
    /// Invalidates temporal histories after an anti-aliasing profile change.
    /// Structural resource changes are handled by replacement generations.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    /// <param name="reason">The reason for invalidation.</param>
    internal static void InvalidateAntiAliasingResources(XRRenderPipelineInstance instance, string reason = "AntiAliasingSettingsChanged")
    {
        VPRC_TemporalAccumulationPass.ResetHistory(instance);
        VPRC_AtmosphereHistoryPass.ResetHistory(instance);
        VPRC_VolumetricFogHistoryPass.ResetHistory(instance);
    }

    /// <summary>
    /// Invalidates temporal histories after a viewport resize.
    /// </summary>
    /// <param name="instance">The render pipeline instance.</param>
    internal static void InvalidateViewportResizeResources(XRRenderPipelineInstance instance)
    {
        InvalidateAntiAliasingResources(instance, "ViewportResized");
    }
}
