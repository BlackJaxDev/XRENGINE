using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>Bind-pose signature checked within one avatar instance's session cache.</summary>
internal readonly record struct VrAvatarCalibrationKey(
    Guid PrefabAssetId, string? Name, Matrix4x4 Head, Matrix4x4 Hips,
    Matrix4x4 LeftHand, Matrix4x4 RightHand, Matrix4x4 LeftFoot, Matrix4x4 RightFoot);
