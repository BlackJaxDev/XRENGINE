namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Parameters for one runtime-owned graphics swapchain.</summary>
public readonly record struct OpenXrSwapchainDescriptor(
    ulong CreateFlags,
    ulong UsageFlags,
    long Format,
    uint Width,
    uint Height,
    uint SampleCount,
    uint FaceCount,
    uint ArraySize,
    uint MipCount);
