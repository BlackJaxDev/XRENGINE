namespace XREngine.Rendering.API.Rendering.OpenXR;

public readonly record struct OpenXrDeviceLossBindingAbandonment(
    int AbandonedGenerationCount,
    int AbandonedSwapchainCount,
    int AbandonedAcquiredSwapchainCount);
