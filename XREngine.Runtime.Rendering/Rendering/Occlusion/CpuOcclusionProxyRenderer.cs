using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Draws a tiny depth-only AABB proxy used by <see cref="CpuRenderOcclusionCoordinator"/>
    /// to retest an already-occluded mesh *without* contributing to the visible image.
    /// Recovery runs after visible meshes have built complete depth. Because the candidate did
    /// not contribute to that depth, a zero-sample bounds result proves it remains occluded;
    /// a false-positive merely preserves an extra visible draw. Each recovery query draws a
    /// cheap solid bounding box with:
    ///
    ///   - Color writes disabled (WriteRed/Green/Blue/Alpha = false)
    ///   - Depth writes disabled (DepthTest.UpdateDepth = false)
    ///   - Depth test enabled (so occluders correctly suppress samples)
    ///   - Cull mode = None (count samples regardless of face winding)
    ///
    /// Visible-demotion queries instead bracket the exact contributing mesh draw. The query
    /// (AnySamplesPassedConservative) is begun/ended around this proxy only for recovery; if
    /// any AABB fragment would pass, the next-frame visibility flips back to "drawn".
    /// </summary>
    internal static class CpuOcclusionProxyRenderer
    {
        private static readonly object s_initLock = new();
        private static volatile XRMeshRenderer? s_unitCubeRenderer;
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
                // Author the canonical normal-Z comparison once. Backends map this
                // through the active camera depth mode (LEQUAL -> GEQUAL for reversed Z).
                rp.DepthTest.Function = EComparison.Lequal;
                rp.StencilTest.Enabled = ERenderParamUsage.Disabled;

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
        public static void Draw(in AABB worldBounds, XRCamera camera)
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
            // RenderCPU's camera argument is the ownership key used for the query state.
            // Independent desktop, preview, and OpenXR outputs can be enqueued in one
            // engine frame, so ambient pipeline state may already point at a sibling
            // output when this deferred proxy is emitted. Snapshot the same camera that
            // made the visibility decision into the proxy draw.
            RuntimeEngine.Rendering.State.RenderingCameraOverride = camera;
            try
            {
                renderer.Render(model, model, s_probeMaterial, instances: 1u, renderOptionsOverride: s_probeRenderParams);
            }
            finally
            {
                // RenderingCameraOverride is a thread-local stack: null pops the
                // camera pushed above and reveals any outer override automatically.
                RuntimeEngine.Rendering.State.RenderingCameraOverride = null;
            }
        }
    }
}
