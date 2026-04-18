using XREngine.Extensions;
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

        private static float DistancePlanePoint(Plane plane, Vector3 point)
            => Vector3.Dot(plane.Normal, point) + plane.D;

        private static bool SegmentIntersectsAabb(Vector3 segmentStart, Vector3 segmentEnd, Vector3 boxMin, Vector3 boxMax)
            => SegmentIntersectsAabb(segmentStart, segmentEnd, boxMin, boxMax, out _, out _);

        private static bool SegmentIntersectsAabb(Vector3 segmentStart, Vector3 segmentEnd, Vector3 boxMin, Vector3 boxMax, out Vector3 enterPoint, out Vector3 exitPoint)
        {
            Vector3 direction = segmentEnd - segmentStart;
            float tMin = 0.0f;
            float tMax = 1.0f;

            for (int axis = 0; axis < 3; axis++)
            {
                float start = segmentStart[axis];
                float delta = direction[axis];
                float min = boxMin[axis];
                float max = boxMax[axis];

                if (MathF.Abs(delta) <= 1e-12f)
                {
                    if (start < min || start > max)
                    {
                        enterPoint = Vector3.Zero;
                        exitPoint = Vector3.Zero;
                        return false;
                    }

                    continue;
                }

                float invDelta = 1.0f / delta;
                float t1 = (min - start) * invDelta;
                float t2 = (max - start) * invDelta;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);

                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax)
                {
                    enterPoint = Vector3.Zero;
                    exitPoint = Vector3.Zero;
                    return false;
                }
            }

            enterPoint = segmentStart + direction * tMin;
            exitPoint = segmentStart + direction * tMax;
            return true;
        }

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
            return SegmentIntersectsAabb(localSegment.Start, localSegment.End, LocalMinimum, LocalMaximum);
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
                    if (DistancePlanePoint(plane, corners[j]) > 0)
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
            var corners = WorldCorners;
            foreach (var corner in corners)
                if (!boundingBox.ContainsPoint(corner))
                    return false;
            return true;
        }

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            Vector3[] corners = box.GetCorners();
            bool allInside = true;
            bool anyInside = false;
            for (int i = 0; i < corners.Length; i++)
            {
                bool inside = ContainsPoint(corners[i], tolerance);
                allInside &= inside;
                anyInside |= inside;
            }

            if (allInside)
                return EContainment.Contains;
            if (anyInside)
                return EContainment.Intersects;

            if (GetAABB(true).Intersects(box))
                return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        public EContainment ContainsSphere(Sphere sphere)
        {
            Vector3 localCenter = PointToLocalSpace(sphere.Center);
            Vector3 min = LocalMinimum;
            Vector3 max = LocalMaximum;

            Vector3 closest = Vector3.Clamp(localCenter, min, max);
            Vector3 delta = localCenter - closest;
            float radius = MathF.Max(0.0f, sphere.Radius);
            float radiusSquared = radius * radius;
            if (Vector3.Dot(delta, delta) > radiusSquared)
                return EContainment.Disjoint;

            bool fullyInside =
                localCenter.X - radius >= min.X && localCenter.X + radius <= max.X &&
                localCenter.Y - radius >= min.Y && localCenter.Y + radius <= max.Y &&
                localCenter.Z - radius >= min.Z && localCenter.Z + radius <= max.Z;

            return fullyInside ? EContainment.Contains : EContainment.Intersects;
        }

        public EContainment ContainsCone(Cone cone)
        {
            Vector3 localCenter = PointToLocalSpace(cone.Center);
            Vector3 localUp = NormalToLocalSpace(cone.Up);
            float localUpLengthSquared = localUp.LengthSquared();
            if (localUpLengthSquared > 1e-12f)
                localUp /= MathF.Sqrt(localUpLengthSquared);
            else
                localUp = Vector3.UnitY;

            Cone localCone = new(localCenter, localUp, cone.Height, cone.Radius);
            AABB localAabb = new(LocalMinimum, LocalMaximum);
            return localAabb.ContainsCone(localCone);
        }

        public EContainment ContainsCapsule(Capsule shape)
        {
            Vector3 localCenter = PointToLocalSpace(shape.Center);
            Vector3 localUp = NormalToLocalSpace(shape.UpAxis);
            float localUpLengthSquared = localUp.LengthSquared();
            if (localUpLengthSquared > 1e-12f)
                localUp /= MathF.Sqrt(localUpLengthSquared);
            else
                localUp = Vector3.UnitY;

            Capsule localCapsule = new(localCenter, localUp, shape.Radius, shape.HalfHeight);
            AABB localAabb = new(LocalMinimum, LocalMaximum);
            return localAabb.ContainsCapsule(localCapsule);
        }

        public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            Vector3 localPoint = PointToLocalSpace(point);
            Vector3 localClosest = Vector3.Clamp(localPoint, LocalMinimum, LocalMaximum);

            if (clampToEdge)
            {
                float minDistX = MathF.Abs(localPoint.X - LocalMinimum.X);
                float maxDistX = MathF.Abs(LocalMaximum.X - localPoint.X);
                float minDistY = MathF.Abs(localPoint.Y - LocalMinimum.Y);
                float maxDistY = MathF.Abs(LocalMaximum.Y - localPoint.Y);
                float minDistZ = MathF.Abs(localPoint.Z - LocalMinimum.Z);
                float maxDistZ = MathF.Abs(LocalMaximum.Z - localPoint.Z);

                float bestDist = minDistX;
                int bestAxis = 0;
                bool useMax = false;

                if (maxDistX < bestDist)
                {
                    bestDist = maxDistX;
                    bestAxis = 0;
                    useMax = true;
                }
                if (minDistY < bestDist)
                {
                    bestDist = minDistY;
                    bestAxis = 1;
                    useMax = false;
                }
                if (maxDistY < bestDist)
                {
                    bestDist = maxDistY;
                    bestAxis = 1;
                    useMax = true;
                }
                if (minDistZ < bestDist)
                {
                    bestDist = minDistZ;
                    bestAxis = 2;
                    useMax = false;
                }
                if (maxDistZ < bestDist)
                {
                    bestAxis = 2;
                    useMax = true;
                }

                localClosest[bestAxis] = useMax ? LocalMaximum[bestAxis] : LocalMinimum[bestAxis];
            }

            return PointToWorldSpace(localClosest);
        }

        public readonly AABB GetAABB(bool transformed)
            => transformed 
            ? new(WorldMinimum, WorldMaximum)
            : new(LocalMinimum, LocalMaximum);

        public readonly bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            segment = segment.TransformedBy(_transform.Inverted());
            bool intersects = SegmentIntersectsAabb(segment.Start, segment.End, LocalMinimum, LocalMaximum, out Vector3 enter, out Vector3 exit);
            points = intersects ? [PointToWorldSpace(enter), PointToWorldSpace(exit)] : [];
            return intersects;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            segment = segment.TransformedBy(_transform.Inverted());
            return SegmentIntersectsAabb(segment.Start, segment.End, LocalMinimum, LocalMaximum);
        }

        public EContainment ContainsBox(Box box)
        {
            var corners = box.WorldCorners.ToArray();
            var planes = WorldPlanes.ToArray();
            for (int i = 0; i < 6; i++)
            {
                var plane = planes[i];
                for (int j = 0; j < 8; j++)
                {
                    if (DistancePlanePoint(plane, corners[j]) > 0)
                        break;
                    if (j == 7)
                        return EContainment.Disjoint;
                }
            }
            return EContainment.Contains;
        }
    }
}