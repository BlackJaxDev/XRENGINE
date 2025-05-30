using System.Numerics;

namespace XREngine.Data.Transforms.Rotations
{
    /// <summary>
    /// Represents a simple bivector in 3D (XY, XZ, YZ components).
    /// </summary>
    public struct Bivector3(float b01, float b02, float b12)
    {
        public float B01 = b01; // XY‐plane
        public float B02 = b02; // XZ‐plane
        public float B12 = b12; // YZ‐plane

        /// <summary>
        /// Wedge (exterior) product of two vectors, returning the bivector.
        /// </summary>
        public static Bivector3 Wedge(Vector3 u, Vector3 v)
            => new(
                u.X * v.Y - u.Y * v.X, // XY
                u.X * v.Z - u.Z * v.X, // XZ
                u.Y * v.Z - u.Z * v.Y  // YZ
            );
    }
}