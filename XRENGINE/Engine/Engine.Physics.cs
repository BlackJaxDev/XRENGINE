using System.Numerics;
using XREngine.Rendering.Physics.Physx;

namespace XREngine
{
    public static partial class Engine
    {
        public static class Physics
        {
            public static IPhysicsGeometry NewSphere(float radius) => new IPhysicsGeometry.Sphere(radius);

            public static IPhysicsGeometry NewBox(Vector3 halfExtents) => new IPhysicsGeometry.Box(halfExtents);

            public static IPhysicsGeometry NewCapsule(float radius, float halfHeight) => new IPhysicsGeometry.Capsule(radius, halfHeight);
        }
    }
}
