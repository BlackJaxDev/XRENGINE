using ImageMagick;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Captures backend render resources for tooling without exposing a concrete renderer.
/// </summary>
public interface IRenderCaptureBackendCapability
{
    void CaptureTexture(
        BoundingRectangle region,
        Action<MagickImage, int, int> callback,
        uint bindingId,
        int mipLevel,
        int layerIndex);

    void CaptureFrameBufferAttachment(
        BoundingRectangle region,
        bool flipY,
        Action<MagickImage, int> callback,
        uint frameBufferBindingId,
        EFrameBufferAttachment attachment);

    bool TryCaptureTextureBytes(
        uint textureBindingId,
        int mipLevel,
        int layerIndex,
        out byte[] data,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out uint width,
        out uint height);
}
