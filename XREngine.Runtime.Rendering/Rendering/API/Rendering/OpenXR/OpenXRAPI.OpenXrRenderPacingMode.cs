namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Controls where OpenXR's next-frame preparation (xrWaitFrame / xrBeginFrame / LocateViews(Predicted) /
    /// UpdateActionPoseCaches(Predicted)) runs.
    /// </summary>
    public enum OpenXrRenderPacingMode
    {
        /// <summary>Run prep inline at the start of the render callback (legacy behavior).</summary>
        InRenderCallback,
        /// <summary>Run prep at the end of the render callback after desktop viewports finish (default).</summary>
        PostRenderCallback,
        /// <summary>Run prep on a dedicated OpenXR pacing thread; the render thread only signals after xrEndFrame.</summary>
        DedicatedThread,
        /// <summary>Run prep on the engine CollectVisible thread before building OpenXR visibility buffers.</summary>
        CollectVisibleThread
    }
}
