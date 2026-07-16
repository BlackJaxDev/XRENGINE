using System;
using System.Collections.Generic;

namespace XREngine.Rendering;

public sealed class BlendshapeLodProfile(params BlendshapeLodTier[] tiers)
{
    private readonly BlendshapeLodTier[] _tiers = tiers ?? [];

    public bool TryGetTier(int tierIndex, out BlendshapeLodTier tier)
    {
        if ((uint)tierIndex < (uint)_tiers.Length)
        {
            tier = _tiers[tierIndex];
            return true;
        }

        tier = default;
        return false;
    }

    public int SelectTier(float distance, float screenCoverage, BlendshapeLodAvatarRole role = BlendshapeLodAvatarRole.Primary)
    {
        if (_tiers.Length == 0)
            return 0;

        int firstTier = role switch
        {
            BlendshapeLodAvatarRole.Crowd => Math.Min(3, _tiers.Length - 1),
            BlendshapeLodAvatarRole.Secondary => Math.Min(1, _tiers.Length - 1),
            _ => 0,
        };

        float normalizedDistance = Math.Max(0.0f, distance);
        float normalizedCoverage = Math.Clamp(screenCoverage, 0.0f, 1.0f);
        for (int i = firstTier; i < _tiers.Length; i++)
        {
            BlendshapeLodTier tier = _tiers[i];
            bool distanceAllowed = tier.MaxDistance <= 0.0f || normalizedDistance <= tier.MaxDistance;
            bool coverageAllowed = tier.MinScreenCoverage <= 0.0f || normalizedCoverage >= tier.MinScreenCoverage;
            if (distanceAllowed && coverageAllowed)
                return i;
        }

        return _tiers.Length - 1;
    }
}
