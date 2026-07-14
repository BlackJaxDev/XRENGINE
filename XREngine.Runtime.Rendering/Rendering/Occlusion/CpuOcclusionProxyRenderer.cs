using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Draws a tiny depth-only AABB proxy used by <see cref="CpuRenderOcclusionCoordinator"/>
    /// to refresh an occluded mesh's hardware occlusion-query result *without* contributing
    /// to the visible image. This is the requery path for periodically-retested occluded
    /// meshes — instead of redrawing the full mesh (which causes visible flicker), we draw
    /// a cheap solid bounding box with:
    ///
    ///   - Color writes disabled (WriteRed/Green/Blue/Alpha = false)
    ///   - Depth writes disabled (DepthTest.UpdateDepth = false)
    ///   - Depth test enabled (so occluders correctly suppress samples)
    ///   - Cull mode = None (count samples regardless of face winding)
    ///
    /// The query (AnySamplesPassedConservative) is begun/ended around this proxy draw by
    /// the coordinator; if any fragment of the AABB would pass the depth test, the query
    /// reports the mesh as visible and the next-frame visibility flips back to "drawn".
    /// </summary>
    internal static class CpuOcclusionProxyRenderer
    {
        private static readonly object s_initLock = new();
        private static XRMeshRenderer? s_unitCubeRenderer;
        private static XRMaterial? s_probeMaterial;
        private static RenderingParameters? s_probeRenderParams;

        private static void EnsureInitialized()
        {
            if (s_unitCubeRenderer is not null)
                return;

            lock (s_initLock)
            {
                if (s_unitCubeRenderer is not null)
                    return;

                // Unit cube in local space [0,0,0]..[1,1,1] — the AABB→model mapping is a
                // simple scale-by-size + translate-by-min, no centering correction needed.
                XRMesh cube = XRMesh.Shapes.SolidBox(Vector3.Zero, Vector3.One);
                cube.Name = "CpuOcclusionProxy.UnitCube";

                // The color attachment is masked off below, so an opaque shader value
                // cannot affect the image. Keeping alpha nonzero also prevents future
                // alpha-discard variants from turning a valid query into an empty one.
                XRMaterial probeMat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.White);
                probeMat.Name = "CpuOcclusionProxy.Material";

                var rp = new RenderingParameters
                {
                    WriteRed = false,
                    WriteGreen = false,
                    WriteBlue = false,
                    WriteAlpha = false,
                    CullMode = ECullMode.None,
                };
                rp.DepthTest.Enabled = ERenderParamUsage.Enabled;
                rp.DepthTest.UpdateDepth = false;
                rp.DepthTest.Function = EComparison.Lequal;

                probeMat.RenderOptions = rp;

                s_probeMaterial = probeMat;
                s_probeRenderParams = rp;
                s_unitCubeRenderer = new XRMeshRenderer(cube, probeMat)
                {
                    Name = "CpuOcclusionProxy.Renderer",
                };
            }
        }

        /// <summary>
        /// Ensures the proxy's program, buffers, and descriptors are ready before a
        /// query begin is emitted. Query brackets must never be recorded around a
        /// draw that is still waiting on asynchronous resource generation.
        /// </summary>
        public static bool TryPrepare(out string reason)
        {
            EnsureInitialized();

            XRMeshRenderer? renderer = s_unitCubeRenderer;
            if (renderer is null)
            {
                reason = "RendererMissing";
                return false;
            }

            return renderer.TryPrepareForRendering(out reason);
        }

        /// <summary>
        /// Draws the depth-only AABB proxy around the supplied world-space bounds.
        /// Must be called between <c>CpuRenderOcclusionCoordinator.BeginQuery</c> and
        /// <c>EndQuery</c> so the conservative samples-passed query straddles the draw.
        /// </summary>
        public static void Draw(in AABB worldBounds)
        {
            EnsureInitialized();

            XRMeshRenderer? renderer = s_unitCubeRenderer;
            if (renderer is null)
                return;

            Vector3 size = worldBounds.Max - worldBounds.Min;
            // Degenerate bounds — nothing meaningful to query against.
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f)
                return;

            Matrix4x4 model = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(worldBounds.Min);
            renderer.Render(model, model, s_probeMaterial, instances: 1u, renderOptionsOverride: s_probeRenderParams);
        }
    }
}
