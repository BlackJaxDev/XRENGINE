using System;
using System.Collections.Generic;

namespace XREngine.Rendering;

public enum BlendshapeLodEvaluation
{
    Full = 0,
    ProtectedAndHighImpact = 1,
    VisemeOrSilhouette = 2,
    Disabled = 3,
}

public enum BlendshapeLodAvatarRole
{
    Primary = 0,
    Secondary = 1,
    Crowd = 2,
}

public sealed class BlendshapeLodProfile
{
    private readonly BlendshapeLodTier[] _tiers;

    public BlendshapeLodProfile(params BlendshapeLodTier[] tiers)
        => _tiers = tiers ?? [];

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

public readonly record struct BlendshapeLodTier(
    BlendshapeLodEvaluation Evaluation,
    IReadOnlyList<int>? ShapeIndices = null,
    IReadOnlyList<string>? ProtectedShapeNames = null,
    float MaxDistance = 0.0f,
    float MinScreenCoverage = 0.0f);
