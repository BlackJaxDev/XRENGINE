using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;

namespace XREngine.Components.Scene.Mesh
{
    public partial class RenderableMesh
    {
        #region Imported texture streaming

        /// <summary>
        /// Texture residency should only be updated from the main scene pass; shadow and probe passes
        /// do not describe what the player can actually inspect on screen.
        /// </summary>
        internal static bool ShouldRecordImportedTextureStreamingUsage(bool isShadowPass, bool isMainPass)
            => !isShadowPass && isMainPass;

        private ImportedTextureStreamingUsage BuildImportedTextureStreamingUsage(XRMesh? mesh, XRCamera? camera, float distanceFromCamera)
        {
            MeshTextureStreamingMetrics metrics = MeshTextureStreamingMetricsCache.Get(mesh);
            float projectedPixelSpan = 0.0f;
            float screenCoverage = 0.0f;
            if (camera is not null && TryGetCurrentStreamingBounds(out AABB worldBounds))
                CalculateTextureStreamingScreenMetrics(camera, worldBounds, distanceFromCamera, out projectedPixelSpan, out screenCoverage);

            return new ImportedTextureStreamingUsage(
                distanceFromCamera,
                projectedPixelSpan,
                screenCoverage,
                metrics.UvDensityHint,
                metrics.PageSelection);
        }

        private bool TryGetCurrentStreamingBounds(out AABB worldBounds)
        {
            worldBounds = default;
            RenderInfo3D renderInfo = RenderInfo;
            AABB localBounds = renderInfo.LocalCullingVolume ?? _bindPoseBounds;
            if (!localBounds.IsValid)
                return TryGetWorldBounds(out worldBounds);

            worldBounds = TransformBounds(localBounds, renderInfo.CullingOffsetMatrix);
            return worldBounds.IsValid;
        }

        private static void CalculateTextureStreamingScreenMetrics(XRCamera camera, AABB worldBounds, float distanceFromCamera, out float projectedPixelSpan, out float screenCoverage)
        {
            projectedPixelSpan = 0.0f;
            screenCoverage = 0.0f;

            float distance = MathF.Max(distanceFromCamera, camera.Parameters.NearZ + 0.001f);
            Vector2 frustumSize = camera.Parameters.GetFrustumSizeAtDistance(distance);
            if (!(frustumSize.X > 0.0f) || !(frustumSize.Y > 0.0f))
                return;

            float diameter = MathF.Max(0.001f, worldBounds.HalfExtents.Length() * 2.0f);
            for (int viewportIndex = 0; viewportIndex < camera.Viewports.Count; viewportIndex++)
            {
                XRViewport viewport = camera.Viewports[viewportIndex];
                int viewportWidth = Math.Max(1, viewport.InternalWidth > 0 ? viewport.InternalWidth : viewport.Width);
                int viewportHeight = Math.Max(1, viewport.InternalHeight > 0 ? viewport.InternalHeight : viewport.Height);

                float projectedWidth = Math.Clamp(diameter / frustumSize.X * viewportWidth, 0.0f, viewportWidth);
                float projectedHeight = Math.Clamp(diameter / frustumSize.Y * viewportHeight, 0.0f, viewportHeight);
                projectedPixelSpan = MathF.Max(projectedPixelSpan, MathF.Max(projectedWidth, projectedHeight));
                screenCoverage = MathF.Max(screenCoverage, Math.Clamp((projectedWidth * projectedHeight) / (viewportWidth * viewportHeight), 0.0f, 1.0f));
            }
        }

        private sealed class MeshTextureStreamingMetrics(float uvDensityHint, SparseTextureStreamingPageSelection pageSelection)
        {
            public static MeshTextureStreamingMetrics Default { get; } = new(1.0f, SparseTextureStreamingPageSelection.Full);

            public float UvDensityHint { get; } = uvDensityHint;
            public SparseTextureStreamingPageSelection PageSelection { get; } = pageSelection.Normalize();
        }

        private static class MeshTextureStreamingMetricsCache
        {
            private static readonly ConditionalWeakTable<XRMesh, MeshTextureStreamingMetrics> Cache = new();

            public static MeshTextureStreamingMetrics Get(XRMesh? mesh)
                => mesh is null ? MeshTextureStreamingMetrics.Default : Cache.GetValue(mesh, static value => Create(value));

            private static MeshTextureStreamingMetrics Create(XRMesh mesh)
            {
                Vertex[] vertices = mesh.Vertices;
                if (vertices.Length == 0)
                    return MeshTextureStreamingMetrics.Default;

                bool hasUvBounds = false;
                bool uvOutOfRange = false;
                Vector2 minUv = new(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 maxUv = new(float.NegativeInfinity, float.NegativeInfinity);
                for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    if (!TryGetPrimaryTexCoord(vertices[vertexIndex], out Vector2 uv))
                        continue;

                    hasUvBounds = true;
                    if (uv.X < -0.01f || uv.X > 1.01f || uv.Y < -0.01f || uv.Y > 1.01f)
                    {
                        uvOutOfRange = true;
                        break;
                    }

                    minUv = Vector2.Min(minUv, uv);
                    maxUv = Vector2.Max(maxUv, uv);
                }

                SparseTextureStreamingPageSelection pageSelection = hasUvBounds && !uvOutOfRange
                    ? SparseTextureStreamingPageSelection.Partial(minUv.X, minUv.Y, maxUv.X, maxUv.Y).Normalize()
                    : SparseTextureStreamingPageSelection.Full;

                float surfaceArea = 0.0f;
                float uvArea = 0.0f;
                if (mesh.Triangles is { Count: > 0 } triangles)
                {
                    for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
                    {
                        IndexTriangle triangle = triangles[triangleIndex];
                        if (triangle.Point0 < 0 || triangle.Point1 < 0 || triangle.Point2 < 0
                            || triangle.Point0 >= vertices.Length || triangle.Point1 >= vertices.Length || triangle.Point2 >= vertices.Length)
                        {
                            continue;
                        }

                        Vertex v0 = vertices[triangle.Point0];
                        Vertex v1 = vertices[triangle.Point1];
                        Vertex v2 = vertices[triangle.Point2];
                        if (!TryGetPrimaryTexCoord(v0, out Vector2 uv0)
                            || !TryGetPrimaryTexCoord(v1, out Vector2 uv1)
                            || !TryGetPrimaryTexCoord(v2, out Vector2 uv2))
                        {
                            continue;
                        }

                        float triangleSurfaceArea = Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position).Length() * 0.5f;
                        float triangleUvArea = MathF.Abs((uv1.X - uv0.X) * (uv2.Y - uv0.Y) - (uv1.Y - uv0.Y) * (uv2.X - uv0.X)) * 0.5f;
                        if (!float.IsFinite(triangleSurfaceArea) || !float.IsFinite(triangleUvArea) || triangleSurfaceArea <= 1.0e-6f || triangleUvArea <= 1.0e-6f)
                            continue;

                        surfaceArea += triangleSurfaceArea;
                        uvArea += triangleUvArea;
                    }
                }

                float uvDensityHint = 1.0f;
                if (surfaceArea > 1.0e-6f && uvArea > 1.0e-6f)
                {
                    Vector3 halfExtents = mesh.Bounds.HalfExtents;
                    float maxExtent = MathF.Max(1.0e-3f, MathF.Max(halfExtents.X, MathF.Max(halfExtents.Y, halfExtents.Z)) * 2.0f);
                    uvDensityHint = Math.Clamp(MathF.Sqrt(uvArea / surfaceArea) * maxExtent, 0.5f, 2.0f);
                }

                return new MeshTextureStreamingMetrics(uvDensityHint, pageSelection);
            }

            private static bool TryGetPrimaryTexCoord(Vertex vertex, out Vector2 uv)
            {
                List<Vector2>? textureCoordinateSets = vertex.TextureCoordinateSets;
                if (textureCoordinateSets is null || textureCoordinateSets.Count == 0)
                {
                    uv = default;
                    return false;
                }

                uv = textureCoordinateSets[0];
                return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
            }
        }

        #endregion
    }
}
