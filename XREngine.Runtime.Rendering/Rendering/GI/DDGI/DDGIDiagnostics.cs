using System.Numerics;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Cold-path inspection snapshot; constructing it never reads probe rays back from the GPU.</summary>
public sealed record DDGIDiagnostics(uint CompletedUpdates, bool WarmingUp, string UpdateMode, int ActiveCascade,
    int ScheduledProbes, int RaysPerProbe, float MeasuredMilliseconds, int[] ProbeUpdateOffsets, int[] UpdatedProbeCounts,
    bool GeometryReady, uint GeometryTriangles, uint GeometryNodes, uint ProbeCapacity, uint RayCapacity,
    Vector3[] CascadeOrigins, DDGIMemoryDiagnostics Memory,
    string UpdateStage, bool ResourcesInitialized, bool EnvironmentReady, string? PendingSubmission)
{
    public static DDGIDiagnostics? Capture(XRRenderPipelineInstance pipeline)
    {
        if (!DDGIFrameContext.TryGet(pipeline, out DDGIFrameContext? context) || context is null)
            return null;
        DDGIVolumeRuntimeState state = context.State;
        int[] offsets = new int[state.Cascades.Count];
        int[] updated = new int[offsets.Length];
        Vector3[] origins = new Vector3[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = state.Cascades[i].ProbeUpdateOffset;
            updated[i] = state.Cascades[i].UpdatedProbeCount;
            origins[i] = state.Cascades[i].Origin;
        }
        pipeline.Variables.TryGet("DDGIGeometryReady", out bool ready);
        pipeline.Variables.TryGet("DDGIGeometryTriangleCount", out uint triangles);
        pipeline.Variables.TryGet("DDGIGeometryNodeCount", out uint nodes);
        return new(state.FrameIndex, state.IsInvalidated, state.UpdateMode.ToString(), state.ActiveCascadeIndex,
            state.GetActiveCascade().ScheduledProbeCount, state.RaysPerProbe, state.MeasuredFrameTimeMs, offsets, updated, ready, triangles, nodes,
            pipeline.GetBuffer(DefaultRenderPipeline.DDGIProbeStateBufferName)?.ElementCount ?? 0,
            pipeline.GetBuffer(DefaultRenderPipeline.DDGIRayBufferName)?.ElementCount ?? 0, origins,
            DDGIMemoryDiagnostics.Capture(pipeline, state.Cascades.Count),
            context.UpdateStage.ToString(), context.HasInitializedResources,
            DDGIEnvironmentResources.IsAvailable(pipeline), context.PendingSubmission?.ToString());
    }
}
