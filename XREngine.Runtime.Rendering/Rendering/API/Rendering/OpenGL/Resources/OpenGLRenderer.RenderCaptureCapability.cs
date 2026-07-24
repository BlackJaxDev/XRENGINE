using ImageMagick;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRenderCaptureBackendCapability
{
    void IRenderCaptureBackendCapability.CaptureTexture(
        BoundingRectangle region,
        Action<MagickImage, int, int> callback,
        uint bindingId,
        int mipLevel,
        int layerIndex)
        => CaptureTexture(
            region,
            (image, layer, channelIndex) => callback(image, layer, channelIndex),
            bindingId,
            mipLevel,
            layerIndex);

    void IRenderCaptureBackendCapability.CaptureFrameBufferAttachment(
        BoundingRectangle region,
        bool flipY,
        Action<MagickImage, int> callback,
        uint frameBufferBindingId,
        EFrameBufferAttachment attachment)
        => CaptureFBOAttachment(region, flipY, callback, frameBufferBindingId, attachment);

    bool IRenderCaptureBackendCapability.TryCaptureTextureBytes(
        uint textureBindingId,
        int mipLevel,
        int layerIndex,
        out byte[] data,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out uint width,
        out uint height)
        => TryCaptureTextureBytes(
            textureBindingId,
            mipLevel,
            layerIndex,
            out data,
            out pixelFormat,
            out pixelType,
            out width,
            out height);
}
