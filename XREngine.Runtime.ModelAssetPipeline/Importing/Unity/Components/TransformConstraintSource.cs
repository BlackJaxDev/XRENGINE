using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// One weighted transform source for an imported Unity/VRChat constraint.
/// </summary>
[Serializable]
public sealed class TransformConstraintSource
{
    public TransformBase? SourceTransform { get; set; }

    /// <summary>
    /// The source's share of the result. A source with a weight of zero stays in the list, so that
    /// animation or code can raise its weight later.
    /// </summary>
    public float Weight { get; set; } = 1.0f;

    /// <summary>
    /// Position offset in the source's space (the source matrix moves it, including scale).
    /// Only <see cref="TransformConstraintMode.Parent"/> uses it.
    /// </summary>
    public Vector3 PositionOffset { get; set; }

    /// <summary>
    /// Rotation offset applied after the source rotation (source rotation * offset).
    /// Only <see cref="TransformConstraintMode.Parent"/> uses it.
    /// </summary>
    public Quaternion RotationOffset { get; set; } = Quaternion.Identity;
}
