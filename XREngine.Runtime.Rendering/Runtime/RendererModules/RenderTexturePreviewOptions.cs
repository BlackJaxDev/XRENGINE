namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral options used when preparing a texture for tooling UI.
/// </summary>
public readonly record struct RenderTexturePreviewOptions(
    RenderTexturePreviewChannel Channel = RenderTexturePreviewChannel.Rgba,
    bool ApplySingleChannelSwizzle = false,
    bool ForceBaseMipSampling = false,
    bool UploadIfNeeded = false);
