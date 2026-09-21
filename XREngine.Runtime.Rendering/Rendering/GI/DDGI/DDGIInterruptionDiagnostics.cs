namespace XREngine.Rendering.GI.DDGI;

/// <summary>Development-only controls for verifying DDGI interruption receipt lifetime.</summary>
public static class DDGIInterruptionDiagnostics
{
    /// <summary>Arms one exact pipeline context to skip Visibility border copies for a bounded count.</summary>
    public static bool TryArm(XRRenderPipelineInstance pipeline, int skipCount, out DDGIInterruptionDiagnosticSnapshot? snapshot, out string? failure)
    {
#if !XRE_PUBLISHED
        if (!DDGIFrameContext.TryGet(pipeline, out DDGIFrameContext? context) || context is null)
        {
            snapshot = null;
            failure = "The selected pipeline has no DDGI frame context yet.";
            return false;
        }
        return context.TryArmInterruptionDiagnostic(pipeline, skipCount, out snapshot, out failure);
#else
        snapshot = null;
        failure = "DDGI interruption diagnostics are unavailable in published builds.";
        return false;
#endif
    }

    /// <summary>Returns the active interruption snapshot for one exact pipeline context.</summary>
    public static bool TryGetSnapshot(XRRenderPipelineInstance pipeline, out DDGIInterruptionDiagnosticSnapshot? snapshot)
    {
#if !XRE_PUBLISHED
        snapshot = null;
        return DDGIFrameContext.TryGet(pipeline, out DDGIFrameContext? context) &&
            context is not null && context.TryGetInterruptionDiagnosticSnapshot(pipeline, out snapshot);
#else
        snapshot = null;
        return false;
#endif
    }
}
