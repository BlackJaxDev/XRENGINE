namespace XREngine.Components;

/// <summary>
/// Selects the world-up direction that an aim constraint turns the target's up axis toward.
/// </summary>
public enum TransformConstraintWorldUpType
{
    /// <summary>The scene up direction (+Y).</summary>
    SceneUp,

    /// <summary>The direction from the target to the world-up transform.</summary>
    ObjectUp,

    /// <summary>The world-up vector, rotated by the rotation of the world-up transform.</summary>
    ObjectRotationUp,

    /// <summary>The world-up vector, as given.</summary>
    Vector,

    /// <summary>
    /// No up direction. The target takes the shortest rotation that turns the aim axis toward the source.
    /// </summary>
    None,
}
