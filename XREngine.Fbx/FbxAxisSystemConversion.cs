using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Fbx;

/// <summary>
/// Converts a declared FBX axis system to a canonical Y-up basis while preserving
/// the source handedness. Importers can apply the resulting proper rotation at the
/// imported root without reflecting geometry or changing triangle winding.
/// </summary>
public static class FbxAxisSystemConversion
{
    /// <summary>
    /// Resolves the proper rotation from the FBX right/up/front basis to the
    /// matching-handed X-right, Y-up basis.
    /// </summary>
    public static bool TryCreateCanonicalYUpRotation(
        FbxAxisSystem axisSystem,
        out Matrix4x4 rotation)
    {
        ArgumentNullException.ThrowIfNull(axisSystem);

        if (!TryConvertAxis(axisSystem.CoordAxis, out SpatialAxis right)
            || !TryConvertAxis(axisSystem.UpAxis, out SpatialAxis up)
            || !TryConvertAxis(axisSystem.FrontAxis, out SpatialAxis forward))
        {
            rotation = Matrix4x4.Identity;
            return false;
        }

        SpatialCoordinateSystem source;
        try
        {
            source = new SpatialCoordinateSystem(right, up, forward);
        }
        catch (ArgumentException)
        {
            rotation = Matrix4x4.Identity;
            return false;
        }

        SpatialCoordinateSystem target = source.IsRightHanded
            ? SpatialCoordinateSystem.XRightYUpZForward
            : SpatialCoordinateSystem.Engine;
        // The result is post-multiplied onto the imported root, so it maps the
        // source basis vectors into canonical world axes. That is the inverse of
        // converting already-expressed vector components from source to target.
        rotation = SpatialCoordinateConversion.GetVectorConversionMatrix(target, source);
        return IsFinite(rotation);
    }

    private static bool TryConvertAxis(FbxSignedAxis source, out SpatialAxis axis)
    {
        axis = default;
        if (source.Sign is not (-1 or 1))
            return false;

        axis = (source.AxisIndex, source.Sign) switch
        {
            (0, 1) => SpatialAxis.PositiveX,
            (0, -1) => SpatialAxis.NegativeX,
            (1, 1) => SpatialAxis.PositiveY,
            (1, -1) => SpatialAxis.NegativeY,
            (2, 1) => SpatialAxis.PositiveZ,
            (2, -1) => SpatialAxis.NegativeZ,
            _ => default,
        };
        return source.AxisIndex is >= 0 and <= 2;
    }

    private static bool IsFinite(in Matrix4x4 matrix)
        => float.IsFinite(matrix.M11)
        && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M13)
        && float.IsFinite(matrix.M21)
        && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M23)
        && float.IsFinite(matrix.M31)
        && float.IsFinite(matrix.M32)
        && float.IsFinite(matrix.M33);
}
