namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies deterministic desktop-frame phase boundaries that can inject a
/// one-shot failure without adding delegates or allocations to the render path.
/// </summary>
internal enum EVulkanDesktopFrameFaultPoint
{
    None = 0,
    Acquire = 1,
    ImagePreparation = 2,
    SceneRecording = 3,
    OverlayRecording = 4,
    Submission = 5,
    PostSubmitAuxiliary = 6,
    Presentation = 7,
    PostPresentAuxiliary = 8,
}
