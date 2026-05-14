using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Occlusion
{
    internal sealed class MaskedOcclusionAabbTester
    {
        private const float ClipEpsilon = 1e-5f;

        public bool TestVisible(MaskedOcclusionBuffer buffer, in Matrix4x4 viewProjectionMatrix, in AABB worldBounds)
        {
            if (!TryProjectAabb(
                worldBounds,
                viewProjectionMatrix,
                buffer.Width,
                buffer.Height,
                out ProjectedAabb projected))
            {
                return true;
            }

            if (projected.OutsideFrustum)
                return false;

            return !buffer.IsRectOccluded(
                projected.MinX,
                projected.MinY,
                projected.MaxXExclusive,
                projected.MaxYExclusive,
                projected.NearestReciprocalW);
        }

        public static bool TryProjectAabb(
            in AABB worldBounds,
            in Matrix4x4 viewProjectionMatrix,
            int width,
            int height,
            out ProjectedAabb projected)
        {
            projected = default;
            if (!worldBounds.IsValid || width <= 0 || height <= 0)
                return false;

            Vector3 min = worldBounds.Min;
            Vector3 max = worldBounds.Max;
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = new Vector3(min.X, min.Y, min.Z);
            corners[1] = new Vector3(max.X, min.Y, min.Z);
            corners[2] = new Vector3(min.X, max.Y, min.Z);
            corners[3] = new Vector3(max.X, max.Y, min.Z);
            corners[4] = new Vector3(min.X, min.Y, max.Z);
            corners[5] = new Vector3(max.X, min.Y, max.Z);
            corners[6] = new Vector3(min.X, max.Y, max.Z);
            corners[7] = new Vector3(max.X, max.Y, max.Z);

            bool allLeft = true;
            bool allRight = true;
            bool allBelow = true;
            bool allAbove = true;
            bool allNear = true;
            bool allFar = true;
            float minScreenX = float.PositiveInfinity;
            float minScreenY = float.PositiveInfinity;
            float maxScreenX = float.NegativeInfinity;
            float maxScreenY = float.NegativeInfinity;
            float nearestReciprocalW = float.NegativeInfinity;

            for (int i = 0; i < corners.Length; i++)
            {
                Vector4 clip = Vector4.Transform(new Vector4(corners[i], 1.0f), viewProjectionMatrix);
                if (!IsFinite(clip) || clip.W <= ClipEpsilon)
                    return false;

                allLeft &= clip.X < -clip.W;
                allRight &= clip.X > clip.W;
                allBelow &= clip.Y < -clip.W;
                allAbove &= clip.Y > clip.W;
                allNear &= clip.Z < -clip.W;
                allFar &= clip.Z > clip.W;

                float reciprocalW = 1.0f / clip.W;
                nearestReciprocalW = MathF.Max(nearestReciprocalW, reciprocalW);
                float ndcX = clip.X * reciprocalW;
                float ndcY = clip.Y * reciprocalW;
                float screenX = (ndcX * 0.5f + 0.5f) * width;
                float screenY = (1.0f - (ndcY * 0.5f + 0.5f)) * height;

                minScreenX = MathF.Min(minScreenX, screenX);
                minScreenY = MathF.Min(minScreenY, screenY);
                maxScreenX = MathF.Max(maxScreenX, screenX);
                maxScreenY = MathF.Max(maxScreenY, screenY);
            }

            if (allLeft || allRight || allBelow || allAbove || allNear || allFar)
            {
                projected = ProjectedAabb.Outside;
                return true;
            }

            int minX = Math.Clamp((int)MathF.Floor(minScreenX), 0, width);
            int minY = Math.Clamp((int)MathF.Floor(minScreenY), 0, height);
            int maxXExclusive = Math.Clamp((int)MathF.Ceiling(maxScreenX), 0, width);
            int maxYExclusive = Math.Clamp((int)MathF.Ceiling(maxScreenY), 0, height);
            if (minX >= maxXExclusive || minY >= maxYExclusive)
            {
                projected = ProjectedAabb.Outside;
                return true;
            }

            projected = new ProjectedAabb(
                minX,
                minY,
                maxXExclusive,
                maxYExclusive,
                nearestReciprocalW,
                outsideFrustum: false);
            return true;
        }

        private static bool IsFinite(in Vector4 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    internal readonly struct ProjectedAabb(
        int minX,
        int minY,
        int maxXExclusive,
        int maxYExclusive,
        float nearestReciprocalW,
        bool outsideFrustum)
    {
        public static ProjectedAabb Outside => new(0, 0, 0, 0, 0.0f, true);

        public readonly int MinX = minX;
        public readonly int MinY = minY;
        public readonly int MaxXExclusive = maxXExclusive;
        public readonly int MaxYExclusive = maxYExclusive;
        public readonly float NearestReciprocalW = nearestReciprocalW;
        public readonly bool OutsideFrustum = outsideFrustum;

        public readonly float NormalizedArea(int width, int height)
        {
            int rectWidth = Math.Max(0, MaxXExclusive - MinX);
            int rectHeight = Math.Max(0, MaxYExclusive - MinY);
            return width > 0 && height > 0 ? (rectWidth * rectHeight) / (float)(width * height) : 0.0f;
        }
    }
}
