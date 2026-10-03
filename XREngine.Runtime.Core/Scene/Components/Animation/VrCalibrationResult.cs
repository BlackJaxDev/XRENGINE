using System.Collections.Generic;

namespace XREngine.Components.Animation;

/// <summary>Outcome of a rig capture, including a player-facing failure reason.</summary>
public readonly record struct VrCalibrationResult(bool Success, string? Error = null, IReadOnlyList<VrCalibrationSlotState>? Slots = null)
{
    public static VrCalibrationResult Failed(string error) => new(false, error);
    public static VrCalibrationResult Completed => new(true);
}

