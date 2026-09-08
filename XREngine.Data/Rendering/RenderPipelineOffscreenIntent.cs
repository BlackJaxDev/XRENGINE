namespace XREngine.Data.Rendering;

/// <summary>
/// Identifies the owner semantics of an explicit advanced offscreen request.
/// </summary>
public enum ERenderPipelineOffscreenViewIntent : uint
{
    SceneCapture = 0u,
    Mirror = 1u,
    Portal = 2u,
    ReflectionProbe = 3u,
    Thumbnail = 4u,
    DepthOnly = 5u,
    VisibilityOnly = 6u,
}

/// <summary>
/// Selects the unprocessed advanced resource exported to an offscreen owner.
/// </summary>
public enum ERenderPipelineOffscreenOutput : uint
{
    HdrColor = 0u,
    Depth = 1u,
    Visibility = 2u,
}

/// <summary>
/// Immutable intent for an offscreen request that explicitly selects the advanced pipeline.
/// The caller must provide an output target compatible with <see cref="Output"/>.
/// </summary>
public readonly record struct RenderPipelineOffscreenIntent(
    ERenderPipelineOffscreenViewIntent ViewIntent,
    ERenderPipelineOffscreenOutput Output,
    bool EnableLateTransparency = false,
    bool EnablePostProcessing = false,
    bool EnableTemporalHistory = false,
    bool EnableBloomAndDoF = false)
{
    public static RenderPipelineOffscreenIntent Thumbnail()
        => new(ERenderPipelineOffscreenViewIntent.Thumbnail, ERenderPipelineOffscreenOutput.HdrColor);

    public static RenderPipelineOffscreenIntent ReflectionProbe()
        => new(
            ERenderPipelineOffscreenViewIntent.ReflectionProbe,
            ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true);

    public static RenderPipelineOffscreenIntent Depth()
        => new(ERenderPipelineOffscreenViewIntent.DepthOnly, ERenderPipelineOffscreenOutput.Depth);

    public static RenderPipelineOffscreenIntent Visibility()
        => new(ERenderPipelineOffscreenViewIntent.VisibilityOnly, ERenderPipelineOffscreenOutput.Visibility);
}
