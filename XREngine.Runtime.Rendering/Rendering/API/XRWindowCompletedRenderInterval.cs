namespace XREngine.Rendering;

/// <summary>Latest completed CPU interval for one window RenderWindow invocation.</summary>
public readonly record struct XRWindowCompletedRenderInterval(
    long Sequence,
    ulong EngineRenderFrameId,
    long BackendGeneration,
    long StartQpc,
    long EndQpc,
    bool Succeeded);
