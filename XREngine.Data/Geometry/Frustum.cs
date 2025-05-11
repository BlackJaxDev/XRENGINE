using Extensions;
using Silk.NET.Maths;
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
        public IReadOnlyList<Vector3> Corners => _corners;

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

        public Vector3 LeftBottomNear
        {
            get => _corners[0];
            private set => _corners[0] = value;
        }
        public Vector3 LeftTopNear
        {
            get => _corners[1];
            set => _corners[1] = value;
        }
        public Vector3 RightBottomNear
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
        public Vector3 LeftTopFar
        {
            get => _corners[5];
            set => _corners[5] = value;
        }
        public Vector3 RightBottomFar
        {
            get => _corners[6];
            set => _corners[6] = value;
        }
        public Vector3 RightTopFar
        {
            get => _corners[7];
            set => _corners[7] = value;
        }

        private readonly Plane[] _planes = new Plane[6];
        public IReadOnlyList<Plane> Planes => _planes;

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
            private set
            {
                _planes[0] = value;
                //Verify normal is facing right
                if (_planes[0].Normal.X < 0)
                    _planes[0] = new Plane(-_planes[0].Normal, -_planes[0].D);
            }
        }

        public Plane Right
        {
            get => _planes[1];
            private set
            {
                _planes[1] = value;
                //Verify normal is facing left
                if (_planes[1].Normal.X > 0)
                    _planes[1] = new Plane(-_planes[1].Normal, -_planes[1].D);
            }
        }

        public Plane Bottom
        {
            get => _planes[2];
            private set
            {
                _planes[2] = value;
                //Verify normal is facing up
                if (_planes[2].Normal.Y < 0)
                    _planes[2] = new Plane(-_planes[2].Normal, -_planes[2].D);
            }
        }

        public Plane Top
        {
            get => _planes[3];
            private set
            {
                _planes[3] = value;
                //Verify normal is facing down
                if (_planes[3].Normal.Y > 0)
                    _planes[3] = new Plane(-_planes[3].Normal, -_planes[3].D);
            }
        }

        public Plane Near
        {
            get => _planes[4];
            private set
            {
                _planes[4] = value;
                //Verify normal is facing forward (away from camera, negative Z)
                if (_planes[4].Normal.Z > 0)
                    _planes[4] = new Plane(-_planes[4].Normal, -_planes[4].D);
            }
        }

        public Plane Far
        {
            get => _planes[5];
            private set
            {
                _planes[5] = value;
                //Verify normal is facing backward (towards camera, positive Z)
                if (_planes[5].Normal.Z < 0)
                    _planes[5] = new Plane(-_planes[5].Normal, -_planes[5].D);
            }
        }

        private Frustum(Plane[] planes, Vector3[] corners)
        {
            _planes = planes;
            _corners = corners;
        }

        public Frustum() { }

        public Frustum(Matrix4x4 invProj) : this(
            DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 1.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 0.0f), invProj)),
            DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 0.0f), invProj))) { }
        
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

            UpdatePoints(fbl, fbr, ftl, ftr, nbl, nbr, ntl, ntr);

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

            UpdatePoints(fbl, fbr, ftl, ftr, nbl, nbr, ntl, ntr);
        }
        public Frustum(
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight) : this()
        {
            UpdatePoints(
                farBottomLeft, farBottomRight, farTopLeft, farTopRight,
                nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight);
        }
        private Frustum(
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
            Vector3 sphereCenter, float sphereRadius) : this()
            => UpdatePoints(
                farBottomLeft, farBottomRight, farTopLeft, farTopRight,
                nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight, sphereCenter, sphereRadius);

        public void UpdatePoints(
           Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
           Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight)
        {
            _corners[0] = farBottomLeft;
            _corners[1] = farBottomRight;
            _corners[2] = farTopLeft;
            _corners[3] = farTopRight;
            _corners[4] = nearBottomLeft;
            _corners[5] = nearBottomRight;
            _corners[6] = nearTopLeft;
            _corners[7] = nearTopRight;

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
            Vector3 farBottomLeft, Vector3 farBottomRight, Vector3 farTopLeft, Vector3 farTopRight,
            Vector3 nearBottomLeft, Vector3 nearBottomRight, Vector3 nearTopLeft, Vector3 nearTopRight,
            Vector3 sphereCenter, float sphereRadius)
        {
            _corners[0] = farBottomLeft;
            _corners[1] = farBottomRight;
            _corners[2] = farTopLeft;
            _corners[3] = farTopRight;
            _corners[4] = nearBottomLeft;
            _corners[5] = nearBottomRight;
            _corners[6] = nearTopLeft;
            _corners[7] = nearTopRight;

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

        public bool Intersects(AABB boundingBox)
        {
            for (int i = 0; i < 6; i++)
            {
                Plane plane = _planes[i];
                Vector3 point = new(
                    plane.Normal.X > 0 ? boundingBox.Min.X : boundingBox.Max.X,
                    plane.Normal.Y > 0 ? boundingBox.Min.Y : boundingBox.Max.Y,
                    plane.Normal.Z > 0 ? boundingBox.Min.Z : boundingBox.Max.Z);
                if (DistanceFromPointToPlane(point, plane) < 0)
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

        public IEnumerator<Plane> GetEnumerator() => ((IEnumerable<Plane>)_planes).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _planes.GetEnumerator();

        public EContainment Contains(Box box)
            => GeoUtil.FrustumContainsBox1(this, box.LocalHalfExtents, box.Transform);

        public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
            => GeoUtil.FrustumContainsAABB(this, box.Min, box.Max);

        public EContainment ContainsSphere(Sphere sphere)
            => GeoUtil.FrustumContainsSphere(this, sphere.Center, sphere.Radius);

        public EContainment Contains(IVolume shape)
            => shape switch
            {
                AABB box => ContainsAABB(box),
                Sphere sphere => ContainsSphere(sphere),
                Cone cone => ContainsCone(cone),
                Capsule capsule => ContainsCapsule(capsule),
                _ => throw new NotImplementedException(),
            };

        public EContainment ContainsCone(Cone cone)
            => GeoUtil.FrustumContainsCone(this, cone.Center, cone.Up, cone.Height, cone.Radius);

        public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            for (int i = 0; i < 6; i++)
                if (DistanceFromPointToPlane(point, _planes[i]) < 0)
                    return false;
            
            return true;
        }

        public bool ContainedWithin(AABB boundingBox)
        {
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
            var topContained = ContainsPoint(top, radius);
            var bottomContained = ContainsPoint(bottom, radius);
            if (topContained && bottomContained)
                return EContainment.Contains;
            if (topContained || bottomContained)
                return EContainment.Intersects;
            return EContainment.Disjoint;
        }

        public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            throw new NotImplementedException();
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

            bool nearHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, near.D, near.Normal, out Vector3 nearPoint);
            bool farHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, far.D, far.Normal, out Vector3 farPoint);
            bool leftHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, left.D, left.Normal, out Vector3 leftPoint);
            bool rightHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, right.D, right.Normal, out Vector3 rightPoint);
            bool topHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, top.D, top.Normal, out Vector3 topPoint);
            bool bottomHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, bottom.D, bottom.Normal, out Vector3 bottomPoint);

            //Each plane hit must be between the 4 planes perpendicular to it
            if (nearHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(nearPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(nearPoint, left, right, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(nearPoint);
            }

            if (farHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(farPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(farPoint, left, right, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(farPoint);
            }

            if (leftHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(leftPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(leftPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(leftPoint);
            }

            if (rightHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(rightPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(rightPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(rightPoint);
            }

            if (topHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(topPoint, left, right, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(topPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(topPoint);
            }

            if (bottomHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(bottomPoint, left, right, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(bottomPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    intersections.Add(bottomPoint);
            }

            points = [.. intersections];
            return points.Length > 0;
        }

        public bool IntersectsSegment(Segment segment)
        {
            Plane far = Far;
            Plane near = Near;
            Plane left = Left;
            Plane right = Right;
            Plane top = Top;
            Plane bottom = Bottom;

            bool nearHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, near.D, near.Normal, out Vector3 nearPoint);
            bool farHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, far.D, far.Normal, out Vector3 farPoint);
            bool leftHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, left.D, left.Normal, out Vector3 leftPoint);
            bool rightHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, right.D, right.Normal, out Vector3 rightPoint);
            bool topHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, top.D, top.Normal, out Vector3 topPoint);
            bool bottomHit = GeoUtil.SegmentIntersectsPlane(segment.Start, segment.End, bottom.D, bottom.Normal, out Vector3 bottomPoint);

            //Each plane hit must be between the 4 planes perpendicular to it
            if (nearHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(nearPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(nearPoint, left, right, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            if (farHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(farPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(farPoint, left, right, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            if (leftHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(leftPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(leftPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            if (rightHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(rightPoint, top, bottom, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(rightPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            if (topHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(topPoint, left, right, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(topPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            if (bottomHit)
            {
                if (GeoUtil.PointIsBetweenPlanes(bottomPoint, left, right, GeoUtil.EBetweenPlanes.DontCare) &&
                    GeoUtil.PointIsBetweenPlanes(bottomPoint, near, far, GeoUtil.EBetweenPlanes.DontCare))
                    return true;
            }

            return false;
        }

        public EContainment ContainsBox(Box box)
        {
            // First check if all corners of the box are inside the frustum
            var corners = box.WorldCorners;
            int numInside = 0;

            foreach (var corner in corners)
                if (ContainsPoint(corner))
                    numInside++;

            if (numInside == 8)
                return EContainment.Contains;

            if (numInside > 0)
                return EContainment.Intersects;

            // If no corners are inside, we need additional checks:

            // Check if any of the frustum edges intersect the box
            // Create segments for the 12 edges of the frustum
            var frustumEdges = new Segment[]
            {
                // Near face edges
                new(LeftBottomNear, LeftTopNear),
                new(LeftTopNear, RightTopNear),
                new(RightTopNear, RightBottomNear),
                new(RightBottomNear, LeftBottomNear),
                
                // Far face edges
                new(LeftBottomFar, LeftTopFar),
                new(LeftTopFar, RightTopFar),
                new(RightTopFar, RightBottomFar),
                new(RightBottomFar, LeftBottomFar),
                
                // Connecting edges
                new(LeftBottomNear, LeftBottomFar),
                new(LeftTopNear, LeftTopFar),
                new(RightTopNear, RightTopFar),
                new(RightBottomNear, RightBottomFar)
            };

            // If any frustum edge intersects the box, the shapes intersect
            foreach (var edge in frustumEdges)
                if (box.Intersects(edge))
                    return EContainment.Intersects;
            
            // Check if the frustum is completely inside the box
            if (box.Contains(this))
                return EContainment.Intersects;

            // Check if any box edge intersects a frustum face
            Vector3[] boxCorners = [.. corners];

            // Create segments for the 12 edges of the box
            var boxEdges = new Segment[]
            {
                // Bottom face
                new(boxCorners[0], boxCorners[1]),
                new(boxCorners[1], boxCorners[2]),
                new(boxCorners[2], boxCorners[3]),
                new(boxCorners[3], boxCorners[0]),
                
                // Top face
                new(boxCorners[4], boxCorners[5]),
                new(boxCorners[5], boxCorners[6]),
                new(boxCorners[6], boxCorners[7]),
                new(boxCorners[7], boxCorners[4]),
                
                // Connecting edges
                new(boxCorners[0], boxCorners[4]),
                new(boxCorners[1], boxCorners[5]),
                new(boxCorners[2], boxCorners[6]),
                new(boxCorners[3], boxCorners[7])
            };

            // If any box edge intersects the frustum, the shapes intersect
            foreach (var edge in boxEdges)
                if (IntersectsSegment(edge))
                    return EContainment.Intersects;
            
            // Test if the frustum is completely inside the box
            bool frustumInsideBox = true;
            foreach (var corner in Corners)
            {
                if (!box.ContainsPoint(corner))
                {
                    frustumInsideBox = false;
                    break;
                }
            }

            return frustumInsideBox ? EContainment.Intersects : EContainment.Disjoint;
        }
    }
}
