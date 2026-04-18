using XREngine.Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct ConeX(Vector3 center, float height, float radius) : IShape
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
            readonly get => new(Center, Center + Vector3.UnitX * Height);
            set
            {
                Center = value.Start;
                Height = value.End.X - value.Start.X;
            }
        }

        private readonly Cone AsCone
            => new(Center, Vector3.UnitX, Height, Radius);

        /// <summary>
        /// At t1, radius is 0 (the tip)
        /// At t0, radius is Radius (the base)
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public readonly float GetRadiusAlongAxisNormalized(float t)
            => Interp.Lerp(Radius, 0.0f, t);

        public readonly float GetRadiusAlongAxisAtHeight(float height)
            => Height == 0.0f ? 0.0f : GetRadiusAlongAxisNormalized(height / Height);

        public readonly Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
            => AsCone.ClosestPoint(point, clampToEdge);

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
            => AsCone.ContainsAABB(box, tolerance);

        public EContainment ContainsSphere(Sphere sphere)
            => AsCone.ContainsSphere(sphere);

        public EContainment ContainsCone(Cone cone)
            => AsCone.ContainsCone(cone);

        public EContainment ContainsCapsule(Capsule shape)
            => AsCone.ContainsCapsule(shape);

        public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
            => AsCone.ContainsPoint(point, tolerance);

        public AABB GetAABB(bool transformed)
            => AsCone.GetAABB(transformed);

        public bool IntersectsSegment(Segment segment, out Vector3[] points)
            => AsCone.IntersectsSegment(segment, out points);

        public bool IntersectsSegment(Segment segment)
            => AsCone.IntersectsSegment(segment);

        public EContainment ContainsBox(Box box)
            => AsCone.ContainsBox(box);
    }
}
