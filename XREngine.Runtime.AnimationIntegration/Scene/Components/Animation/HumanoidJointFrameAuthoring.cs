using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Builds a proper canonical-joint-to-zero-local rotation from anatomical axes.
/// Authoring code supplies axes in the zero-pose parent/world frame; this helper
/// deliberately uses quaternion inversion only, never an affine bind inverse,
/// so non-uniform bind scale cannot contaminate a rotation basis.
/// </summary>
internal static class HumanoidJointFrameAuthoring
{
    /// <summary>
    /// Tries to construct the rotation from canonical X/Y joint axes to the
    /// bone's zero-muscle local frame. The supplied anatomical axes must be
    /// non-collinear and are interpreted in the zero-pose frame represented by
    /// <paramref name="zeroPoseRotation"/>.
    /// </summary>
    public static bool TryCreateJointBasis(
        Quaternion zeroPoseRotation,
        Vector3 canonicalXWorld,
        Vector3 canonicalYWorld,
        bool preserveCanonicalY,
        out Quaternion jointBasisToZeroLocal)
    {
        jointBasisToZeroLocal = Quaternion.Identity;
        if (!IsFiniteNormalizedQuaternion(zeroPoseRotation)
            || !TryNormalize(canonicalXWorld, out Vector3 xWorld)
            || !TryNormalize(canonicalYWorld, out Vector3 yWorld))
            return false;

        Quaternion inverseZeroPose = Quaternion.Inverse(Quaternion.Normalize(zeroPoseRotation));
        Vector3 xLocal = Vector3.Transform(xWorld, inverseZeroPose);
        Vector3 yLocal = Vector3.Transform(yWorld, inverseZeroPose);
        if (preserveCanonicalY)
        {
            if (!TryNormalize(yLocal, out yLocal))
                return false;

            // Chain-authored joints use Y as their anatomical long axis. Keep
            // that measured axis exact and project the reference bend axis into
            // its normal plane; otherwise a non-orthogonal bind chain invents
            // swing during twist and twist during swing.
            xLocal -= Vector3.Dot(xLocal, yLocal) * yLocal;
            if (!TryNormalize(xLocal, out xLocal))
                return false;
        }
        else
        {
            if (!TryNormalize(xLocal, out xLocal))
                return false;

            yLocal -= Vector3.Dot(yLocal, xLocal) * xLocal;
            if (!TryNormalize(yLocal, out yLocal))
                return false;
        }

        Vector3 zLocal = Vector3.Cross(xLocal, yLocal);
        if (!TryNormalize(zLocal, out zLocal))
            return false;

        var basis = new Matrix4x4(
            xLocal.X, xLocal.Y, xLocal.Z, 0.0f,
            yLocal.X, yLocal.Y, yLocal.Z, 0.0f,
            zLocal.X, zLocal.Y, zLocal.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
        jointBasisToZeroLocal = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        return IsFiniteNormalizedQuaternion(jointBasisToZeroLocal);
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(value.X)
            || !float.IsFinite(value.Y)
            || !float.IsFinite(value.Z)
            || !float.IsFinite(lengthSquared)
            || lengthSquared <= 1e-12f)
        {
            normalized = Vector3.Zero;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static bool IsFiniteNormalizedQuaternion(Quaternion value)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && float.IsFinite(lengthSquared)
            && lengthSquared > 1e-12f;
    }
}
