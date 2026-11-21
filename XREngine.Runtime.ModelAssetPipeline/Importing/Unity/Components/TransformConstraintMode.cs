namespace XREngine.Components;

/// <summary>
/// Selects how <see cref="WeightedTransformConstraintComponent"/> turns its sources into a target pose.
/// </summary>
public enum TransformConstraintMode
{
    /// <summary>
    /// The target copies the weighted source position, rotation, and scale, then the constraint-level
    /// offsets are applied. Native and avatar SDK position, rotation, and scale constraints use this mode.
    /// </summary>
    Match,

    /// <summary>
    /// Each source acts as a parent of the target: the source pose is the source matrix multiplied by the
    /// per-source offset (<see cref="TransformConstraintSource.PositionOffset"/> in source space, then
    /// <see cref="TransformConstraintSource.RotationOffset"/>). The weighted result drives position and
    /// rotation. Native and avatar SDK parent constraints use this mode.
    /// </summary>
    Parent,

    /// <summary>
    /// The target rotates so that its aim axis points at the weighted source position, with its up axis
    /// turned toward the world-up direction. Native and avatar SDK aim and look-at constraints use this mode.
    /// </summary>
    Aim,
}
