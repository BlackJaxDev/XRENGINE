namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private EAdvancedStereoMode _stereoMode = EAdvancedStereoMode.Mono;
    private AdvancedOffscreenProfile? _offscreenProfile;

    /// <summary>
    /// Active stereo rendering mode (Mono, RvcTwoPass, OpenGlSinglePassStereo, or VulkanMultiview).
    /// </summary>
    public EAdvancedStereoMode StereoMode
    {
        get => _stereoMode;
        set
        {
            if (!SetField(ref _stereoMode, value))
                return;
            InvalidateStereoResourceProfile();
        }
    }

    /// <summary>
    /// Optional offscreen capability profile if this pipeline instance drives a secondary view.
    /// </summary>
    public AdvancedOffscreenProfile? OffscreenProfile
    {
        get => _offscreenProfile;
        set
        {
            if (!SetField(ref _offscreenProfile, value))
                return;
            InvalidateStereoResourceProfile();
        }
    }

    private void InvalidateStereoResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("StereoProfileChanged");
}
