using System.Numerics;

namespace XREngine.Input;

/// <summary>A copied tracker sample from the same publication as the headset and controllers.</summary>
public readonly record struct RuntimeVrTrackerPose(RuntimeVrTrackerInfo Info, Matrix4x4 LocalPose);
