using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Local frame of a driver triangle and the reconstruction of points from plane coordinates.</summary>
public static class TriangleFrame
{
    /// <summary>
    /// The smallest accepted length of the cross product (b - a) x (c - a).
    /// The test uses the length, not the squared length.
    /// </summary>
    public const float MinCrossLength = 1e-12f;

    /// <summary>
    /// Computes the rotation that maps local +X to the tangent t = normalize(b - a), local +Z to the normal
    /// n = normalize((b - a) x (c - a)), and local +Y to n x t. The frame is right-handed.
    /// Returns false, with an identity frame, when the length of the cross product is less than
    /// <see cref="MinCrossLength"/>.
    /// </summary>
    public static bool TryCompute(Vector3 a, Vector3 b, Vector3 c, out Quaternion frame)
    {
        Vector3 edge = b - a;
        Vector3 cross = Vector3.Cross(edge, c - a);
        float crossLength = cross.Length();
        float edgeLength = edge.Length();
        if (!(crossLength >= MinCrossLength) || !(edgeLength > 0f))
        {
            frame = Quaternion.Identity;
            return false;
        }

        Vector3 t = edge / edgeLength;
        Vector3 n = cross / crossLength;
        Vector3 bitangent = Vector3.Cross(n, t);
        Matrix4x4 m = new(
            t.X, t.Y, t.Z, 0f,
            bitangent.X, bitangent.Y, bitangent.Z, 0f,
            n.X, n.Y, n.Z, 0f,
            0f, 0f, 0f, 1f);
        frame = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        return true;
    }

    /// <summary>Returns a + u(b - a) + v(c - a) + h n, where n is the normalized triangle normal.</summary>
    public static Vector3 Reconstruct(Vector3 a, Vector3 b, Vector3 c, float u, float v, float h)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 n = Vector3.Normalize(Vector3.Cross(ab, ac));
        return a + u * ab + v * ac + h * n;
    }
}
