namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Requested and resolved dimensions for a single OpenXR eye image.</summary>
public readonly record struct OpenXrEyeSwapchainExtent(
    uint Width,
    uint Height,
    uint RequestedWidth,
    uint RequestedHeight,
    uint BaseWidth,
    uint BaseHeight,
    uint RecommendedWidth,
    uint RecommendedHeight,
    uint MaxWidth,
    uint MaxHeight,
    EOpenXrEyeResolutionPreset Preset,
    float Scale,
    bool ExceedsRuntimeMax,
    string Source);
