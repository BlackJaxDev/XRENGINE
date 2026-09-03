namespace XREngine.Rendering;

/// <summary>
/// Stereo rendering execution modes supported by the Advanced Render Pipeline.
/// </summary>
public enum EAdvancedStereoMode : uint
{
    Mono = 0u,
    RvcTwoPass = 1u,
    OpenGlSinglePassStereo = 2u,
    VulkanMultiview = 3u,
}
