using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Sphere(Vector3 center, float radius) : IShape
    {
        public Vector3 Center = center;
        public float Radius = radius;

        public float Diameter
        {
            readonly get => Radius * 2.0f;
            set => Radius = value / 2.0f;
        }

        public readonly Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            Vector3 vec = point - Center;
            float length = vec.Length();
            return Center + vec / length * Radius;
        }

        public readonly bool ContainedWithin(AABB boundingBox)
        {
            Vector3 min = boundingBox.Min;
            Vector3 max = boundingBox.Max;
            Vector3 closestPoint = ClosestPoint(min, false);
            if (closestPoint.X < min.X || closestPoint.X > max.X)
                return false;
            if (closestPoint.Y < min.Y || closestPoint.Y > max.Y)
                return false;
            if (closestPoint.Z < min.Z || closestPoint.Z > max.Z)
                return false;
            return true;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            Vector3 min = box.Min;
            Vector3 max = box.Max;
            Vector3 closestPoint = ClosestPoint(min, false);
            if (closestPoint.X < min.X || closestPoint.X > max.X)
                return EContainment.Disjoint;
            if (closestPoint.Y < min.Y || closestPoint.Y > max.Y)
                return EContainment.Disjoint;
            if (closestPoint.Z < min.Z || closestPoint.Z > max.Z)
                return EContainment.Disjoint;
            return EContainment.Contains;
        }

        public readonly EContainment ContainsSphere(Sphere sphere)
        {
            float distance = Vector3.Distance(Center, sphere.Center);
            if (distance > Radius + sphere.Radius)
                return EContainment.Disjoint;
            if (distance + sphere.Radius < Radius)
                return EContainment.Contains;
            return EContainment.Intersects;
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
            Vector3 vec = point - Center;
            float length = vec.Length();
            return length <= Radius + tolerance;
        }

        public readonly AABB GetAABB(bool transformed) 
            => new(Center - new Vector3(Radius), Center + new Vector3(Radius));

        public readonly bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            Vector3 direction = segment.End - segment.Start;
            Vector3 diff = segment.Start - Center;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(diff, direction);
            float c = Vector3.Dot(diff, diff) - Radius * Radius;
            float discriminant = b * b - 4.0f * a * c;
            if (discriminant < 0)
            {
                points = [];
                return false;
            }
            float t1 = (-b + MathF.Sqrt(discriminant)) / (2.0f * a);
            float t2 = (-b - MathF.Sqrt(discriminant)) / (2.0f * a);
            points = [segment.Start + t1 * direction, segment.Start + t2 * direction];
            return true;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            Vector3 direction = segment.End - segment.Start;
            Vector3 diff = segment.Start - Center;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(diff, direction);
            float c = Vector3.Dot(diff, diff) - Radius * Radius;
            float discriminant = b * b - 4.0f * a * c;
            return discriminant >= 0;
        }

        public override readonly string ToString()
            => $"Sphere (Center: {Center}, Radius: {Radius})";

        public readonly EContainment ContainsBox(Box box)
        {
            Vector3 min = box.LocalMinimum;
            Vector3 max = box.LocalMaximum;
            var wtl = box.Transform.Inverted();
            Vector3 localCenter = Vector3.Transform(Center, wtl);
            if (localCenter.X - Radius > max.X || localCenter.X + Radius < min.X || 
                localCenter.Y - Radius > max.Y || localCenter.Y + Radius < min.Y || 
                localCenter.Z - Radius > max.Z || localCenter.Z + Radius < min.Z)
                return EContainment.Disjoint;
            if (localCenter.X - Radius < min.X && localCenter.X + Radius > max.X &&
                localCenter.Y - Radius < min.Y && localCenter.Y + Radius > max.Y &&
                localCenter.Z - Radius < min.Z && localCenter.Z + Radius > max.Z)
                return EContainment.Contains;
            return EContainment.Intersects;
        }
    }
}
