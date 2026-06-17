namespace XREngine.Rendering.Resources;

public readonly record struct RenderPipelineResourceProfile(
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    bool OutputHDR,
    EAntiAliasingMode AntiAliasingMode,
    uint MsaaSampleCount,
    bool Stereo,
    bool UseVulkanSafeFeatureProfile,
    ulong FeatureMask = 0)
{
    public static RenderPipelineResourceProfile Empty { get; } = new(
        1u,
        1u,
        1u,
        1u,
        OutputHDR: false,
        EAntiAliasingMode.None,
        1u,
        Stereo: false,
        UseVulkanSafeFeatureProfile: false);
}
