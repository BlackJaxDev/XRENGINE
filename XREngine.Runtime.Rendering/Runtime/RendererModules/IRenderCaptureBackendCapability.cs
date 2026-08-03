using ImageMagick;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Captures backend render resources for tooling without exposing a concrete renderer.
/// Callback ownership of each <see cref="MagickImage"/> transfers to the callback; callback implementations must dispose it.
/// </summary>
public interface IRenderCaptureBackendCapability
{
    bool TryCaptureTexture(
        XRTexture texture,
        BoundingRectangle region,
        Action<MagickImage, int, int> callback,
        int mipLevel,
        int layerIndex);

    bool TryCaptureFrameBufferAttachment(
        XRFrameBuffer frameBuffer,
        BoundingRectangle region,
        bool flipY,
        Action<MagickImage, int> callback,
        EFrameBufferAttachment attachment);

    bool TryCaptureTextureBytes(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out byte[] data,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out uint width,
        out uint height);

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
