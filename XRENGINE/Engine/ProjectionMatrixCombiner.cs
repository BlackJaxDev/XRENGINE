using System.Numerics;

namespace XREngine
{
    public static class ProjectionMatrixCombiner
    {
        public static Matrix4x4 CombineProjectionMatrices(Matrix4x4 proj1, Matrix4x4 proj2, Matrix4x4? view1 = null, Matrix4x4? view2 = null)
        {
            // Default to identity view matrices if not provided
            view1 ??= Matrix4x4.Identity;
            view2 ??= Matrix4x4.Identity;

            // Get all frustum corners in world space
            var corners1 = GetFrustumCornersWorldSpace(proj1, view1.Value);
            var corners2 = GetFrustumCornersWorldSpace(proj2, view2.Value);

            // Combine all corners
            var allCorners = new List<Vector3>(corners1);
            allCorners.AddRange(corners2);

            // Calculate the optimal projection for the combined set of points
            return CreateMinimalEnclosingProjection(allCorners);
        }

        private static Vector3[] GetFrustumCornersWorldSpace(Matrix4x4 proj, Matrix4x4 view)
        {
            // Inverse view-projection matrix to go from clip space to world space
            Matrix4x4.Invert(view * proj, out Matrix4x4 invViewProj);

            Vector3[] frustumCorners = new Vector3[8];

            // Iterate over all combinations of clip space coordinates
            int index = 0;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        // Create clip space coordinate
                        Vector4 clipSpacePos = new(x, y, z, 1.0f);

                        // Transform to world space
                        Vector4 worldSpacePos = Vector4.Transform(clipSpacePos, invViewProj);

                        // Perspective divide
                        frustumCorners[index++] = new Vector3(
                            worldSpacePos.X / worldSpacePos.W,
                            worldSpacePos.Y / worldSpacePos.W,
                            worldSpacePos.Z / worldSpacePos.W);
                    }
                }
            }

            return frustumCorners;
        }

        private static Matrix4x4 CreateMinimalEnclosingProjection(List<Vector3> worldPoints)
        {
            // We need to find a view matrix that looks at the combined frustum
            // For simplicity, we'll use the centroid as the look-at point
            Vector3 centroid = CalculateCentroid(worldPoints);

            // Calculate optimal camera position and orientation
            // This is simplified - a real implementation might want to find the minimal enclosing frustum
            Vector3 cameraPos = centroid - Vector3.UnitZ * CalculateOptimalDistance(worldPoints, centroid);

            // Create view matrix looking at the centroid
            Matrix4x4 viewMatrix = Matrix4x4.CreateLookAt(cameraPos, centroid, Vector3.UnitY);

            // Transform all points to view space
            List<Vector3> viewSpacePoints = [];
            foreach (var point in worldPoints)
                viewSpacePoints.Add(Vector3.Transform(point, viewMatrix));
            
            // Calculate the bounds in view space
            CalculateViewSpaceBounds(viewSpacePoints, out float left, out float right,
                                   out float bottom, out float top, out float near, out float far);

            // Create the enclosing projection matrix
            return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, near, far);
        }

        private static Vector3 CalculateCentroid(List<Vector3> points)
        {
            Vector3 sum = Vector3.Zero;
            foreach (var point in points)
                sum += point;
            return sum / points.Count;
        }

        private static float CalculateOptimalDistance(List<Vector3> points, Vector3 centroid)
        {
            // Find the maximum distance from centroid to any point
            float maxDistance = 0;
            foreach (var point in points)
            {
                float distance = Vector3.Distance(point, centroid);
                if (distance > maxDistance)
                    maxDistance = distance;
            }
            return maxDistance * 1.5f; // Add some padding
        }

        private static void CalculateViewSpaceBounds(List<Vector3> viewSpacePoints,
            out float left, out float right, out float bottom, out float top,
            out float near, out float far)
        {
            // Initialize with extreme values
            left = float.MaxValue;
            right = float.MinValue;
            bottom = float.MaxValue;
            top = float.MinValue;
            near = float.MaxValue;
            far = float.MinValue;

            foreach (var point in viewSpacePoints)
            {
                // Project point onto near plane (z = -near)
                float z = -point.Z;
                float x = point.X / z;
                float y = point.Y / z;

                // Update bounds
                left = MathF.Min(left, x);
                right = MathF.Max(right, x);
                bottom = MathF.Min(bottom, y);
                top = MathF.Max(top, y);
                near = MathF.Min(near, -point.Z); // View space Z is negative
                far = MathF.Max(far, -point.Z);
            }

            // Add small padding to avoid clipping
            float padding = 0.1f;
            left -= padding;
            right += padding;
            bottom -= padding;
            top += padding;
            near = MathF.Max(0.1f, near - padding);
            far += padding;
        }
    }
}