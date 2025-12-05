namespace XREngine.Rendering;

/// <summary>
/// Specifies the lens distortion projection mode.
/// </summary>
public enum ELensDistortionMode
{
    /// <summary>
    /// No lens distortion applied.
    /// </summary>
    None = 0,

    /// <summary>
    /// Radial barrel/pincushion distortion using manual intensity.
    /// Negative values = barrel (edges pulled in), Positive = pincushion (edges pushed out).
    /// </summary>
    Radial = 1,

    /// <summary>
    /// Radial distortion with intensity automatically calculated from camera FOV
    /// to counteract perspective stretching at wide angles.
    /// </summary>
    RadialAutoFromFOV = 2,

    /// <summary>
    /// Panini projection - preserves vertical lines while compressing horizontal periphery.
    /// Excellent for wide-angle views (90°+). Distance parameter controls compression strength.
    /// </summary>
    Panini = 3
}
