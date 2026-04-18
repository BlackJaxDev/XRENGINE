using XREngine.Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Combines two projection matrices into a single projection matrix that can encompass both frustums.
    /// Ignores a lot of edge cases, but works for what we need.
    /// </summary>
    public static class ProjectionMatrixCombiner
    {
        public readonly record struct FrustumPointCloud(
            Vector3[] AllPoints,
            Vector3[] FarPoints,
            int SourceCount);

        public readonly record struct FrustumSolveResult(
            Matrix4x4 View,
            Matrix4x4 Projection,
            Quaternion Orientation,
            string CandidateLabel,
            float Cost,
            bool WasRefined);

        private readonly record struct OrientationCandidate(string Label, Quaternion Orientation, bool WasRefined = false);

        private const float MinNearDistance = 0.001f;
        private static readonly float[] RefinementStepDegrees = [6.0f, 3.0f, 1.5f, 0.75f];
        private static readonly (int start, int end)[] FrustumEdges =
        [
            (0, 1), (1, 3), (3, 2), (2, 0),
            (4, 5), (5, 7), (7, 6), (6, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];

        public static Matrix4x4 CombineProjectionMatrices(Matrix4x4 proj1, Matrix4x4 proj2, Matrix4x4? view1 = null, Matrix4x4? view2 = null)
            => CombineProjectionMatrices(
                [proj1, proj2],
                [view1 ?? Matrix4x4.Identity, view2 ?? Matrix4x4.Identity]);

        public static Matrix4x4 CombineProjectionMatrices(
            IReadOnlyList<Matrix4x4> projections,
            IReadOnlyList<Matrix4x4>? views = null,
            int? farBoundsSourceCount = null,
            bool highSpeedMode = false)
            => SolveMinimalEnclosingFrustum(projections, views, farBoundsSourceCount, highSpeedMode: highSpeedMode).Projection;

        public static FrustumPointCloud BuildFrustumPointCloud(
            IReadOnlyList<Matrix4x4> projections,
            IReadOnlyList<Matrix4x4>? views = null,
            int? farBoundsSourceCount = null)
        {
            ArgumentNullException.ThrowIfNull(projections);
            if (projections.Count == 0)
                throw new ArgumentException("At least one projection matrix is required.", nameof(projections));

            views ??= CreateIdentityViews(projections.Count);
            if (views.Count != projections.Count)
                throw new ArgumentException("Projection and view counts must match.", nameof(views));

            int farSourceCount = farBoundsSourceCount ?? projections.Count;
            farSourceCount = Math.Clamp(farSourceCount, 0, projections.Count);

            List<Vector3> allPoints = new(projections.Count * 16);
            List<Vector3> farPoints = new(Math.Max(farSourceCount, 1) * 16);
            for (int i = 0; i < projections.Count; i++)
                AppendClippedFrustumPoints(allPoints, projections[i], views[i], i < farSourceCount ? farPoints : null);

            if (farPoints.Count == 0)
                farPoints.AddRange(allPoints);

            return new FrustumPointCloud([.. allPoints], [.. farPoints], projections.Count);
        }

        public static FrustumSolveResult SolveMinimalEnclosingFrustum(
            IReadOnlyList<Matrix4x4> projections,
            IReadOnlyList<Matrix4x4>? views = null,
            int? farBoundsSourceCount = null,
            bool solveViewOrientation = false,
            bool refineViewOrientation = true,
            bool highSpeedMode = false)
        {
            FrustumPointCloud pointCloud = BuildFrustumPointCloud(projections, views, farBoundsSourceCount);
            return SolveMinimalEnclosingFrustum(pointCloud, views, solveViewOrientation, refineViewOrientation, highSpeedMode);
        }

        public static FrustumSolveResult SolveMinimalEnclosingFrustum(
            FrustumPointCloud pointCloud,
            IReadOnlyList<Matrix4x4>? views = null,
            bool solveViewOrientation = false,
            bool refineViewOrientation = true,
            bool highSpeedMode = false)
        {
            views ??= CreateIdentityViews(pointCloud.SourceCount);
            if (views.Count != pointCloud.SourceCount)
                throw new ArgumentException("Projection and view counts must match.", nameof(views));

            bool effectiveRefineViewOrientation = refineViewOrientation && !highSpeedMode;
            Vector3[]? transformedAllPoints = highSpeedMode ? new Vector3[pointCloud.AllPoints.Length] : null;
            Vector3[]? transformedFarPoints = highSpeedMode ? new Vector3[pointCloud.FarPoints.Length] : null;

            OrientationCandidate bestCandidate = solveViewOrientation
                ? SolveBestCombinedViewOrientation(pointCloud.AllPoints, pointCloud.FarPoints, views, effectiveRefineViewOrientation, transformedAllPoints, transformedFarPoints)
                : new OrientationCandidate("Identity", Quaternion.Identity);

            Matrix4x4 bestView = CreateViewMatrix(bestCandidate.Orientation);
            float bestCost;
            Matrix4x4 bestProjection;

            if (highSpeedMode && transformedAllPoints is not null && transformedFarPoints is not null)
            {
                TransformPoints(pointCloud.AllPoints, bestView, transformedAllPoints);
                TransformPoints(pointCloud.FarPoints, bestView, transformedFarPoints);
                bestCost = EvaluateProjectionCost(transformedAllPoints, transformedFarPoints);
                bestProjection = CreateMinimalEnclosingProjection(transformedAllPoints, transformedFarPoints);
            }
            else
            {
                bestCost = EvaluateProjectionCost(pointCloud.AllPoints, pointCloud.FarPoints, bestView);
                bestProjection = CreateMinimalEnclosingProjection(pointCloud.AllPoints, pointCloud.FarPoints, bestView);
            }

            return new FrustumSolveResult(
                bestView,
                bestProjection,
                bestCandidate.Orientation,
                bestCandidate.Label,
                bestCost,
                bestCandidate.WasRefined);
        }

        private static IReadOnlyList<Matrix4x4> CreateIdentityViews(int count)
        {
            Matrix4x4[] views = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
                views[i] = Matrix4x4.Identity;
            return views;
        }

        private static Vector3[] GetFrustumCornersInReferenceSpace(Matrix4x4 proj, Matrix4x4 view)
        {
            Matrix4x4.Invert(view * proj, out Matrix4x4 invViewProj);

            Vector3[] frustumCorners = new Vector3[8];
            int index = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = 0; z <= 1; z++)
                    {
                        Vector4 ws = Vector4.Transform(new Vector4(x, y, z, 1.0f), invViewProj);
                        frustumCorners[index++] = ws.XYZ() / ws.W;
                    }

            return frustumCorners;
        }

        private static void AppendClippedFrustumPoints(List<Vector3> points, Matrix4x4 proj, Matrix4x4 view, List<Vector3>? farPoints)
        {
            Vector3[] corners = GetFrustumCornersInReferenceSpace(proj, view);

            foreach (Vector3 corner in corners)
            {
                if (IsInFrontOfEye(corner))
                {
                    points.Add(corner);
                    farPoints?.Add(corner);
                }
            }

            foreach ((int start, int end) in FrustumEdges)
            {
                Vector3 a = corners[start];
                Vector3 b = corners[end];
                bool aInFront = IsInFrontOfEye(a);
                bool bInFront = IsInFrontOfEye(b);
                if (aInFront == bInFront)
                    continue;

                float t = (-MinNearDistance - a.Z) / (b.Z - a.Z);
                Vector3 clippedPoint = Vector3.Lerp(a, b, t);
                points.Add(clippedPoint);
                farPoints?.Add(clippedPoint);
            }
        }

        private static bool IsInFrontOfEye(Vector3 point)
            => -point.Z >= MinNearDistance;

        private static OrientationCandidate SolveBestCombinedViewOrientation(
            IReadOnlyList<Vector3> allPoints,
            IReadOnlyList<Vector3> farPoints,
            IReadOnlyList<Matrix4x4> views,
            bool refineViewOrientation,
            Vector3[]? transformedAllPoints,
            Vector3[]? transformedFarPoints)
        {
            List<OrientationCandidate> candidates = new(views.Count + 2)
            {
                new("Identity", Quaternion.Identity),
            };

            Vector3 averageForward = Vector3.Zero;
            Vector3 averageUp = Vector3.Zero;
            for (int i = 0; i < views.Count; i++)
            {
                Matrix4x4 cameraTransform = GetCameraTransformInReferenceSpace(views[i]);
                Matrix4x4 rotationOnly = cameraTransform;
                rotationOnly.Translation = Vector3.Zero;
                Quaternion orientation = Quaternion.CreateFromRotationMatrix(rotationOnly);

                Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(Globals.Forward, rotationOnly));
                Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Globals.Up, rotationOnly));
                averageForward += forward;
                averageUp += up;

                candidates.Add(new($"Source {i}", orientation));
            }

            if (averageForward.LengthSquared() > float.Epsilon)
            {
                Quaternion averageRotation = XRMath.LookRotation(averageForward, averageUp);
                candidates.Add(new("Average", averageRotation));
            }

            OrientationCandidate bestCandidate = candidates[0];
            float bestCost = float.MaxValue;
            foreach (OrientationCandidate candidate in candidates)
            {
                float cost = EvaluateProjectionCost(allPoints, farPoints, CreateViewMatrix(candidate.Orientation), transformedAllPoints, transformedFarPoints);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestCandidate = candidate;
                }
            }

            if (!refineViewOrientation)
                return bestCandidate;

            OrientationCandidate refined = RefineCombinedViewOrientation(allPoints, farPoints, bestCandidate, bestCost, transformedAllPoints, transformedFarPoints);
            float refinedCost = EvaluateProjectionCost(allPoints, farPoints, CreateViewMatrix(refined.Orientation), transformedAllPoints, transformedFarPoints);
            return refinedCost < bestCost ? refined : bestCandidate;
        }

        private static OrientationCandidate RefineCombinedViewOrientation(
            IReadOnlyList<Vector3> allPoints,
            IReadOnlyList<Vector3> farPoints,
            OrientationCandidate initialCandidate,
            float initialCost,
            Vector3[]? transformedAllPoints,
            Vector3[]? transformedFarPoints)
        {
            OrientationCandidate bestCandidate = initialCandidate;
            float bestCost = initialCost;

            foreach (float stepDegreesValue in RefinementStepDegrees)
            {
                Quaternion currentOrientation = bestCandidate.Orientation;
                Vector3 worldUp = Vector3.Normalize(Vector3.Transform(Globals.Up, currentOrientation));
                Vector3 worldRight = Vector3.Normalize(Vector3.Transform(Globals.Right, currentOrientation));
                Vector3 worldForward = Vector3.Normalize(Vector3.Transform(Globals.Forward, currentOrientation));

                TryRefinementCandidate($"{initialCandidate.Label} refined yaw+", Quaternion.CreateFromAxisAngle(worldUp, XRMath.DegToRad(stepDegreesValue)) * currentOrientation);
                TryRefinementCandidate($"{initialCandidate.Label} refined yaw-", Quaternion.CreateFromAxisAngle(worldUp, XRMath.DegToRad(-stepDegreesValue)) * currentOrientation);
                TryRefinementCandidate($"{initialCandidate.Label} refined pitch+", Quaternion.CreateFromAxisAngle(worldRight, XRMath.DegToRad(stepDegreesValue)) * currentOrientation);
                TryRefinementCandidate($"{initialCandidate.Label} refined pitch-", Quaternion.CreateFromAxisAngle(worldRight, XRMath.DegToRad(-stepDegreesValue)) * currentOrientation);
                TryRefinementCandidate($"{initialCandidate.Label} refined roll+", Quaternion.CreateFromAxisAngle(worldForward, XRMath.DegToRad(stepDegreesValue)) * currentOrientation);
                TryRefinementCandidate($"{initialCandidate.Label} refined roll-", Quaternion.CreateFromAxisAngle(worldForward, XRMath.DegToRad(-stepDegreesValue)) * currentOrientation);
            }

            return bestCandidate;

            void TryRefinementCandidate(string label, Quaternion orientation)
            {
                OrientationCandidate candidate = new(label, orientation, true);
                float cost = EvaluateProjectionCost(allPoints, farPoints, CreateViewMatrix(candidate.Orientation), transformedAllPoints, transformedFarPoints);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestCandidate = candidate;
                }
            }
        }

        private static Matrix4x4 CreateViewMatrix(Quaternion orientation)
            => Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(orientation));

        private static Matrix4x4 GetCameraTransformInReferenceSpace(Matrix4x4 view)
            => Matrix4x4.Invert(view, out Matrix4x4 transform) ? transform : Matrix4x4.Identity;

        private static float EvaluateProjectionCost(IReadOnlyList<Vector3> worldPoints, IReadOnlyList<Vector3> farPoints, Matrix4x4 view)
        {
            foreach (Vector3 point in worldPoints)
            {
                if (-Vector3.Transform(point, view).Z < MinNearDistance)
                    return float.MaxValue;
            }

            CalculateProjectedBounds(worldPoints, farPoints, view,
                out float left, out float right,
                out float bottom, out float top,
                out float nearDist, out float farDist);

            float width = MathF.Max(right - left, MinNearDistance);
            float height = MathF.Max(top - bottom, MinNearDistance);
            float nearSquared = MathF.Max(nearDist * nearDist, MinNearDistance);
            float depthTerm = MathF.Max((farDist * farDist * farDist) - (nearDist * nearDist * nearDist), MinNearDistance);
            return width * height * depthTerm / (3.0f * nearSquared);
        }

        private static float EvaluateProjectionCost(
            IReadOnlyList<Vector3> worldPoints,
            IReadOnlyList<Vector3> farPoints,
            Matrix4x4 view,
            Vector3[]? transformedWorldPoints,
            Vector3[]? transformedFarPoints)
        {
            if (transformedWorldPoints is null || transformedFarPoints is null)
                return EvaluateProjectionCost(worldPoints, farPoints, view);

            TransformPoints(worldPoints, view, transformedWorldPoints);
            TransformPoints(farPoints, view, transformedFarPoints);
            return EvaluateProjectionCost(transformedWorldPoints, transformedFarPoints);
        }

        private static float EvaluateProjectionCost(ReadOnlySpan<Vector3> viewSpacePoints, ReadOnlySpan<Vector3> farBoundPoints)
        {
            foreach (Vector3 point in viewSpacePoints)
            {
                if (-point.Z < MinNearDistance)
                    return float.MaxValue;
            }

            CalculateProjectedBounds(viewSpacePoints, farBoundPoints,
                out float left, out float right,
                out float bottom, out float top,
                out float nearDist, out float farDist);

            float width = MathF.Max(right - left, MinNearDistance);
            float height = MathF.Max(top - bottom, MinNearDistance);
            float nearSquared = MathF.Max(nearDist * nearDist, MinNearDistance);
            float depthTerm = MathF.Max((farDist * farDist * farDist) - (nearDist * nearDist * nearDist), MinNearDistance);
            return width * height * depthTerm / (3.0f * nearSquared);
        }

        private static Matrix4x4 CreateMinimalEnclosingProjection(IReadOnlyList<Vector3> worldPoints, IReadOnlyList<Vector3> farPoints, Matrix4x4 view)
        {
            CalculateProjectedBounds(worldPoints, farPoints, view,
                out float left, out float right,
                out float bottom, out float top,
                out float nearDist, out float farDist);

            return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, nearDist, farDist);
        }

        private static Matrix4x4 CreateMinimalEnclosingProjection(ReadOnlySpan<Vector3> viewSpacePoints, ReadOnlySpan<Vector3> farBoundPoints)
        {
            CalculateProjectedBounds(viewSpacePoints, farBoundPoints,
                out float left, out float right,
                out float bottom, out float top,
                out float nearDist, out float farDist);

            return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, nearDist, farDist);
        }

        private static void TransformPoints(IReadOnlyList<Vector3> sourcePoints, Matrix4x4 view, Span<Vector3> transformedPoints)
        {
            for (int i = 0; i < sourcePoints.Count; i++)
                transformedPoints[i] = Vector3.Transform(sourcePoints[i], view);
        }

        private static void CalculateProjectedBounds(
            IReadOnlyList<Vector3> viewSpacePoints,
            IReadOnlyList<Vector3> farBoundPoints,
            Matrix4x4 view,
            out float nearPlaneLeft, out float nearPlaneRight,
            out float nearPlaneBottom, out float nearPlaneTop,
            out float nearDist, out float farDist)
        {
            nearDist = float.MaxValue;
            farDist = MinNearDistance;
            nearPlaneLeft = float.MaxValue;
            nearPlaneRight = float.MinValue;
            nearPlaneBottom = float.MaxValue;
            nearPlaneTop = float.MinValue;

            foreach (var point in viewSpacePoints)
            {
                Vector3 viewPoint = Vector3.Transform(point, view);
                float depth = MathF.Max(-viewPoint.Z, MinNearDistance);
                nearDist = MathF.Min(nearDist, depth);
            }

            foreach (var point in farBoundPoints)
            {
                Vector3 viewPoint = Vector3.Transform(point, view);
                float depth = MathF.Max(-viewPoint.Z, MinNearDistance);
                farDist = MathF.Max(farDist, depth);
            }

            if (nearDist == float.MaxValue)
                nearDist = MinNearDistance;

            foreach (Vector3 point in viewSpacePoints)
            {
                Vector3 viewPoint = Vector3.Transform(point, view);
                float depth = MathF.Max(-viewPoint.Z, MinNearDistance);
                float scale = nearDist / depth;
                float projectedX = viewPoint.X * scale;
                float projectedY = viewPoint.Y * scale;

                nearPlaneLeft = MathF.Min(nearPlaneLeft, projectedX);
                nearPlaneRight = MathF.Max(nearPlaneRight, projectedX);
                nearPlaneBottom = MathF.Min(nearPlaneBottom, projectedY);
                nearPlaneTop = MathF.Max(nearPlaneTop, projectedY);
            }

            if (nearPlaneLeft == float.MaxValue || nearPlaneRight == float.MinValue ||
                nearPlaneBottom == float.MaxValue || nearPlaneTop == float.MinValue)
            {
                float halfExtent = nearDist;
                nearPlaneLeft = -halfExtent;
                nearPlaneRight = halfExtent;
                nearPlaneBottom = -halfExtent;
                nearPlaneTop = halfExtent;
            }

            if (nearPlaneRight <= nearPlaneLeft)
                nearPlaneRight = nearPlaneLeft + MinNearDistance;
            if (nearPlaneTop <= nearPlaneBottom)
                nearPlaneTop = nearPlaneBottom + MinNearDistance;
            if (farDist <= nearDist)
                farDist = nearDist + MinNearDistance;
        }

        private static void CalculateProjectedBounds(
            ReadOnlySpan<Vector3> viewSpacePoints,
            ReadOnlySpan<Vector3> farBoundPoints,
            out float nearPlaneLeft, out float nearPlaneRight,
            out float nearPlaneBottom, out float nearPlaneTop,
            out float nearDist, out float farDist)
        {
            nearDist = float.MaxValue;
            farDist = MinNearDistance;
            nearPlaneLeft = float.MaxValue;
            nearPlaneRight = float.MinValue;
            nearPlaneBottom = float.MaxValue;
            nearPlaneTop = float.MinValue;

            foreach (Vector3 point in viewSpacePoints)
            {
                float depth = MathF.Max(-point.Z, MinNearDistance);
                nearDist = MathF.Min(nearDist, depth);
            }

            foreach (Vector3 point in farBoundPoints)
            {
                float depth = MathF.Max(-point.Z, MinNearDistance);
                farDist = MathF.Max(farDist, depth);
            }

            if (nearDist == float.MaxValue)
                nearDist = MinNearDistance;

            foreach (Vector3 point in viewSpacePoints)
            {
                float depth = MathF.Max(-point.Z, MinNearDistance);
                float scale = nearDist / depth;
                float projectedX = point.X * scale;
                float projectedY = point.Y * scale;

                nearPlaneLeft = MathF.Min(nearPlaneLeft, projectedX);
                nearPlaneRight = MathF.Max(nearPlaneRight, projectedX);
                nearPlaneBottom = MathF.Min(nearPlaneBottom, projectedY);
                nearPlaneTop = MathF.Max(nearPlaneTop, projectedY);
            }

            if (nearPlaneLeft == float.MaxValue || nearPlaneRight == float.MinValue ||
                nearPlaneBottom == float.MaxValue || nearPlaneTop == float.MinValue)
            {
                float halfExtent = nearDist;
                nearPlaneLeft = -halfExtent;
                nearPlaneRight = halfExtent;
                nearPlaneBottom = -halfExtent;
                nearPlaneTop = halfExtent;
            }

            if (nearPlaneRight <= nearPlaneLeft)
                nearPlaneRight = nearPlaneLeft + MinNearDistance;
            if (nearPlaneTop <= nearPlaneBottom)
                nearPlaneTop = nearPlaneBottom + MinNearDistance;
            if (farDist <= nearDist)
                farDist = nearDist + MinNearDistance;
        }
    }
}