using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Cone(Vector3 center, Vector3 up, float height, float radius) : IShape
    {
        public Vector3 Center = center;
        public Vector3 Up = up;
        public float Height = height;
        public float Radius = radius;

        public float Diameter
        {
            readonly get => Radius * 2.0f;
            set => Radius = value / 2.0f;
        }

        public Segment Axis
        {
            readonly get => new(Center, Center + Up * Height);
            set
            {
                Center = value.Start;
                Up = Vector3.Normalize(value.End - value.Start);
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
            float dot = Vector3.Dot(dir, Up);
            if (dot < 0.0f)
                return Center;
            if (dot > Height)
                return Center + Up * Height;
            return Center + Up * dot + Vector3.Normalize(dir - Up * dot) * Radius;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            var corners = box.GetCorners();
            foreach (Vector3 corner in corners)
                if (!ContainsPoint(corner, tolerance))
                    return EContainment.Disjoint;
            return EContainment.Contains;
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

        public readonly bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            Vector3 dir = point - Center;
            float dot = Vector3.Dot(dir, Up);
            if (dot < 0.0f || dot > Height)
                return false;
            Vector3 closest = Center + Up * dot + (dir - Up * dot).Normalized() * Radius;
            return Vector3.DistanceSquared(point, closest) <= Radius * Radius;
        }

        public override readonly string ToString()
            => $"Cone (Center: {Center}, Up: {Up}, Height: {Height}, Radius: {Radius})";

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
