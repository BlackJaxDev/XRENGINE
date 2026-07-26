namespace XREngine.Rendering;

/// <summary>
/// Isolates the temporary OpenGL-to-Vulkan vendor-upscale bridge from stable pipeline code.
/// The bridge-specific parameter types move with the backend extraction in P4.8.
/// </summary>
internal interface IOpenGlVendorUpscaleBackendCapability
{
    ulong FrameIndex { get; }

    bool TryGenerateFrameBuffer(XRFrameBuffer frameBuffer, out string failureReason);

    void BlitFrameBuffer(
        XRFrameBuffer source,
        XRFrameBuffer destination,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);

    bool TryResolveTextureBinding(XRTexture texture, out uint bindingId);
    bool TryGenerateTexture(XRTexture texture, out string failureReason);
    uint CreateImportedSemaphore(nint handle);
    void DeleteSemaphore(uint semaphore);
    void SignalExternalTextureSemaphore(uint semaphore, ReadOnlySpan<uint> textureIds);
    void WaitExternalTextureSemaphore(uint semaphore, uint textureId);
}
