using System.Numerics;

namespace XREngine.Input;

/// <summary>Session-local notification emitted after a coherent pose-basis change.</summary>
public readonly record struct VrPoseDiscontinuity(long Version, EVrPoseDiscontinuity Kind, Matrix4x4? KnownBasisChange);
