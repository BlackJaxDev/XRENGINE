using Jitter2.LinearMath;
using System.Numerics;

namespace XREngine.Rendering.Physics.Physx
{
    public static class JitterExtensions
    {
        public static JVector ToJVector(this Vector3 v)
            => new(v.X, v.Y, v.Z);
        public static Vector3 ToVector3(this JVector v)
            => new(v.X, v.Y, v.Z);
    }
}