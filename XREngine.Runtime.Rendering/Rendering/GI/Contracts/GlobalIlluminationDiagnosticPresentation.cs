using System.Runtime.CompilerServices;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Tracks a provider-owned diagnostic presentation for the current render frame
/// without exposing an algorithm runtime to generic post-processing.
/// </summary>
public static class GlobalIlluminationDiagnosticPresentation
{
    private sealed class FrameMarker
    {
        public ulong RenderFrameId { get; set; } = ulong.MaxValue;
    }

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, FrameMarker> Markers = new();

    public static void Mark(XRRenderPipelineInstance pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        Markers.GetValue(pipeline, static _ => new()).RenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
    }

    public static bool IsCurrentFrame(XRRenderPipelineInstance pipeline)
        => Markers.TryGetValue(pipeline, out FrameMarker? marker) &&
            marker.RenderFrameId == RuntimeEngine.Rendering.State.RenderFrameId;
}
