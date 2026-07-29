using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Complete render-pose scheduling result and its diagnostic state.
/// Gameplay animation remains an explicit, independent decision.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedAnimationScheduleDecision(
    bool UpdateRenderPose,
    bool GameplayCpuAnimationRequired,
    uint CadenceFrames,
    uint BoneTier,
    uint StalePoseAge,
    float AccumulatedDeltaSeconds,
    EAdvancedAnimationSkipReason SkipReason);
