using System;
using MemoryPack;

namespace XREngine.Animation;

/// <summary>
/// Serializable normalized time window inside a <see cref="MotionWarpingMotion"/> during which
/// root translation and yaw deltas are warped to align the character with a runtime
/// <see cref="MotionWarpTarget"/>.
/// </summary>
[MemoryPackable]
public sealed partial class MotionWarpWindow
{
    public string TargetName { get; set; } = "Default";

    /// <summary>Normalized start time [0..1] in the source clip where warping begins.</summary>
    public float StartNormalizedTime { get; set; } = 0.1f;

    /// <summary>Normalized end time [0..1] in the source clip where the target pose is reached.</summary>
    public float EndNormalizedTime { get; set; } = 0.85f;

    /// <summary>Warps horizontal (X, Z) root translation toward the target.</summary>
    public bool WarpTranslationXZ { get; set; } = true;

    /// <summary>Warps vertical (Y) root translation toward the target.</summary>
    public bool WarpTranslationY { get; set; } = true;

    /// <summary>Warps root facing yaw toward the target rotation.</summary>
    public bool WarpYaw { get; set; } = true;

    /// <summary>Blend strength [0..1] of the warp correction inside this window.</summary>
    public float WarpWeight { get; set; } = 1.0f;

    public float ClampedStart => float.IsFinite(StartNormalizedTime) ? Math.Clamp(StartNormalizedTime, 0.0f, 0.99f) : 0.0f;

    public float ClampedEnd => float.IsFinite(EndNormalizedTime) ? Math.Clamp(EndNormalizedTime, ClampedStart + 0.01f, 1.0f) : 1.0f;

    public float ClampedWeight => float.IsFinite(WarpWeight) ? Math.Clamp(WarpWeight, 0.0f, 1.0f) : 1.0f;
}
