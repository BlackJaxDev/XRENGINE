namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Defines the kind of a post process parameter.
/// </summary>
public enum PostProcessParameterKind
{
    /// <summary>
    /// The parameter is a floating point value.
    /// Generally used for parameters that are continuous and can take on a wide range of values, such as intensity or opacity.
    /// </summary>
    Float,
    /// <summary>
    /// The parameter is an integer value.
    /// Generally used for parameters that are discrete and can only take on specific values, such as the number of iterations or the size of a kernel.
    /// </summary>
    Int,
    /// <summary>
    /// The parameter is a boolean value.
    /// Generally used for parameters that are binary and can only be true or false, such as whether a feature is enabled or disabled.
    /// </summary>
    Bool,
    /// <summary>
    /// The parameter is a two-dimensional vector.
    /// Generally used for parameters that represent 2D coordinates or directions.
    /// </summary>
    Vector2,
    /// <summary>
    /// The parameter is a three-dimensional vector.
    /// Generally used for parameters that represent 3D coordinates or directions.
    /// </summary>
    Vector3,
    /// <summary>
    /// The parameter is a four-dimensional vector.
    /// Generally used for parameters that represent 4D coordinates or directions, such as quaternions.
    /// </summary>
    Vector4,
}
