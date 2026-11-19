namespace XREngine.Fbx;

/// <summary>
/// Interpolation of one FBX animation curve segment, from one key to the next key.
/// The values come from the interpolation bits of <c>KeyAttrFlags</c>.
/// </summary>
public enum EFbxCurveInterpolation : byte
{
    /// <summary>Straight line from the key value to the next key value.</summary>
    Linear,

    /// <summary>The key value holds until the next key time.</summary>
    Constant,

    /// <summary>The next key value applies immediately after the key time.</summary>
    ConstantNext,

    /// <summary>Weighted cubic Bezier curve from the key slopes and tangent weights.</summary>
    Cubic,
}
