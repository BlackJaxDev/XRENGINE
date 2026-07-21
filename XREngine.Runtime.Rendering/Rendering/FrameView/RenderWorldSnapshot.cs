namespace XREngine.Rendering;

/// <summary>
/// Immutable publication token for the render-side scene buffers of one engine frame.
/// The referenced scene and GPU scene expose their already-swapped render buffers; workers must not
/// reacquire scene state outside this snapshot while processing it.
/// </summary>
public readonly record struct RenderWorldSnapshot(
    ulong FrameId,
    IRuntimeRenderCommandSceneContext Scene,
    GPUScene GpuScene);