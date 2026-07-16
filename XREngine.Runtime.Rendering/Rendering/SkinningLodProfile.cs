using System.Collections.Generic;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

public sealed class SkinningLodProfile(params SkinningLodTier[] tiers)
{
    private readonly SkinningLodTier[] _tiers = tiers ?? [];

    public bool TryGetTier(int tierIndex, out SkinningLodTier tier)
    {
        if ((uint)tierIndex < (uint)_tiers.Length)
        {
            tier = _tiers[tierIndex];
            return true;
        }

        tier = default;
        return false;
    }
}

public readonly record struct SkinningLodTier(
    int InfluenceCap,
    BoneRemap? BoneRemap = null,
    bool AllowRigidFallback = false);

public sealed class BoneRemap(IReadOnlyList<TransformBase> protectedBones, IReadOnlyList<int> paletteRemap)
{
    public IReadOnlyList<TransformBase> ProtectedBones { get; } = protectedBones;
    public IReadOnlyList<int> PaletteRemap { get; } = paletteRemap;
}
