using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// One weighted transform source for an imported Unity/VRChat constraint.
/// </summary>
[Serializable]
public sealed class UnityTransformConstraintSource
{
    public TransformBase? SourceTransform { get; set; }
    public float Weight { get; set; } = 1.0f;
    public Vector3 PositionOffset { get; set; }
    public Quaternion RotationOffset { get; set; } = Quaternion.Identity;
    public Vector3 ScaleOffset { get; set; }
}
