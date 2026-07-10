using System.Numerics;

namespace XREngine;

/// <summary>
/// Validates whether a shadelet can be reused based on various criteria such as normal angle, depth difference, and roughness bucket delta.
/// </summary>
public static class RvcShadeletReuseValidator
{
    /// <summary>
    /// Determines whether a shadelet can be reused based on the specified criteria.
    /// </summary>
    /// <param name="source">The source shadelet reuse candidate.</param>
    /// <param name="target">The target shadelet reuse candidate.</param>
    /// <param name="stereoReuse">Indicates whether stereo reuse is enabled.</param>
    /// <param name="maxNormalAngleDegrees">The maximum allowed normal angle difference in degrees.</param>
    /// <param name="maxDepthDeltaMeters">The maximum allowed depth difference in meters.</param>
    /// <param name="maxRoughnessBucketDelta">The maximum allowed roughness bucket difference.</param>
    /// <param name="rejectionReason">The reason for rejection if the shadelet cannot be reused.</param>
    /// <returns>True if the shadelet can be reused; otherwise, false.</returns>
    public static bool CanReuse(
        in RvcShadeletReuseCandidate source,
        in RvcShadeletReuseCandidate target,
        bool stereoReuse,
        float maxNormalAngleDegrees,
        float maxDepthDeltaMeters,
        byte maxRoughnessBucketDelta,
        out ERvcFallbackReason rejectionReason)
    {
        // Early out if stereo reuse is not enabled.
        if (!stereoReuse)
        {
            rejectionReason = ERvcFallbackReason.StereoReuseDisabledUntilValidated;
            return false;
        }

        // Early out if the keys or material/deformation/lod information do not match.
        if (source.Key != target.Key ||
            source.MaterialResourceGeneration != target.MaterialResourceGeneration ||
            source.DeformationVersion != target.DeformationVersion ||
            source.LodBucket != target.LodBucket)
        {
            rejectionReason = ERvcFallbackReason.UnsupportedMaterialClass;
            return false;
        }

        // Early out if either shadelet is disoccluded or has a view-dependent material.
        if (source.Disoccluded || target.Disoccluded || source.ViewDependentMaterial || target.ViewDependentMaterial)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        // Early out if the normal angle difference or depth difference exceeds the specified thresholds.
        float normalDot = Vector3.Dot(Vector3.Normalize(source.Normal), Vector3.Normalize(target.Normal));
        normalDot = Math.Clamp(normalDot, -1.0f, 1.0f);
        float angle = MathF.Acos(normalDot) * 180.0f / MathF.PI;
        if (angle > maxNormalAngleDegrees || MathF.Abs(source.DepthMeters - target.DepthMeters) > maxDepthDeltaMeters)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        // Early out if the roughness bucket difference exceeds the specified threshold.
        int roughnessDelta = Math.Abs(source.RoughnessBucket - target.RoughnessBucket);
        if (roughnessDelta > maxRoughnessBucketDelta)
        {
            rejectionReason = ERvcFallbackReason.ValidationHarnessFailed;
            return false;
        }

        // If none of the early out conditions were met, the shadelet can be reused.
        rejectionReason = ERvcFallbackReason.None;
        return true;
    }
}
