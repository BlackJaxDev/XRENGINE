using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Box : IShape
    {
        public Vector3 LocalCenter;
        public Vector3 LocalSize;
        public Matrix4x4 Transform = Matrix4x4.Identity;

        public readonly Vector3 LocalHalfExtents => LocalSize * 0.5f;
        public readonly Vector3 LocalMinimum => LocalCenter - LocalHalfExtents;
        public readonly Vector3 LocalMaximum => LocalCenter + LocalHalfExtents;

        public readonly Vector3 WorldCenter => Vector3.Transform(LocalCenter, Transform);
        public readonly Vector3 WorldMinimum => Vector3.Transform(LocalMinimum, Transform);
        public readonly Vector3 WorldMaximum => Vector3.Transform(LocalMaximum, Transform);

        public readonly Vector3 PointToLocalSpace(Vector3 worldPoint)
            => Matrix4x4.Invert(Transform, out var inv) ? Vector3.Transform(worldPoint, inv) : worldPoint;
        public readonly Vector3 PointToWorldSpace(Vector3 localPoint)
            => Vector3.Transform(localPoint, Transform);
        public readonly Vector3 NormalToLocalSpace(Vector3 worldNormal)
            => Matrix4x4.Invert(Transform, out var inv) ? Vector3.TransformNormal(worldNormal, Matrix4x4.Transpose(inv)) : worldNormal;
        public readonly Vector3 NormalToWorldSpace(Vector3 localNormal)
            => Vector3.TransformNormal(localNormal, Transform);

        public Box() { }
        public Box(float uniformSize)
        {
            LocalCenter = Vector3.Zero;
            LocalSize = new Vector3(uniformSize);
        }
        public Box(float sizeX, float sizeY, float sizeZ)
        {
            LocalCenter = Vector3.Zero;
            LocalSize = new Vector3(sizeX, sizeY, sizeZ);
        }
        public Box(Vector3 size)
        {
            LocalCenter = Vector3.Zero;
            LocalSize = size;
        }
        public Box(Vector3 center, Vector3 size)
        {
            LocalCenter = center;
            LocalSize = size;
        }
        public Box(Vector3 center, Vector3 size, Matrix4x4 transform)
        {
            LocalCenter = center;
            LocalSize = size;
            Transform = transform;
        }

        public static Box FromMinMax(Vector3 min, Vector3 max)
            => new((min + max) * 0.5f, max - min);

        public readonly bool Contains(Vector3 worldPoint)
            => ContainsPoint(worldPoint, float.Epsilon);
        public readonly bool ContainsPoint(Vector3 worldPoint, float tolerance)
        {
            var localPoint = PointToLocalSpace(worldPoint);
            return localPoint.X >= LocalMinimum.X && localPoint.X <= LocalMaximum.X &&
                   localPoint.Y >= LocalMinimum.Y && localPoint.Y <= LocalMaximum.Y &&
                   localPoint.Z >= LocalMinimum.Z && localPoint.Z <= LocalMaximum.Z;
        }

        public readonly bool Contains(Box box)
            => box.WorldCorners.All(Contains);

        public readonly bool Intersects(Box box)
        {
            var corners = box.WorldCorners.ToArray();
            var planes = WorldPlanes.ToArray();
            for (int i = 0; i < 6; i++)
            {
                var plane = planes[i];
                for (int j = 0; j < 8; j++)
                {
                    if (GeoUtil.DistancePlanePoint(plane, corners[j]) > 0)
                        break;
                    if (j == 7)
                        return false;
                }
            }
            return true;
        }

        public static Box FromPoints(Vector3[] corners)
        {
            var min = corners[0];
            var max = corners[0];
            for (int i = 1; i < corners.Length; i++)
            {
                min = Vector3.Min(min, corners[i]);
                max = Vector3.Max(max, corners[i]);
            }
            return FromMinMax(min, max);
        }

        public bool ContainedWithin(AABB boundingBox)
        {
            throw new NotImplementedException();
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

        public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            throw new NotImplementedException();
        }

        public readonly AABB GetAABB(bool transformed)
            => transformed 
            ? new(WorldMinimum, WorldMaximum)
            : new(LocalMinimum, LocalMaximum);

        public readonly bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            segment = segment.TransformedBy(Transform.Inverted());
            bool intersects = GeoUtil.SegmentIntersectsAABB(segment.Start, segment.End, LocalMinimum, LocalMaximum, out Vector3 enter, out Vector3 exit);
            points = intersects ? [PointToWorldSpace(enter), PointToWorldSpace(exit)] : [];
            return intersects;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            segment = segment.TransformedBy(Transform.Inverted());
            return GeoUtil.SegmentIntersectsAABB(segment.Start, segment.End, LocalMinimum, LocalMaximum, out _, out _);
        }

        public EContainment ContainsBox(Box box)
        {
            throw new NotImplementedException();
        }

        public readonly IEnumerable<Vector3> LocalCorners
        {
            get
            {
                yield return new Vector3(LocalMinimum.X, LocalMinimum.Y, LocalMinimum.Z);
                yield return new Vector3(LocalMaximum.X, LocalMinimum.Y, LocalMinimum.Z);
                yield return new Vector3(LocalMinimum.X, LocalMaximum.Y, LocalMinimum.Z);
                yield return new Vector3(LocalMaximum.X, LocalMaximum.Y, LocalMinimum.Z);
                yield return new Vector3(LocalMinimum.X, LocalMinimum.Y, LocalMaximum.Z);
                yield return new Vector3(LocalMaximum.X, LocalMinimum.Y, LocalMaximum.Z);
                yield return new Vector3(LocalMinimum.X, LocalMaximum.Y, LocalMaximum.Z);
                yield return new Vector3(LocalMaximum.X, LocalMaximum.Y, LocalMaximum.Z);
            }
        }

        public readonly IEnumerable<Plane> LocalPlanes
        {
            get
            {
                yield return new Plane(Vector3.UnitX, -LocalMinimum.X);
                yield return new Plane(-Vector3.UnitX, LocalMaximum.X);
                yield return new Plane(Vector3.UnitY, -LocalMinimum.Y);
                yield return new Plane(-Vector3.UnitY, LocalMaximum.Y);
                yield return new Plane(Vector3.UnitZ, -LocalMinimum.Z);
                yield return new Plane(-Vector3.UnitZ, LocalMaximum.Z);
            }
        }

        public readonly IEnumerable<Vector3> WorldCorners
            => LocalCorners.Select(PointToWorldSpace);

        public readonly IEnumerable<Plane> WorldPlanes
        {
            get
            {
                var tfm = Transform;
                return LocalPlanes.Select(p => Plane.Transform(p, tfm));
            }
        }
    }
}