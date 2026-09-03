using System;

namespace XREngine.Rendering;

/// <summary>
/// Operational rules and invariants for stereo and multiview rendering in the Advanced Render Pipeline.
/// </summary>
public static class AdvancedStereoContract
{
    public const uint LeftEyeIndex = 0u;
    public const uint RightEyeIndex = 1u;

    /// <summary>
    /// Resolves the required number of texture array layers for a given stereo mode.
    /// </summary>
    public static uint ResolveLayerCount(EAdvancedStereoMode mode, uint viewCount = 2u)
    {
        return mode switch
        {
            EAdvancedStereoMode.Mono => 1u,
            _ => Math.Max(2u, viewCount)
        };
    }

    /// <summary>
    /// Invariant: An occlusion or visibility culling verdict computed for one eye view
    /// must never be reused for another eye view, to prevent asymmetrical VR popping.
    /// </summary>
    public static bool CanShareOcclusionVerdict(uint eyeA, uint eyeB)
        => eyeA == eyeB;
}
