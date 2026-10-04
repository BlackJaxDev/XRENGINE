using XREngine.Data.Core;

namespace XREngine.Rendering;

/// <summary>
/// Browser-target reflection carrier for a canonical ordinary unlit material.
/// Its inherited graph keeps authored parameter and image references intact;
/// the versioned profile restores texture metadata omitted by raw image storage.
/// </summary>
[CookedBinaryReflectionOnly]
public sealed class PublishedUnlitMaterial : XRMaterial
{
    public UnlitPublishedTextureProfile? PublishedUnlitTextureProfile { get; set; }
}
