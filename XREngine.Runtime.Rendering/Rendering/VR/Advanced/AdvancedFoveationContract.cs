using System;
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
