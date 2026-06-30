namespace XREngine.Rendering.Resources;

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

    public override string ToString()
        => $"{PipelineName} display={DisplayWidth}x{DisplayHeight} internal={InternalWidth}x{InternalHeight} hdr={OutputHDR} aa={AntiAliasingMode} msaa={MsaaSampleCount} stereo={Stereo} features=0x{FeatureMask:X} views={ReservedViewCount} eye={ReservedEyeIndex}";
}
