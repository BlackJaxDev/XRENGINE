namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Runtime view dimensions and sample-count limits without native XR structs.</summary>
public readonly record struct OpenXrViewConfiguration(
    uint RecommendedWidth,
    uint RecommendedHeight,
    uint MaxWidth,
    uint MaxHeight,
    uint RecommendedSampleCount,
    uint MaxSampleCount);
