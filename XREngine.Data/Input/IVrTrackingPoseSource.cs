namespace XREngine.Input;

/// <summary>Current tracking validity, separate from the transform's retained display pose.</summary>
public interface IVrTrackingPoseSource
{
    bool PoseCurrentlyUsable { get; }
    string? TrackingIdentity { get; }
    long TrackingSessionGeneration { get; }
    long TrackingSnapshotId { get; }
}
