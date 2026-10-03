using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Conservative local AABB rejection against a row-vector WebGPU clip transform.</summary>
internal static class BrowserFrustumVisibility
{
    public static bool Intersects(BrowserMeshData mesh, in Matrix4x4 m)
    {
        // An invalid transform must not incorrectly hide geometry. The upload descriptor's
        // bounds are already finite, so only the current transform requires this guard.
        if (!Finite(m))
            return true;

        Vector3 min = mesh.BoundsMinimum;
        Vector3 max = mesh.BoundsMaximum;
        // x and y lie in [-w, w], while WebGPU's z lies in [0, w].
        return Inside(min, max, m.M11 + m.M14, m.M21 + m.M24, m.M31 + m.M34, m.M41 + m.M44) &&
            Inside(min, max, m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41) &&
            Inside(min, max, m.M12 + m.M14, m.M22 + m.M24, m.M32 + m.M34, m.M42 + m.M44) &&
            Inside(min, max, m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42) &&
            Inside(min, max, m.M13, m.M23, m.M33, m.M43) &&
            Inside(min, max, m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
    }

    private static bool Inside(Vector3 min, Vector3 max, float x, float y, float z, float w)
    {
        float vx = (x >= 0 ? max.X : min.X) * x;
        float vy = (y >= 0 ? max.Y : min.Y) * y;
        float vz = (z >= 0 ? max.Z : min.Z) * z;
        float furthest = vx + vy + vz + w;
        float tolerance = 0.00001f * (MathF.Abs(vx) + MathF.Abs(vy) + MathF.Abs(vz) + MathF.Abs(w));
        return !float.IsFinite(furthest) || !float.IsFinite(tolerance) || furthest >= -tolerance;
    }

    private static bool Finite(in Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
