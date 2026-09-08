using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Rules for variable-rate shading and radial foveation in the Advanced Render Pipeline.
/// </summary>
public static class AdvancedFoveationContract
{
    public const float DefaultInnerRadius = 0.25f;
    public const float DefaultMiddleRadius = 0.55f;
    public const float DefaultPeripheralRadius = 0.85f;

    /// <summary>Freezes validated per-view quality without consulting mutable XR state on the GPU path.</summary>
    public static Vector4 CaptureCenterAndBias(in ViewFoveationContext context)
    {
        if (!context.IsEnabled)
            return Vector4.Zero;
        Vector2 center = context.RenderTargetUvCenter;
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
            throw new ArgumentException("Advanced foveation requires a finite target-UV center.", nameof(context));
        _ = CaptureRadii(context);
        float maximumBias = context.QualityPreset switch
        {
            EVrFoveationQualityPreset.Conservative => 0.5f,
            EVrFoveationQualityPreset.Aggressive => 1.5f,
            _ => 1.0f,
        };
        return new Vector4(Vector2.Clamp(center, Vector2.Zero, Vector2.One), maximumBias, 1.0f);
    }

    /// <summary>Captures ordered radii; malformed enabled profiles fail admission visibly.</summary>
    public static Vector4 CaptureRadii(in ViewFoveationContext context)
    {
        if (!context.IsEnabled)
            return Vector4.Zero;
        VrFoveationRegionDefinition regions = context.Regions;
        if (!float.IsFinite(regions.InnerRadius) || !float.IsFinite(regions.GuardRadius) ||
            !float.IsFinite(regions.MidRadius) || !float.IsFinite(regions.OuterRadius) ||
            regions.InnerRadius < 0.0f || regions.GuardRadius < regions.InnerRadius ||
            regions.MidRadius <= regions.GuardRadius || regions.OuterRadius <= regions.MidRadius)
            throw new ArgumentException("Advanced foveation requires finite, ordered inner/guard/middle/outer radii.", nameof(context));
        return new Vector4(regions.InnerRadius, regions.GuardRadius, regions.MidRadius, regions.OuterRadius);
    }

    /// <summary>
    /// Computes a conservative derivative multiplier for peripheral shading to prevent texture undersampling.
    /// At higher eccentricity, derivative scaling must be clamped to avoid overly sharp MIP choices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CalculateConservativeLODBias(float normalizedEccentricity)
    {
        float ecc = Math.Clamp(normalizedEccentricity, 0.0f, 1.0f);
        // Conservative bias increases with eccentricity to pull towards higher (coarser) MIPs
        return MathF.Max(0.0f, (ecc - DefaultInnerRadius) * 1.5f);
    }
}
