namespace XREngine.Rendering.Resources;

public readonly record struct RenderResourceMipPolicy(
    uint BaseMipLevel = 0,
    uint MipLevelCount = 1,
    bool AutoGenerateMipmaps = false,
    bool RequireImmutableStorage = false);
