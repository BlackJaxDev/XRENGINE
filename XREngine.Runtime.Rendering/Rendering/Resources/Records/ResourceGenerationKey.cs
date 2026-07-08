namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a key for generating resources in the render pipeline, including pipeline name, display and internal dimensions, HDR output, anti-aliasing settings, stereo rendering, feature mask, reserved view count, and reserved eye index.
/// </summary>
/// <param name="PipelineName">The name of the render pipeline.</param>
/// <param name="DisplayWidth">The width of the display.</param>
/// <param name="DisplayHeight">The height of the display.</param>
/// <param name="InternalWidth">The internal width used for rendering.</param>
/// <param name="InternalHeight">The internal height used for rendering.</param>
/// <param name="OutputHDR">Indicates whether HDR output is enabled.</param>
/// <param name="AntiAliasingMode">The anti-aliasing mode.</param>
/// <param name="MsaaSampleCount">The number of MSAA samples.</param>
/// <param name="Stereo">Indicates whether stereo rendering is enabled.</param>
/// <param name="FeatureMask">The feature mask for the render pipeline.</param>
/// <param name="ReservedViewCount">The number of reserved views.</param>
/// <param name="ReservedEyeIndex">The index of the reserved eye.</param>
public readonly record struct ResourceGenerationKey(
    string PipelineName,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    bool OutputHDR,
    EAntiAliasingMode AntiAliasingMode,
    uint MsaaSampleCount,
    bool Stereo,
    ulong FeatureMask = 0,
    uint ReservedViewCount = 1,
    uint ReservedEyeIndex = 0)
{
    /// <summary>
    /// Converts this resource generation key into a render pipeline resource profile that can be used to configure the render pipeline.
    /// </summary>
    /// <returns>A render pipeline resource profile representing the configuration of the render pipeline.</returns>
    public RenderPipelineResourceProfile ToProfile()
        => new(
            DisplayWidth,
            DisplayHeight,
            InternalWidth,
            InternalHeight,
            OutputHDR,
            AntiAliasingMode,
            Math.Max(1u, MsaaSampleCount),
            Stereo,
            FeatureMask);

    /// <summary>
    /// Returns a string representation of the resource generation key, including pipeline name, display and internal dimensions, HDR output, anti-aliasing settings, stereo rendering, feature mask, reserved view count, and reserved eye index.
    /// </summary>
    /// <returns>A string representation of the resource generation key.</returns>
    public override string ToString()
        => $"{PipelineName} display={DisplayWidth}x{DisplayHeight} internal={InternalWidth}x{InternalHeight} hdr={OutputHDR} aa={AntiAliasingMode} msaa={MsaaSampleCount} stereo={Stereo} features=0x{FeatureMask:X} views={ReservedViewCount} eye={ReservedEyeIndex}";
}
