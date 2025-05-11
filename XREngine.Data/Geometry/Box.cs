using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Box : IShape
    {
        private Vector3 _localCenter;
        private Vector3 _localSize;
        private Matrix4x4 _transform = Matrix4x4.Identity;

        private bool _localCacheValid = false;
        private bool _worldCacheValid = false;

        private Vector3[]? _cachedLocalCorners;
        private Plane[]? _cachedLocalPlanes;
        private Vector3[]? _cachedWorldCorners;
        private Plane[]? _cachedWorldPlanes;

        public Vector3 LocalCenter
        {
            readonly get => _localCenter;
            set
            {
                _localCenter = value;
                _localCacheValid = false;
            }
        }
        public Vector3 LocalSize
        {
            readonly get => _localSize;
            set
            {
                _localSize = value;
                _localCacheValid = false;
            }
        }
        public Matrix4x4 Transform
        {
            readonly get => _transform;
            set
            {
                _transform = value;
                _worldCacheValid = false;
            }
        }

        public readonly Vector3 LocalHalfExtents => _localSize * 0.5f;
        public readonly Vector3 LocalMinimum => _localCenter - LocalHalfExtents;
        public readonly Vector3 LocalMaximum => _localCenter + LocalHalfExtents;

        private void UpdateLocalCache()
        {
            var min = LocalMinimum;
            var max = LocalMaximum;

            _cachedLocalCorners =
            [
                new Vector3(min.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z),
                new Vector3(max.X, min.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z)
            ];

            _cachedLocalPlanes =
            [
                new Plane(Vector3.UnitX, -min.X),
                new Plane(-Vector3.UnitX, max.X),
                new Plane(Vector3.UnitY, -min.Y),
                new Plane(-Vector3.UnitY, max.Y),
                new Plane(Vector3.UnitZ, -min.Z),
                new Plane(-Vector3.UnitZ, max.Z)
            ];
        }

        private void UpdateWorldCache()
        {
            if (!_localCacheValid)
                UpdateLocalCache();
            _cachedWorldCorners = [.. _cachedLocalCorners!.Select(PointToWorldSpace)];
            var tfm = _transform;
            _cachedWorldPlanes = [.. _cachedLocalPlanes!.Select(p => Plane.Transform(p, tfm))];
        }

        public IEnumerable<Vector3> LocalCorners
        {
            get
            {
                if (!_localCacheValid)
                    UpdateLocalCache();
                return _cachedLocalCorners!;
            }
        }

        public IEnumerable<Plane> LocalPlanes
        {
            get
            {
                if (!_localCacheValid)
                    UpdateLocalCache();
                return _cachedLocalPlanes!;
            }
        }

        public IEnumerable<Vector3> WorldCorners
        {
            get
            {
                if (!_worldCacheValid)
                    UpdateWorldCache();
                return _cachedWorldCorners!;
            }
        }

        public IEnumerable<Plane> WorldPlanes
        {
            get
            {
                if (!_worldCacheValid)
                    UpdateWorldCache();
                return _cachedWorldPlanes!;
            }
        }

        public readonly Vector3 WorldCenter => Vector3.Transform(_localCenter, _transform);
        public readonly Vector3 WorldMinimum => Vector3.Transform(LocalMinimum, _transform);
        public readonly Vector3 WorldMaximum => Vector3.Transform(LocalMaximum, _transform);

        public readonly Vector3 PointToLocalSpace(Vector3 worldPoint)
            => Matrix4x4.Invert(_transform, out var inv) ? Vector3.Transform(worldPoint, inv) : worldPoint;
        public readonly Vector3 PointToWorldSpace(Vector3 localPoint)
            => Vector3.Transform(localPoint, _transform);
        public readonly Vector3 NormalToLocalSpace(Vector3 worldNormal)
            => Matrix4x4.Invert(_transform, out var inv) ? Vector3.TransformNormal(worldNormal, Matrix4x4.Transpose(inv)) : worldNormal;
        public readonly Vector3 NormalToWorldSpace(Vector3 localNormal)
            => Vector3.TransformNormal(localNormal, _transform);

        public Box() { }
        public Box(float uniformSize)
        {
            _localCenter = Vector3.Zero;
            _localSize = new Vector3(uniformSize);
        }
        public Box(float sizeX, float sizeY, float sizeZ)
        {
            _localCenter = Vector3.Zero;
            _localSize = new Vector3(sizeX, sizeY, sizeZ);
        }
        public Box(Vector3 size)
        {
            _localCenter = Vector3.Zero;
            _localSize = size;
        }
        public Box(Vector3 center, Vector3 size)
        {
            _localCenter = center;
            _localSize = size;
        }
        public Box(Vector3 center, Vector3 size, Matrix4x4 transform)
        {
            _localCenter = center;
            _localSize = size;
            _transform = transform;
        }

        public static Box FromMinMax(Vector3 min, Vector3 max)
            => new((min + max) * 0.5f, max - min);

        public readonly bool Contains(Vector3 worldPoint)
            => ContainsPoint(worldPoint, float.Epsilon);
        public readonly bool ContainsPoint(Vector3 worldPoint, float tolerance = 0.0001f)
        {
            var localPoint = PointToLocalSpace(worldPoint);
            return localPoint.X >= LocalMinimum.X && localPoint.X <= LocalMaximum.X &&
                   localPoint.Y >= LocalMinimum.Y && localPoint.Y <= LocalMaximum.Y &&
                   localPoint.Z >= LocalMinimum.Z && localPoint.Z <= LocalMaximum.Z;
        }

        public readonly bool Contains(Frustum f)
        {
            foreach (var corner in f.Corners)
                if (!Contains(corner))
                    return false;
            return true;
        }

        public readonly bool Contains(Box box)
            => box.WorldCorners.All(Contains);

        public readonly bool Intersects(Segment segment)
        {
            var localSegment = segment.TransformedBy(_transform.Inverted());
            return GeoUtil.SegmentIntersectsAABB(localSegment.Start, localSegment.End, LocalMinimum, LocalMaximum, out _, out _);
        }

        public bool Intersects(Box box)
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
            segment = segment.TransformedBy(_transform.Inverted());
            bool intersects = GeoUtil.SegmentIntersectsAABB(segment.Start, segment.End, LocalMinimum, LocalMaximum, out Vector3 enter, out Vector3 exit);
            points = intersects ? [PointToWorldSpace(enter), PointToWorldSpace(exit)] : [];
            return intersects;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            segment = segment.TransformedBy(_transform.Inverted());
            return GeoUtil.SegmentIntersectsAABB(segment.Start, segment.End, LocalMinimum, LocalMaximum, out _, out _);
        }

        public EContainment ContainsBox(Box box)
        {
            throw new NotImplementedException();
        }
    }
}