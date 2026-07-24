using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IOpenGlVendorUpscaleBackendCapability
{
    ulong IOpenGlVendorUpscaleBackendCapability.FrameIndex
        => unchecked((ulong)Math.Max(0L, _frameCounter));

    bool IOpenGlVendorUpscaleBackendCapability.TryGenerateFrameBuffer(
        XRFrameBuffer frameBuffer,
        out string failureReason)
    {
        if (GenericToAPI<GLFrameBuffer>(frameBuffer) is not GLFrameBuffer glFrameBuffer)
        {
            failureReason = $"Failed to create the OpenGL framebuffer wrapper for '{frameBuffer.Name ?? "<unnamed>"}'.";
            return false;
        }

        glFrameBuffer.Generate();
        failureReason = string.Empty;
        return true;
    }

    void IOpenGlVendorUpscaleBackendCapability.BlitFrameBuffer(
        XRFrameBuffer source,
        XRFrameBuffer destination,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
        => BlitFBOToFBO(
            source,
            destination,
            readBuffer,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter);

    bool IOpenGlVendorUpscaleBackendCapability.TryResolveTextureBinding(
        XRTexture texture,
        out uint bindingId)
    {
        bindingId = GenericToAPI<GLTexture2D>(texture)?.BindingId ?? 0u;
        return bindingId != 0;
    }

    bool IOpenGlVendorUpscaleBackendCapability.TryGenerateTexture(
        XRTexture texture,
        out string failureReason)
    {
        if (GenericToAPI<GLTexture2D>(texture) is not GLTexture2D glTexture)
        {
            failureReason = $"Failed to create the OpenGL texture wrapper for '{texture.Name ?? "<unnamed>"}'.";
            return false;
        }

        glTexture.Generate();
        failureReason = string.Empty;
        return true;
    }

    uint IOpenGlVendorUpscaleBackendCapability.CreateImportedSemaphore(nint handle)
    {
        unsafe
        {
            return CreateImportedSemaphore((void*)handle);
        }
    }

    void IOpenGlVendorUpscaleBackendCapability.DeleteSemaphore(uint semaphore)
        => DeleteSemaphore(semaphore);

    void IOpenGlVendorUpscaleBackendCapability.SignalExternalTextureSemaphore(
        uint semaphore,
        ReadOnlySpan<uint> textureIds)
    {
        Span<Silk.NET.OpenGLES.TextureLayout> layouts =
            stackalloc Silk.NET.OpenGLES.TextureLayout[textureIds.Length];
        layouts.Fill(Silk.NET.OpenGLES.TextureLayout.GeneralExt);
        SignalExternalTextureSemaphore(semaphore, textureIds, layouts);
    }

    void IOpenGlVendorUpscaleBackendCapability.WaitExternalTextureSemaphore(
        uint semaphore,
        uint textureId)
        => WaitExternalTextureSemaphore(
            semaphore,
            textureId,
            Silk.NET.OpenGLES.TextureLayout.GeneralExt);
}
