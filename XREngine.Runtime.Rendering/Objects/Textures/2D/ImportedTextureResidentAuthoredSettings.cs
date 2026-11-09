using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Captures settings that can change the imported image or its sampler.
/// </summary>
internal readonly record struct ImportedTextureResidentAuthoredSettings(
    ETexMinFilter MinFilter,
    ETexMagFilter MagFilter,
    ETexWrapMode UWrap,
    ETexWrapMode VWrap,
    float LodBias,
    float MaxAnisotropy,
    int MinLOD,
    int MaxLOD,
    bool EnableComparison,
    ETextureCompareFunc CompareFunc,
    bool RequiresStorageUsage,
    uint MultiSampleCount,
    ETextureImportUsage ImportedUsage,
    bool ImportedNormalMapFlipGreen,
    ESizedInternalFormat PreviousSizedInternalFormat,
    bool PreviousResizable,
    bool PreviousAutoGenerateMipmaps,
    int PreviousLargestMipmapLevel,
    int PreviousSmallestAllowedMipmapLevel,
    bool Rectangle);
