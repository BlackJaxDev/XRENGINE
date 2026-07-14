namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a profile of the render pipeline resource configuration, including display and internal dimensions, HDR output, anti-aliasing settings, stereo rendering, and feature mask.
/// </summary>
/// <param name="DisplayWidth">The width of the display.</param>
/// <param name="DisplayHeight">The height of the display.</param>
/// <param name="InternalWidth">The internal width used for rendering.</param>
/// <param name="InternalHeight">The internal height used for rendering.</param>
/// <param name="OutputHDR">Indicates whether HDR output is enabled.</param>
/// <param name="AntiAliasingMode">The anti-aliasing mode.</param>
/// <param name="MsaaSampleCount">The number of MSAA samples.</param>
/// <param name="Stereo">Indicates whether stereo rendering is enabled.</param>
/// <param name="FeatureMask">The feature mask for the render pipeline.</param>
/// <param name="ExternalTargetKind">The externally owned output class imported by the pipeline.</param>
/// <param name="ViewCount">The number of views represented by the profile.</param>
/// <param name="ViewIndex">The selected view index for per-view profiles.</param>
public readonly record struct RenderPipelineResourceProfile(
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    bool OutputHDR,
    EAntiAliasingMode AntiAliasingMode,
    uint MsaaSampleCount,
    bool Stereo,
    ulong FeatureMask = 0,
    RenderPipelineExternalTargetKind ExternalTargetKind = RenderPipelineExternalTargetKind.None,
    uint ViewCount = 1,
    uint ViewIndex = 0)
{
    /// <summary>
    /// Gets an empty render pipeline resource profile with default values for all properties.
    /// </summary>
    public static RenderPipelineResourceProfile Empty { get; } = new(
        1u,
        1u,
        1u,
        1u,
        OutputHDR: false,
        EAntiAliasingMode.None,
        1u,
        Stereo: false,
        ExternalTargetKind: RenderPipelineExternalTargetKind.None,
        ViewCount: 1u,
        ViewIndex: 0u);
}
