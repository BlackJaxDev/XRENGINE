using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Authored render-pose palette tier. A scheduler may only select the tier
/// when it contains every runtime-required output.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedBoneLodTier(
    uint BoneCount,
    EAdvancedAnimationBoneRequirement PreservedRequirements);
