using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct CapsuleZ : IShape
    {
        private Vector3 _center;
        private float _radius;
        private float _halfHeight;

        public Vector3 Center
        {
            readonly get => _center;
            set => _center = value;
        }
        public float Radius
        {
            readonly get => _radius;
            set => _radius = value;
        }
        public float HalfHeight
        {
            readonly get => _halfHeight;
            set => _halfHeight = value;
        }

        public CapsuleZ() { }
        public CapsuleZ(Vector3 center, float radius, float halfHeight)
        {
            Center = center;
            Radius = radius;
            HalfHeight = halfHeight;
        }

        private readonly Capsule AsCapsule
            => new(Center, Vector3.UnitZ, Radius, HalfHeight);

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
            => AsCapsule.ContainsAABB(box, tolerance);

        public EContainment ContainsSphere(Sphere sphere)
            => AsCapsule.ContainsSphere(sphere);

        public EContainment ContainsCone(Cone cone)
            => AsCapsule.ContainsCone(cone);

        public EContainment ContainsCapsule(Capsule shape)
            => AsCapsule.ContainsCapsule(shape);

        public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
            => AsCapsule.ClosestPoint(point, clampToEdge);

        public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
            => AsCapsule.ContainsPoint(point, tolerance);

        public AABB GetAABB(bool transformed)
            => AsCapsule.GetAABB(transformed);

        public bool IntersectsSegment(Segment segment, out Vector3[] points)
            => AsCapsule.IntersectsSegment(segment, out points);

        public bool IntersectsSegment(Segment segment)
            => AsCapsule.IntersectsSegment(segment);

        public EContainment ContainsBox(Box box)
            => AsCapsule.ContainsBox(box);
    }
}
