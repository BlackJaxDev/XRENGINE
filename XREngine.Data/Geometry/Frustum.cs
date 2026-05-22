using XREngine.Extensions;
using System.Collections;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using Plane = System.Numerics.Plane;

namespace XREngine.Data.Geometry
{
    public readonly struct Frustum : IVolume, IEnumerable<Plane>
    {
        /// <summary>
        /// Returns frustum corners in the following order:
        /// left bottom near, 
        /// left top near, 
        /// right bottom near, 
        /// right top near, 
        /// left bottom far, 
        /// left top far, 
        /// right bottom far, 
        /// right top far
        /// </summary>
        private readonly Vector3[] _corners = new Vector3[8];
        public IReadOnlyList<Vector3> Corners => _corners ?? [];

        private void ComputeCorners(Matrix4x4 mvp)
        {
            // Compute the inverse of the MVP matrix
            if (!Matrix4x4.Invert(mvp, out Matrix4x4 invMVP))
                throw new InvalidOperationException("Cannot invert the MVP matrix.");
            
            // Define the 8 corners of the unit cube in clip space
            Vector4[] clipSpaceCorners =
            [
                new(-1, -1, -1, 1), // Near bottom left
                new(-1, 1, -1, 1),  // Near top left
                new(1, -1, -1, 1),  // Near bottom right
                new(1, 1, -1, 1),   // Near top right
                new(-1, -1, 1, 1),  // Far bottom left
                new(-1, 1, 1, 1),   // Far top left
                new(1, -1, 1, 1),   // Far bottom right
                new(1, 1, 1, 1),    // Far top right
            ];

            // Transform the corners to world space
            for (int i = 0; i < 8; i++)
            {
                Vector4 corner = Vector4.Transform(clipSpaceCorners[i], invMVP);
                // Perform perspective divide
                corner /= corner.W;
                _corners[i] = new Vector3(corner.X, corner.Y, corner.Z);
            }
        }

        private readonly Plane[] _planes = new Plane[6];
        public IReadOnlyList<Plane> Planes => _planes ?? [];

        private void ExtractPlanes(Matrix4x4 mvp)
        {
            // Left plane
            Left = new Plane(
                mvp.M14 + mvp.M11,
                mvp.M24 + mvp.M21,
                mvp.M34 + mvp.M31,
                mvp.M44 + mvp.M41);

            // Right plane
            Right = new Plane(
                mvp.M14 - mvp.M11,
                mvp.M24 - mvp.M21,
                mvp.M34 - mvp.M31,
                mvp.M44 - mvp.M41);

            // Bottom plane
            Bottom = new Plane(
                mvp.M14 + mvp.M12,
                mvp.M24 + mvp.M22,
                mvp.M34 + mvp.M32,
                mvp.M44 + mvp.M42);

            // Top plane
            Top = new Plane(
                mvp.M14 - mvp.M12,
                mvp.M24 - mvp.M22,
                mvp.M34 - mvp.M32,
                mvp.M44 - mvp.M42);

            // Near plane
            Near = new Plane(
                mvp.M13,
                mvp.M23,
                mvp.M33,
                mvp.M43);

            // Far plane
            Far = new Plane(
                mvp.M14 - mvp.M13,
                mvp.M24 - mvp.M23,
                mvp.M34 - mvp.M33,
                mvp.M44 - mvp.M43);

            // Normalize the planes
            for (int i = 0; i < 6; i++)
                _planes[i] = Plane.Normalize(_planes[i]);
        }

        public Plane Left
        {
            get => _planes[0];
            private set => _planes[0] = OrientPlaneInward(value);
        }

        public Plane Right
        {
            get => _planes[1];
            private set => _planes[1] = OrientPlaneInward(value);
        }

        public Plane Bottom
        {
            get => _planes[2];
            private set => _planes[2] = OrientPlaneInward(value);
        }

        public Plane Top
        {
            get => _planes[3];
            private set => _planes[3] = OrientPlaneInward(value);
        }

        public Plane Near
        {
            get => _planes[4];
            private set => _planes[4] = OrientPlaneInward(value);
        }

        public Plane Far
        {
            get => _planes[5];
            private set => _planes[5] = OrientPlaneInward(value);
        }

        private Plane OrientPlaneInward(Plane plane)
        {
            plane = Plane.Normalize(plane);
            Vector3 center = GetApproximateCenter();
            return DistanceFromPointToPlane(center, plane) >= 0.0f
                ? plane
                : new Plane(-plane.Normal, -plane.D);
        }

        private Vector3 GetApproximateCenter()
        {
            Vector3 center = Vector3.Zero;
            for (int i = 0; i < _corners.Length; i++)
                center += _corners[i];

            return center / _corners.Length;
        }

        private Frustum(Plane[] planes, Vector3[] corners)
        {
            _planes = planes;
            _corners = corners;
        }

        public Frustum() { }
        public Frustum(Matrix4x4 invProj) : this(
            DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 1.0f), invProj))) { }
        
        private static Vector3 DivideW(Vector4 v)
            => new(v.X / v.W, v.Y / v.W, v.Z / v.W);

        public Frustum(float width, float height, float nearPlane, float farPlane) : this()
        {
            float 
                w = width / 2.0f, 
                h = height / 2.0f;

            AABB.GetCorners(
                new Vector3(-w, -h, -farPlane),
                new Vector3(w, h, -nearPlane),
                out Vector3 ftl,
                out Vector3 ftr,
                out Vector3 ntl,
                out Vector3 ntr,
                out Vector3 fbl,
                out Vector3 fbr,
                out Vector3 nbl,
                out Vector3 nbr);

            UpdatePoints(
                nbl, nbr, ntl, ntr,
                fbl, fbr, ftl, ftr);

            //float halfWidth = width / 2.0f;
            //float halfHeight = height / 2.0f;

            //Vector3 nearTopLeft = new(-halfWidth, halfHeight, nearPlane);
            //Vector3 nearTopRight = new(halfWidth, halfHeight, nearPlane);
            //Vector3 nearBottomLeft = new(-halfWidth, -halfHeight, nearPlane);
            //Vector3 nearBottomRight = new(halfWidth, -halfHeight, nearPlane);

            //Vector3 farTopLeft = new(-halfWidth, halfHeight, farPlane);
            //Vector3 farTopRight = new(halfWidth, halfHeight, farPlane);
            //Vector3 farBottomLeft = new(-halfWidth, -halfHeight, farPlane);
            //Vector3 farBottomRight = new(halfWidth, -halfHeight, farPlane);

            //UpdatePoints(
            //    farBottomLeft, farBottomRight, farTopLeft, farTopRight,
            //    nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight);
        }

        public Frustum(
           float fovY,
           float aspect,
           float nearZ,
           float farZ,
           Vector3 forward,
           Vector3 up,
           Vector3 position)
           : this()
        {
            float
                tan = (float)Math.Tan(XRMath.DegToRad(fovY / 2.0f)),
                nearYDist = tan * nearZ,
                nearXDist = aspect * nearYDist,
                farYDist = tan * farZ,
                farXDist = aspect * farYDist;

            Vector3
                rightDir = Vector3.Cross(forward, up),
                nearPos = position + forward * nearZ,
                farPos = position + forward * farZ,
                nX = rightDir * nearXDist,
                fX = rightDir * farXDist,
                nY = up * nearYDist,
                fY = up * farYDist,
                ntl = nearPos + nY - nX,
                ntr = nearPos + nY + nX,
                nbl = nearPos - nY - nX,
                nbr = nearPos - nY + nX,
                ftl = farPos + fY - fX,
                ftr = farPos + fY + fX,
                fbl = farPos - fY - fX,
                fbr = farPos - fY + fX;

            UpdatePoints(
                nbl, nbr, ntl, ntr,
                fbl, fbr, ftl, ftr);
        }
        public Frustum(
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight) : this()
        {
            UpdatePoints(
                nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight,
                farBottomLeft, farBottomRight, farTopLeft, farTopRight);
        }
        private Frustum(
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
            Vector3 sphereCenter, float sphereRadius) : this()
            => UpdatePoints(
                nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight,
                farBottomLeft, farBottomRight, farTopLeft, farTopRight,
                sphereCenter, sphereRadius);

        public Vector3 LeftBottomNear
        {
            get => _corners[0];
            set => _corners[0] = value;
        }
        public Vector3 RightBottomNear
        {
            get => _corners[1];
            set => _corners[1] = value;
        }
        public Vector3 LeftTopNear
        {
            get => _corners[2];
            set => _corners[2] = value;
        }
        public Vector3 RightTopNear
        {
            get => _corners[3];
            set => _corners[3] = value;
        }
        public Vector3 LeftBottomFar
        {
            get => _corners[4];
            set => _corners[4] = value;
        }
        public Vector3 RightBottomFar
        {
            get => _corners[5];
            set => _corners[5] = value;
        }
        public Vector3 LeftTopFar
        {
            get => _corners[6];
            set => _corners[6] = value;
        }
        public Vector3 RightTopFar
        {
            get => _corners[7];
            set => _corners[7] = value;
        }

        public void UpdatePoints(
           Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
           Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight)
        {
            _corners[0] = nearBottomLeft;
            _corners[1] = nearBottomRight;
            _corners[2] = nearTopLeft;
            _corners[3] = nearTopRight;
            _corners[4] = farBottomLeft;
            _corners[5] = farBottomRight;
            _corners[6] = farTopLeft;
            _corners[7] = farTopRight;

            //near, far
            Near = Plane.CreateFromVertices(nearBottomRight, nearBottomLeft, nearTopRight);
            Far = Plane.CreateFromVertices(farBottomLeft, farBottomRight, farTopLeft);

            //left, right
            Left = Plane.CreateFromVertices(nearBottomLeft, farBottomLeft, nearTopLeft);
            Right = Plane.CreateFromVertices(farBottomRight, nearBottomRight, farTopRight);

            //top, bottom
            Top = Plane.CreateFromVertices(farTopLeft, farTopRight, nearTopLeft);
            Bottom = Plane.CreateFromVertices(nearBottomLeft, nearBottomRight, farBottomLeft);

            //CalculateBoundingSphere();
        }

        private void UpdatePoints(
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
            Vector3 sphereCenter, float sphereRadius)
        {
            _corners[0] = nearBottomLeft;
            _corners[1] = nearBottomRight;
            _corners[2] = nearTopLeft;
            _corners[3] = nearTopRight;
            _corners[4] = farBottomLeft;
            _corners[5] = farBottomRight;
            _corners[6] = farTopLeft;
            _corners[7] = farTopRight;

            //near, far
            Near = Plane.CreateFromVertices(nearBottomRight, nearBottomLeft, nearTopRight);
            Far = Plane.CreateFromVertices(farBottomLeft, farBottomRight, farTopLeft);

            //left, right
            Left = Plane.CreateFromVertices(nearBottomLeft, farBottomLeft, nearTopLeft);
            Right = Plane.CreateFromVertices(farBottomRight, nearBottomRight, farTopRight);

            //top, bottom
            Top = Plane.CreateFromVertices(farTopLeft, farTopRight, nearTopLeft);
            Bottom = Plane.CreateFromVertices(nearBottomLeft, nearBottomRight, farBottomLeft);

            //UpdateBoundingSphere(sphereCenter, sphereRadius);
        }
        //public Plane this[int index]
        //{
        //    get => _planes[index];
        //    private set => _planes[index] = value;
        //}

        public Frustum Clone()
            => new(_planes, _corners);

        public PreparedFrustum Prepare()
            => PreparedFrustum.FromFrustum(this);

        public bool Intersects(AABB boundingBox)
        {
            if (_planes is null)
                return false;

            for (int i = 0; i < 6; i++)
            {
                Plane plane = _planes[i];
                Vector3 point = new(
                    plane.Normal.X >= 0.0f ? boundingBox.Max.X : boundingBox.Min.X,
                    plane.Normal.Y >= 0.0f ? boundingBox.Max.Y : boundingBox.Min.Y,
                    plane.Normal.Z >= 0.0f ? boundingBox.Max.Z : boundingBox.Min.Z);
                if (DistanceFromPointToPlane(point, plane) < -1e-5f)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates the distance from a point to a plane.
        /// When the point is in front of the plane, the distance is positive.
        /// When the point is behind the plane, the distance is negative.
        /// </summary>
        /// <param name="point"></param>
        /// <param name="plane"></param>
        /// <returns></returns>
        public static float DistanceFromPointToPlane(Vector3 point, Plane plane)
        {
            Vector3 normal = new(plane.Normal.X, plane.Normal.Y, plane.Normal.Z);
            return (Vector3.Dot(normal, point) + plane.D) / normal.Length();
        }

        /// <summary>
        /// Retrieves a slice of the frustum between two depths
        /// </summary>
        /// <param name="startDepth"></param>
        /// <param name="endDepth"></param>
        /// <returns></returns>
        public Frustum GetFrustumSlice(float startDepth, float endDepth)
        {
            Frustum f = Clone();
            f.Near = new Plane(_planes[4].Normal, _planes[4].D - startDepth);
            f.Far = new Plane(_planes[5].Normal, _planes[5].D + endDepth);
            return f;
        }

        public Plane GetBetweenNearAndFar(bool normalFacesNear)
            => GetBetween(normalFacesNear, Near, Far);
        public Plane GetBetweenLeftAndRight(bool normalFacesLeft)
            => GetBetween(normalFacesLeft, Left, Right);
        public Plane GetBetweenTopAndBottom(bool normalFacesTop)
            => GetBetween(normalFacesTop, Top, Bottom);
        public static Plane GetBetween(bool normalFacesFirst, Plane first, Plane second)
        {
            Vector3 topPoint = XRMath.GetPlanePoint(first);
            Vector3 bottomPoint = XRMath.GetPlanePoint(second);
            Vector3 normal = (normalFacesFirst 
                ? second.Normal - first.Normal 
                : first.Normal - second.Normal).Normalized();
            Vector3 midPoint = (topPoint + bottomPoint) / 2.0f;
            return XRMath.CreatePlaneFromPointAndNormal(midPoint, normal);
        }

        /// <summary>
        /// Divides the frustum into four frustum quadrants
        /// </summary>
        /// <returns></returns>
        public void DivideIntoFourths(
            out Frustum topLeft,
            out Frustum topRight,
            out Frustum bottomLeft,
            out Frustum bottomRight)
        {
            topLeft = Clone();
            //Fix bottom and right planes
            topLeft.Bottom = GetBetweenTopAndBottom(true);
            topLeft.Right = GetBetweenLeftAndRight(true);

            topRight = Clone();
            //Fix bottom and left planes
            topRight.Bottom = GetBetweenTopAndBottom(true);
            topRight.Left = GetBetweenLeftAndRight(false);

            bottomLeft = Clone();
            //Fix top and right planes
            bottomLeft.Top = GetBetweenTopAndBottom(false);
            bottomLeft.Right = GetBetweenLeftAndRight(true);

            bottomRight = Clone();
            //Fix top and left planes
            bottomRight.Top = GetBetweenTopAndBottom(false);
            bottomRight.Left = GetBetweenLeftAndRight(false);
        }

        public IEnumerator<Plane> GetEnumerator() => _planes is null ? Enumerable.Empty<Plane>().GetEnumerator() : ((IEnumerable<Plane>)_planes).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _planes?.GetEnumerator() ?? Enumerable.Empty<Plane>().GetEnumerator();

        public EContainment Contains(Box box)
            => ContainsBox(box);

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
            => GeoUtil.ContainmentOf.AABBWithinFrustum(this, box.Min, box.Max, tolerance);

        public EContainment ContainsSphere(Sphere sphere)
            => GeoUtil.ContainmentOf.SphereWithinFrustum(this, sphere.Center, sphere.Radius);

        public EContainment Contains(IVolume shape)
            => shape switch
            {
                AABB box => ContainsAABB(box),
                Box box => ContainsBox(box),
                Sphere sphere => ContainsSphere(sphere),
                Cone cone => ContainsCone(cone),
                Capsule capsule => ContainsCapsule(capsule),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), $"Unsupported volume type: {shape.GetType().Name}"),
            };

        public EContainment ContainsCone(Cone cone)
            => GeoUtil.ContainmentOf.ConeWithinFrustum(this, cone.Center, cone.Up, cone.Height, cone.Radius);

        public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            if (_planes is null)
                return false;

            for (int i = 0; i < 6; i++)
                if (DistanceFromPointToPlane(point, _planes[i]) < -tolerance)
                    return false;
            
            return true;
        }

        public bool ContainedWithin(AABB boundingBox)
        {
            if (_corners is null)
                return false;

            for (int i = 0; i < 8; i++)
                if (!boundingBox.ContainsPoint(_corners[i]))
                    return false;
            
            return true;
        }

        public EContainment ContainsCapsule(Capsule shape)
        {
            var top = shape.GetTopCenterPoint();
            var bottom = shape.GetBottomCenterPoint();
            var radius = shape.Radius;

            EContainment topContainment = GeoUtil.ContainmentOf.SphereWithinFrustum(this, top, radius);
            EContainment bottomContainment = GeoUtil.ContainmentOf.SphereWithinFrustum(this, bottom, radius);

            if (topContainment == EContainment.Contains && bottomContainment == EContainment.Contains)
                return EContainment.Contains;

            if (topContainment != EContainment.Disjoint || bottomContainment != EContainment.Disjoint)
                return EContainment.Intersects;

            if (IntersectsSegment(new Segment(bottom, top)))
                return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            if (ContainsPoint(point))
                return point;

            var corners = _corners;
            Vector3 closest = corners[0];
            float minDistSq = Vector3.DistanceSquared(point, closest);
            for (int i = 1; i < corners.Length; i++)
            {
                float distSq = Vector3.DistanceSquared(point, corners[i]);
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    closest = corners[i];
                }
            }
            return closest;
        }

        public AABB GetAABB(bool transformed)
        {
            var corners = _corners;
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            for (int i = 0; i < corners.Length; i++)
            {
                min = Vector3.Min(min, corners[i]);
                max = Vector3.Max(max, corners[i]);
            }
            return new AABB(min, max);
        }

        public Frustum TransformedBy(Matrix4x4 worldMatrix)
        {
            Frustum f = new();
            for (int i = 0; i < 8; i++)
                f._corners[i] = Vector3.Transform(_corners[i], worldMatrix);
            for (int i = 0; i < 6; i++)
                f._planes[i] = Plane.Transform(_planes[i], worldMatrix);
            return f;
        }

        public override string ToString()
            => $"Frustum (Near: {Near}, Far: {Far}, Left: {Left}, Right: {Right}, Top: {Top}, Bottom: {Bottom})";

        public bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            var intersections = new List<Vector3>();
            Plane far = Far;
            Plane near = Near;
            Plane left = Left;
            Plane right = Right;
            Plane top = Top;
            Plane bottom = Bottom;

            bool nearHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, near.D, near.Normal, out Vector3 nearPoint);
            bool farHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, far.D, far.Normal, out Vector3 farPoint);
            bool leftHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, left.D, left.Normal, out Vector3 leftPoint);
            bool rightHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, right.D, right.Normal, out Vector3 rightPoint);
            bool topHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, top.D, top.Normal, out Vector3 topPoint);
            bool bottomHit = GeoUtil.Intersect.SegmentWithPlane(segment.Start, segment.End, bottom.D, bottom.Normal, out Vector3 bottomPoint);

            //Each plane hit must be between the 4 planes perpendicular to it
            if (nearHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(nearPoint, top, bottom, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(nearPoint, left, right, EBetweenPlanes.DontCare))
                    intersections.Add(nearPoint);
            }

            if (farHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(farPoint, top, bottom, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(farPoint, left, right, EBetweenPlanes.DontCare))
                    intersections.Add(farPoint);
            }

            if (leftHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(leftPoint, top, bottom, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(leftPoint, near, far, EBetweenPlanes.DontCare))
                    intersections.Add(leftPoint);
            }

            if (rightHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(rightPoint, top, bottom, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(rightPoint, near, far, EBetweenPlanes.DontCare))
                    intersections.Add(rightPoint);
            }

            if (topHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(topPoint, left, right, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(topPoint, near, far, EBetweenPlanes.DontCare))
                    intersections.Add(topPoint);
            }

            if (bottomHit)
            {
                if (GeoUtil.Intersect.PointBetweenPlanes(bottomPoint, left, right, EBetweenPlanes.DontCare) &&
                    GeoUtil.Intersect.PointBetweenPlanes(bottomPoint, near, far, EBetweenPlanes.DontCare))
                    intersections.Add(bottomPoint);
            }

            points = [.. intersections];
            return points.Length > 0;
        }

        public bool IntersectsSegment(Segment segment)
            => IntersectsSegmentByPlanes(segment.Start, segment.End);

        public EContainment ContainsBox(Box box)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            GetBoxWorldCorners(box, corners);
            int numInside = 0;

            for (int i = 0; i < corners.Length; i++)
            {
                if (ContainsPoint(corners[i], 1e-5f))
                    numInside++;
            }

            if (numInside == 8)
                return EContainment.Contains;

            if (numInside > 0)
                return EContainment.Intersects;

            if (FrustumEdgesIntersectBox(box) ||
                BoxContainsFrustum(box) ||
                BoxEdgesIntersectFrustum(corners))
                return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        private static void GetBoxWorldCorners(Box box, Span<Vector3> corners)
        {
            Vector3 min = box.LocalMinimum;
            Vector3 max = box.LocalMaximum;
            Matrix4x4 transform = box.Transform;

            corners[0] = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), transform);
            corners[1] = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), transform);
            corners[2] = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), transform);
            corners[3] = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), transform);
            corners[4] = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), transform);
            corners[5] = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), transform);
            corners[6] = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), transform);
            corners[7] = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), transform);
        }

        private bool FrustumEdgesIntersectBox(Box box)
            => box.Intersects(new Segment(LeftBottomNear, LeftTopNear)) ||
               box.Intersects(new Segment(LeftTopNear, RightTopNear)) ||
               box.Intersects(new Segment(RightTopNear, RightBottomNear)) ||
               box.Intersects(new Segment(RightBottomNear, LeftBottomNear)) ||
               box.Intersects(new Segment(LeftBottomFar, LeftTopFar)) ||
               box.Intersects(new Segment(LeftTopFar, RightTopFar)) ||
               box.Intersects(new Segment(RightTopFar, RightBottomFar)) ||
               box.Intersects(new Segment(RightBottomFar, LeftBottomFar)) ||
               box.Intersects(new Segment(LeftBottomNear, LeftBottomFar)) ||
               box.Intersects(new Segment(LeftTopNear, LeftTopFar)) ||
               box.Intersects(new Segment(RightTopNear, RightTopFar)) ||
               box.Intersects(new Segment(RightBottomNear, RightBottomFar));

        private bool BoxContainsFrustum(Box box)
        {
            if (!Matrix4x4.Invert(box.Transform, out Matrix4x4 worldToBox))
                return false;

            Vector3 min = box.LocalMinimum;
            Vector3 max = box.LocalMaximum;
            IReadOnlyList<Vector3> corners = Corners;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 local = Vector3.Transform(corners[i], worldToBox);
                if (local.X < min.X || local.X > max.X ||
                    local.Y < min.Y || local.Y > max.Y ||
                    local.Z < min.Z || local.Z > max.Z)
                {
                    return false;
                }
            }

            return true;
        }

        private bool BoxEdgesIntersectFrustum(ReadOnlySpan<Vector3> corners)
            => IntersectsSegment(new Segment(corners[0], corners[1])) ||
               IntersectsSegment(new Segment(corners[0], corners[2])) ||
               IntersectsSegment(new Segment(corners[1], corners[3])) ||
               IntersectsSegment(new Segment(corners[2], corners[3])) ||
               IntersectsSegment(new Segment(corners[4], corners[5])) ||
               IntersectsSegment(new Segment(corners[4], corners[6])) ||
               IntersectsSegment(new Segment(corners[5], corners[7])) ||
               IntersectsSegment(new Segment(corners[6], corners[7])) ||
               IntersectsSegment(new Segment(corners[0], corners[4])) ||
               IntersectsSegment(new Segment(corners[1], corners[5])) ||
               IntersectsSegment(new Segment(corners[2], corners[6])) ||
               IntersectsSegment(new Segment(corners[3], corners[7]));

        private bool IntersectsSegmentByPlanes(Vector3 start, Vector3 end)
        {
            if (_planes is null)
                return false;

            Vector3 direction = end - start;
            float enter = 0.0f;
            float exit = 1.0f;

            for (int i = 0; i < 6; i++)
            {
                Plane plane = _planes[i];
                float tolerance = 1e-5f * plane.Normal.Length();
                float startDistance = Vector3.Dot(plane.Normal, start) + plane.D;
                float endDistance = Vector3.Dot(plane.Normal, end) + plane.D;
                bool startInside = startDistance >= -tolerance;
                bool endInside = endDistance >= -tolerance;

                if (!startInside && !endInside)
                    return false;

                if (startInside == endInside)
                    continue;

                float denominator = startDistance - endDistance;
                if (MathF.Abs(denominator) <= float.Epsilon)
                    continue;

                float t = startDistance / denominator;
                if (!startInside)
                    enter = MathF.Max(enter, t);
                else
                    exit = MathF.Min(exit, t);

                if (enter - exit > 1e-5f)
                    return false;
            }

            return true;
        }
    }
}
