using System.Numerics;

namespace XREngine.Scene.Physics;

/// <summary>
/// Marker contract for backend-neutral collision geometry authoring.
/// Native representations are produced by the selected physics backend.
/// </summary>
public interface IPhysicsGeometry
{
    [Serializable]
    public struct Sphere(float radius) : IPhysicsGeometry
    {
        public float Radius = radius;
    }

    [Serializable]
    public struct Box(Vector3 halfExtents) : IPhysicsGeometry
    {
        public Vector3 HalfExtents = halfExtents;
    }

    [Serializable]
    public struct Capsule(float radius, float halfHeight) : IPhysicsGeometry
    {
        public float Radius = radius;
        public float HalfHeight = halfHeight;
    }

    [Serializable]
    public struct Plane() : IPhysicsGeometry
    {
        public System.Numerics.Plane PlaneData = new(Vector3.UnitY, 0.0f);
    }
}
