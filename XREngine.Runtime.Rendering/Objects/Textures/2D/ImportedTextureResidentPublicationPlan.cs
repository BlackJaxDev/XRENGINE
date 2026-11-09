using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Holds decoded texture metadata until the backend accepts the resident image.
/// </summary>
internal readonly record struct ImportedTextureResidentPublicationPlan(
    TextureStreamingResidentData ResidentData,
    Mipmap2D[] PreviousMipmaps,
    uint PreviousWidth,
    uint PreviousHeight,
    ulong SourceGeneration,
    ETextureColorSpace ColorSpace,
    ImportedTextureResidentAuthoredSettings AuthoredSettings,
    ESizedInternalFormat SizedFormat,
    int LockMipLevel,
    int SmallestAllowedMipmapLevel);
