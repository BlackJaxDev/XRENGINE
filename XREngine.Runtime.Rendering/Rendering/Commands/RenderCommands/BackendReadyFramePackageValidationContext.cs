namespace XREngine.Rendering.Commands;

/// <summary>
/// Render-consumer generations used for bounded package validation.
/// </summary>
public readonly record struct BackendReadyFramePackageValidationContext(
    long ConsumedCollectGeneration,
    ulong CommandGeneration,
    int ResourceGeneration,
    int DescriptorGeneration,
    int RenderGraphGeneration,
    int ViewportWidth,
    int ViewportHeight,
    int InternalWidth,
    int InternalHeight);
