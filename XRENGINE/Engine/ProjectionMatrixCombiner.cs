using Extensions;
using System.Numerics;

namespace XREngine
{
    /// <summary>
    /// Combines two projection matrices into a single projection matrix that can encompass both frustums.
    /// Ignores a lot of edge cases, but works for what we need.
    /// </summary>
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
                for (int y = -1; y <= 1; y += 2)
                    for (int z = 0; z <= 1; z++)
                    {
                        Vector4 ws = Vector4.Transform(new Vector4(x, y, z, 1.0f), invViewProj);
                        frustumCorners[index++] = ws.XYZ() / ws.W;
                    }

            return frustumCorners;
        }

        private static Matrix4x4 CreateMinimalEnclosingProjection(List<Vector3> worldPoints)
        {
            CalculateViewSpaceBounds(worldPoints,
                out float left, out float right,
                out float bottom, out float top,
                out float near, out float far);

            // 8. Create the enclosing projection matrix
            return Matrix4x4.CreatePerspectiveOffCenter(left, right, bottom, top, -near, -far);
        }

        private static void CalculateViewSpaceBounds(
            List<Vector3> viewSpacePoints,
            out float nearPlaneLeft, out float nearPlaneRight,
            out float nearPlaneBottom, out float nearPlaneTop,
            out float near, out float far)
        {
            //Near is a small negative value, far is a large negative value
            near = float.MinValue;
            far = float.MaxValue;
            nearPlaneLeft = float.MaxValue;
            nearPlaneRight = float.MinValue;
            nearPlaneBottom = float.MaxValue;
            nearPlaneTop = float.MinValue;

            foreach (var point in viewSpacePoints)
            {
                near = MathF.Max(near, point.Z);
                far = MathF.Min(far, point.Z);
            }

            //Only consider points that are closer to the near plane
            float halfNearFar = (far - near) * 0.5f;
            foreach (Vector3 point in viewSpacePoints)
            {
                if (point.Z > halfNearFar)
                {
                    nearPlaneLeft = MathF.Min(nearPlaneLeft, point.X);
                    nearPlaneRight = MathF.Max(nearPlaneRight, point.X);
                    nearPlaneBottom = MathF.Min(nearPlaneBottom, point.Y);
                    nearPlaneTop = MathF.Max(nearPlaneTop, point.Y);
                }
            }
        }
    }
}