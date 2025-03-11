using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct ConeY(Vector3 center, float height, float radius) : IShape
    {
        public Vector3 Center = center;
        public float Height = height;
        public float Radius = radius;

        public float Diameter
        {
            readonly get => Radius * 2.0f;
            set => Radius = value / 2.0f;
        }

        public Segment Axis
        {
            readonly get => new(Center, Center + Vector3.UnitY * Height);
            set
            {
                Center = value.Start;
                Height = value.Length;
            }
        }

        /// <summary>
        /// At t1, radius is 0 (the tip)
        /// At t0, radius is Radius (the base)
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public readonly float GetRadiusAlongAxisNormalized(float t)
            => Interp.Lerp(Radius, 0.0f, t);

        public readonly float GetRadiusAlongAxisAtHeight(float height)
            => GetRadiusAlongAxisNormalized(height / Height);

        public readonly Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            Vector3 dir = point - Center;
            float dot = Vector3.Dot(dir, Vector3.UnitY);
            if (dot < 0.0f)
                return Center;
            if (dot > Height)
                return Center + Vector3.UnitY * Height;
            return Center + Vector3.UnitY * dot + (dir - Vector3.UnitY * dot).Normalized() * Radius;
        }

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            throw new NotImplementedException();
        }

        public EContainment ContainsSphere(Sphere sphere)
        {
            throw new NotImplementedException();
        }

        public EContainment ContainsCone(Cone cone)
        {
            throw new NotImplementedException();
        }

        public EContainment ContainsCapsule(Capsule shape)
        {
            throw new NotImplementedException();
        }

        public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            throw new NotImplementedException();
        }

        public AABB GetAABB(bool transformed)
        {
            throw new NotImplementedException();
        }

        public bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            throw new NotImplementedException();
        }

        public bool IntersectsSegment(Segment segment)
        {
            throw new NotImplementedException();
        }

        public EContainment ContainsBox(Box box)
        {
            throw new NotImplementedException();
        }
    }
}
