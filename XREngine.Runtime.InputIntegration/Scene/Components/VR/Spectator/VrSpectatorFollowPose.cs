using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>A yaw-only collision-boom origin and independently resolved look-at target.</summary>
public readonly record struct VrSpectatorFollowPose(Matrix4x4 BoomOrigin, Vector3 AimPoint, Vector3 UnobstructedPosition);
