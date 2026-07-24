using ImageMagick;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRenderCaptureBackendCapability
{
    bool IRenderCaptureBackendCapability.TryCaptureTexture(
        XRTexture texture,
        BoundingRectangle region,
        Action<MagickImage, int, int> callback,
        int mipLevel,
        int layerIndex)
    {
        if (texture.APIWrappers.FirstOrDefault(static wrapper => wrapper is IGLTexture) is not IGLTexture glTexture)
            return false;

        CaptureTexture(
            region,
            (image, layer, channelIndex) => callback(image, layer, channelIndex),
            glTexture.BindingId,
            mipLevel,
            layerIndex);
        return true;
    }

    bool IRenderCaptureBackendCapability.TryCaptureFrameBufferAttachment(
        XRFrameBuffer frameBuffer,
        BoundingRectangle region,
        bool flipY,
        Action<MagickImage, int> callback,
        EFrameBufferAttachment attachment)
    {
        if (frameBuffer.APIWrappers.FirstOrDefault(static wrapper => wrapper is GLFrameBuffer) is not GLFrameBuffer glFrameBuffer)
            return false;

        CaptureFBOAttachment(region, flipY, callback, glFrameBuffer.BindingId, attachment);
        return true;
    }

    bool IRenderCaptureBackendCapability.TryCaptureTextureBytes(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out byte[] data,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out uint width,
        out uint height)
    {
        if (texture.APIWrappers.FirstOrDefault(static wrapper => wrapper is IGLTexture) is not IGLTexture glTexture)
        {
            data = [];
            pixelFormat = default;
            pixelType = default;
            width = 0;
            height = 0;
            return false;
        }

        return TryCaptureTextureBytes(
            glTexture.BindingId,
            mipLevel,
            layerIndex,
            out data,
            out pixelFormat,
            out pixelType,
            out width,
            out height);
    }

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
