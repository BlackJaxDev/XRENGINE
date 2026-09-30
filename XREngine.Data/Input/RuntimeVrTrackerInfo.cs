using System.Numerics;

namespace XREngine.Input;

/// <summary>A physical tracker and its current predicted sample. Role paths are transport diagnostics only.</summary>
public readonly record struct RuntimeVrTrackerInfo(
    string UserPath,
    string? PersistentPath,
    string? RolePath,
    string? RoleName,
    bool PoseAvailable,
    bool RuntimeReported)
{
    public long SessionGeneration { get; init; }
    public long SnapshotId { get; init; }
    /// <summary>OpenXR display time in nanoseconds, not wall-clock time.</summary>
    public long SampleTime { get; init; }
    public bool Connected { get; init; }
    public bool ActionActive { get; init; }
    public bool PositionValid { get; init; }
    public bool OrientationValid { get; init; }
    public bool EverTracked { get; init; }
    public long LastValidSampleTime { get; init; }
    public Matrix4x4 LastValidPose { get; init; }
    public bool RequiresInputRebuild { get; init; }
    public bool Bound { get; init; }
    public bool IsStale { get; init; }
    public bool PoseCurrentlyUsable => Connected && Bound && ActionActive && PositionValid && OrientationValid && PoseAvailable && !IsStale && !RequiresInputRebuild;
    /// <summary>OpenXR cannot distinguish a powered-off tracker from one hidden/disabled by the runtime.</summary>
    public string DiagnosticState => !RuntimeReported ? "Not discovered" :
        !Connected ? "Not enumerated (disconnected or runtime-hidden; disabled state unknown)" :
        RequiresInputRebuild ? "Discovered after input attachment; restart VR to include this tracker" :
        !Bound ? "Discovered but unbound" : IsStale ? "Stale" :
        !ActionActive ? "Bound but inactive" : !PoseCurrentlyUsable ? "Tracking lost" : "Tracking";
}
