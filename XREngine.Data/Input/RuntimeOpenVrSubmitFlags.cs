namespace XREngine.Input;

/// <summary>OpenVR compositor submission flags with their native bit values.</summary>
[Flags]
public enum RuntimeOpenVrSubmitFlags
{
    Default = 0,
    LensDistortionAlreadyApplied = 1,
    GlRenderBuffer = 2,
    Reserved = 4,
    TextureWithPose = 8,
    TextureWithDepth = 16,
    FrameDiscontinuity = 32,
    VulkanTextureWithArrayData = 64,
    GlArrayTexture = 128,
    Reserved2 = 32768,
}
