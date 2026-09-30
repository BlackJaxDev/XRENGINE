using XREngine.Input;

namespace XREngine;

/// <summary>Submits a stereo texture pair to the active OpenVR compositor.</summary>
public interface IRuntimeOpenVrCompositor
{
    RuntimeOpenVrSubmitResult SubmitEyes(
        nint leftEyeHandle,
        nint rightEyeHandle,
        RuntimeOpenVrTextureType textureType,
        RuntimeOpenVrColorSpace colorSpace,
        RuntimeOpenVrSubmitFlags flags);
}
