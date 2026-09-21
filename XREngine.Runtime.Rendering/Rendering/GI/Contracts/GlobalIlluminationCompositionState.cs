using System.Runtime.CompilerServices;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Carries the current-frame handoff from a provider screen resolve to the
/// host-neutral fullscreen composition command.
/// </summary>
public static class GlobalIlluminationCompositionState
{
    private sealed class FrameState
    {
        public ulong FrameId { get; set; } = ulong.MaxValue;
        public bool ReplaceDestination { get; set; }
        public bool IsDiagnostic { get; set; }
        public bool ShouldCompose { get; set; }
        public bool WasComposited { get; set; }
        public bool HasPublishedOpaqueReplacement { get; set; }
    }

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, FrameState> States = new();

    public static void Prepare(XRRenderPipelineInstance pipeline, bool replaceDestination, bool isDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        FrameState state = States.GetValue(pipeline, static _ => new());
        state.FrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        state.ReplaceDestination = replaceDestination;
        state.IsDiagnostic = isDiagnostic;
        // A normal radiance result is first published while the probe fallback remains
        // visible. The next frame replaces that fallback, preventing both an
        // initialization black frame and a fallback-plus-GI double contribution.
        state.ShouldCompose = isDiagnostic || state.HasPublishedOpaqueReplacement;
        state.WasComposited = false;
    }

    public static bool TryGetPrepared(
        XRRenderPipelineInstance pipeline,
        out bool replaceDestination,
        out bool isDiagnostic,
        out bool shouldCompose)
    {
        if (States.TryGetValue(pipeline, out FrameState? state) &&
            state.FrameId == RuntimeEngine.Rendering.State.RenderFrameId)
        {
            replaceDestination = state.ReplaceDestination;
            isDiagnostic = state.IsDiagnostic;
            shouldCompose = state.ShouldCompose;
            return true;
        }

        replaceDestination = false;
        isDiagnostic = false;
        shouldCompose = false;
        return false;
    }

    /// <summary>
    /// Makes a completed non-debug opaque resolve eligible to replace probe
    /// diffuse on the following frame. Until then the declared probe fallback
    /// remains visible.
    /// </summary>
    public static void MarkOpaqueReplacementAvailable(XRRenderPipelineInstance pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        States.GetValue(pipeline, static _ => new()).HasPublishedOpaqueReplacement = true;
    }

    /// <summary>
    /// Returns whether deferred opaque shading may omit its baseline probe
    /// diffuse term for this frame. It is deliberately false during provider
    /// initialization, so invalid output is never interpreted as black GI.
    /// </summary>
    public static bool ShouldSuppressBaselineDiffuse(
        XRRenderPipelineInstance? pipeline,
        GlobalIlluminationPlan plan)
        => pipeline is not null && plan.ReplacesProbeDiffuse &&
            States.TryGetValue(pipeline, out FrameState? state) &&
            state.HasPublishedOpaqueReplacement;

    public static void MarkComposited(XRRenderPipelineInstance pipeline)
    {
        if (States.TryGetValue(pipeline, out FrameState? state) &&
            state.FrameId == RuntimeEngine.Rendering.State.RenderFrameId)
            state.WasComposited = true;
    }

    public static bool TryGetComposited(XRRenderPipelineInstance pipeline, out bool isDiagnostic)
    {
        if (States.TryGetValue(pipeline, out FrameState? state) &&
            state.FrameId == RuntimeEngine.Rendering.State.RenderFrameId && state.WasComposited)
        {
            isDiagnostic = state.IsDiagnostic;
            return true;
        }

        isDiagnostic = false;
        return false;
    }
}
