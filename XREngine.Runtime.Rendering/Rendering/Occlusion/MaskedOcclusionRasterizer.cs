using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Occlusion
{
    internal sealed class MaskedOcclusionRasterizer
    {
        private const float ClipEpsilon = 1e-5f;

        public int RasterizeMesh(
            MaskedOcclusionBuffer buffer,
            XRMesh mesh,
            in Matrix4x4 modelMatrix,
            in Matrix4x4 viewProjectionMatrix,
            RenderingParameters? renderOptions,
            int triangleBudget)
        {
            if (triangleBudget <= 0 || mesh.Type != EPrimitiveType.Triangles || mesh.Triangles is not { Count: > 0 } triangles)
                return 0;

            Matrix4x4 modelViewProjection = modelMatrix * viewProjectionMatrix;
            int rasterized = 0;
            int vertexCount = mesh.VertexCount;
            Vertex[] vertices = mesh.Vertices;
            for (int i = 0; i < triangles.Count && rasterized < triangleBudget; i++)
            {
                IndexTriangle triangle = triangles[i];
                int i0 = triangle.Point0;
                int i1 = triangle.Point1;
                int i2 = triangle.Point2;
                if ((uint)i0 >= (uint)vertexCount || (uint)i1 >= (uint)vertexCount || (uint)i2 >= (uint)vertexCount)
                    continue;

                Vector3 p0 = vertices.Length > i0 ? vertices[i0].Position : mesh.GetPosition((uint)i0);
                Vector3 p1 = vertices.Length > i1 ? vertices[i1].Position : mesh.GetPosition((uint)i1);
                Vector3 p2 = vertices.Length > i2 ? vertices[i2].Position : mesh.GetPosition((uint)i2);

                if (RasterizeTriangle(buffer, p0, p1, p2, modelViewProjection, renderOptions))
                    rasterized++;
            }

            return rasterized;
        }

        private static bool RasterizeTriangle(
            MaskedOcclusionBuffer buffer,
            in Vector3 p0,
            in Vector3 p1,
            in Vector3 p2,
            in Matrix4x4 modelViewProjection,
            RenderingParameters? renderOptions)
        {
            Vector4 c0 = Vector4.Transform(new Vector4(p0, 1.0f), modelViewProjection);
            Vector4 c1 = Vector4.Transform(new Vector4(p1, 1.0f), modelViewProjection);
            Vector4 c2 = Vector4.Transform(new Vector4(p2, 1.0f), modelViewProjection);

            if (!IsFinite(c0) || !IsFinite(c1) || !IsFinite(c2))
                return false;

            // Near-plane clipping is intentionally conservative for the first scalar path:
            // skip near-straddling occluder triangles instead of risking over-occlusion.
            if (c0.W <= ClipEpsilon || c1.W <= ClipEpsilon || c2.W <= ClipEpsilon)
                return false;

            if (AllOutsideNegative(c0.X, c0.W, c1.X, c1.W, c2.X, c2.W) ||
                AllOutsidePositive(c0.X, c0.W, c1.X, c1.W, c2.X, c2.W) ||
                AllOutsideNegative(c0.Y, c0.W, c1.Y, c1.W, c2.Y, c2.W) ||
                AllOutsidePositive(c0.Y, c0.W, c1.Y, c1.W, c2.Y, c2.W) ||
                AllOutsideNegative(c0.Z, c0.W, c1.Z, c1.W, c2.Z, c2.W) ||
                AllOutsidePositive(c0.Z, c0.W, c1.Z, c1.W, c2.Z, c2.W))
            {
                return false;
            }

            ScreenVertex v0 = ToScreen(c0, buffer.Width, buffer.Height);
            ScreenVertex v1 = ToScreen(c1, buffer.Width, buffer.Height);
            ScreenVertex v2 = ToScreen(c2, buffer.Width, buffer.Height);

            float area = SignedArea(v0.Position, v1.Position, v2.Position);
            if (MathF.Abs(area) <= 1e-6f)
                return false;

            if (IsCulled(area, renderOptions))
                return false;

            int minX = Math.Clamp((int)MathF.Floor(MathF.Min(v0.Position.X, MathF.Min(v1.Position.X, v2.Position.X))), 0, buffer.Width - 1);
            int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(v0.Position.X, MathF.Max(v1.Position.X, v2.Position.X))), 0, buffer.Width - 1);
            int minY = Math.Clamp((int)MathF.Floor(MathF.Min(v0.Position.Y, MathF.Min(v1.Position.Y, v2.Position.Y))), 0, buffer.Height - 1);
            int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(v0.Position.Y, MathF.Max(v1.Position.Y, v2.Position.Y))), 0, buffer.Height - 1);
            if (minX > maxX || minY > maxY)
                return false;

            bool wroteAny = false;
            float invArea = 1.0f / area;
            bool positive = area > 0.0f;

            float edge0X = v1.Position.Y - v2.Position.Y;
            float edge0Y = v2.Position.X - v1.Position.X;
            float edge0C = v1.Position.X * v2.Position.Y - v2.Position.X * v1.Position.Y;
            float edge1X = v2.Position.Y - v0.Position.Y;
            float edge1Y = v0.Position.X - v2.Position.X;
            float edge1C = v2.Position.X * v0.Position.Y - v0.Position.X * v2.Position.Y;
            float edge2X = v0.Position.Y - v1.Position.Y;
            float edge2Y = v1.Position.X - v0.Position.X;
            float edge2C = v0.Position.X * v1.Position.Y - v1.Position.X * v0.Position.Y;

            float depthDx = (edge0X * v0.ReciprocalW + edge1X * v1.ReciprocalW + edge2X * v2.ReciprocalW) * invArea;
            float depthDy = (edge0Y * v0.ReciprocalW + edge1Y * v1.ReciprocalW + edge2Y * v2.ReciprocalW) * invArea;
            float depthC = (edge0C * v0.ReciprocalW + edge1C * v1.ReciprocalW + edge2C * v2.ReciprocalW) * invArea;

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                float px = minX + 0.5f;
                float w0 = edge0X * px + edge0Y * py + edge0C;
                float w1 = edge1X * px + edge1Y * py + edge1C;
                float w2 = edge2X * px + edge2Y * py + edge2C;
                float reciprocalDepth = depthDx * px + depthDy * py + depthC;

                for (int x = minX; x <= maxX; x++)
                {
                    if (positive)
                    {
                        if (w0 >= 0.0f && w1 >= 0.0f && w2 >= 0.0f)
                        {
                            buffer.WritePixelUnchecked(x, y, reciprocalDepth);
                            wroteAny = true;
                        }
                    }
                    else
                    {
                        if (w0 <= 0.0f && w1 <= 0.0f && w2 <= 0.0f)
                        {
                            buffer.WritePixelUnchecked(x, y, reciprocalDepth);
                            wroteAny = true;
                        }
                    }

                    w0 += edge0X;
                    w1 += edge1X;
                    w2 += edge2X;
                    reciprocalDepth += depthDx;
                }
            }

            return wroteAny;
        }

        internal static float ComputeOccluderScore(float normalizedScreenArea, int triangleCount)
        {
            if (normalizedScreenArea <= 0.0f || triangleCount <= 0)
                return 0.0f;

            return normalizedScreenArea / MathF.Sqrt(triangleCount);
        }

        private static bool IsCulled(float signedScreenArea, RenderingParameters? renderOptions)
        {
            ECullMode cullMode = renderOptions?.CullMode ?? ECullMode.Back;
            if (cullMode == ECullMode.None)
                return false;
            if (cullMode == ECullMode.Both)
                return true;

            bool screenCounterClockwise = signedScreenArea < 0.0f;
            bool frontFacing = (renderOptions?.Winding ?? EWinding.CounterClockwise) == EWinding.CounterClockwise
                ? screenCounterClockwise
                : !screenCounterClockwise;

            return cullMode == ECullMode.Back ? !frontFacing : frontFacing;
        }

        private static ScreenVertex ToScreen(in Vector4 clip, int width, int height)
        {
            float invW = 1.0f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            return new ScreenVertex(
                new Vector2(
                    (ndcX * 0.5f + 0.5f) * width,
                    (1.0f - (ndcY * 0.5f + 0.5f)) * height),
                invW);
        }

        private static float SignedArea(in Vector2 a, in Vector2 b, in Vector2 c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static bool AllOutsideNegative(float a, float aw, float b, float bw, float c, float cw)
            => a < -aw && b < -bw && c < -cw;

        private static bool AllOutsidePositive(float a, float aw, float b, float bw, float c, float cw)
            => a > aw && b > bw && c > cw;

        private static bool IsFinite(in Vector4 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

        private readonly struct ScreenVertex(Vector2 position, float reciprocalW)
        {
            public readonly Vector2 Position = position;
            public readonly float ReciprocalW = reciprocalW;
        }
    }
}
