namespace XREngine.Rendering.Occlusion;

/// <summary>Why an offline calibration did or did not select a Hi-Z variant.</summary>
public enum EGpuHiZSelectorDecisionReason
{
    Uncalibrated,
    NoMeasuredWin,
    InsufficientConfidence,
    AmbiguousMeasuredWins,
    Selected,
}
