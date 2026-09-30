using System.Numerics;

namespace XREngine.Input;

/// <summary>One atomic predicted tracking publication in runtime reference-space meters.</summary>
public readonly record struct RuntimeVrTrackingSnapshot(
    long SessionGeneration,
    long SnapshotId,
    long SampleTime,
    Matrix4x4 HeadPose,
    bool HeadValid,
    Matrix4x4 LeftControllerPose,
    bool LeftControllerValid,
    Matrix4x4 RightControllerPose,
    bool RightControllerValid)
{
    public long ReferenceSpaceVersion { get; init; }
}
