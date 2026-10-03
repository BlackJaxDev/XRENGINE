using Valve.VR;
using XREngine.Input;

namespace XREngine;

/// <summary>Owns OpenVR compositor texture state and the single post-present handoff.</summary>
internal sealed class OpenVrCompositorBackend : IRuntimeOpenVrCompositor
{
    public static OpenVrCompositorBackend Instance { get; } = new();

    private Texture_t _eyeTexture = new()
    {
        eColorSpace = EColorSpace.Auto,
        eType = ETextureType.OpenGL,
    };

    private VRTextureBounds_t _textureBounds = new()
    {
        uMin = 0f,
        vMin = 0f,
        uMax = 1f,
        vMax = 1f,
    };

    public RuntimeOpenVrSubmitResult SubmitEyes(
        nint leftEyeHandle,
        nint rightEyeHandle,
        RuntimeOpenVrTextureType textureType,
        RuntimeOpenVrColorSpace colorSpace,
        RuntimeOpenVrSubmitFlags flags)
    {
        CVRCompositor compositor = Valve.VR.OpenVR.Compositor
            ?? throw new InvalidOperationException("The OpenVR compositor is unavailable.");

        Texture_t texture = _eyeTexture;
        VRTextureBounds_t bounds = _textureBounds;
        texture.eType = (ETextureType)textureType;
        texture.eColorSpace = (EColorSpace)colorSpace;
        texture.handle = leftEyeHandle;
        EVRCompositorError left = compositor.Submit(EVREye.Eye_Left, ref texture, ref bounds, (EVRSubmitFlags)flags);
        texture.handle = rightEyeHandle;
        EVRCompositorError right = compositor.Submit(EVREye.Eye_Right, ref texture, ref bounds, (EVRSubmitFlags)flags);
        _eyeTexture = texture;
        _textureBounds = bounds;
        compositor.PostPresentHandoff();
        return new RuntimeOpenVrSubmitResult((int)left, (int)right);
    }
}
