using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Operational contract and blending functions for advanced ambient occlusion.
/// </summary>
public static class AdvancedAmbientOcclusionContract
{
    public const string ResourceName = "AdvancedShading.AmbientOcclusion";

    /// <summary>
    /// Applies Jimenez multi-bounce ambient occlusion approximation to avoid over-darkening colored surfaces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (float R, float G, float B) EvaluateMultiBounce(float ao, float albedoR, float albedoG, float albedoB)
    {
        // Jimenez 2016 approximation: a = 2.0404 * albedo - 0.3324; b = -4.7951 * albedo + 0.6417; c = 2.7552 * albedo + 0.6903
        // V = ao / ((1 - albedo) * ao + albedo)
        float r = EvaluateChannelMultiBounce(ao, albedoR);
        float g = EvaluateChannelMultiBounce(ao, albedoG);
        float b = EvaluateChannelMultiBounce(ao, albedoB);
        return (r, g, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EvaluateChannelMultiBounce(float ao, float albedo)
    {
        float a = 2.0404f * albedo - 0.3324f;
        float b = -4.7951f * albedo + 0.6417f;
        float c = 2.7552f * albedo + 0.6903f;
        return MathF.Max(0.0f, ao * (ao * (ao * a + b) + c));
    }
}
