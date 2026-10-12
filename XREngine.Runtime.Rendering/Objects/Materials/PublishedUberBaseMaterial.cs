using XREngine.Data.Core;

namespace XREngine.Rendering;

/// <summary>
/// Browser-target reflection carrier preserving the complete inherited source graph.
/// The ordinary authored XRMaterial persistence schema has no Uber target metadata.
/// </summary>
[CookedBinaryReflectionOnly]
public sealed class PublishedUberBaseMaterial : XRMaterial
{
    public UberBaseMaterialProfile? PublishedUberBaseProfile
    {
        get => CookedUberBaseProfile;
        set => CookedUberBaseProfile = value;
    }
}
