using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    /// <summary>
    /// Represents a framebuffer, material, quad (actually a giant triangle), and camera to render with.
    /// </summary>
    public class XRQuadFrameBuffer : XRMaterialFrameBuffer
    {
        /// <summary>
        /// Use to set uniforms to the program containing the fragment shader.
        /// </summary>
        public event DelSetUniforms? SettingUniforms;

        public XRMeshRenderer FullScreenMesh { get; }

        private static XRMesh Mesh(bool useTriangle)
        {
            if (useTriangle)
            {
                //Render a triangle that overdraws past the screen - discard fragments outside the screen in the shader.
                VertexTriangle triangle = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 3, -1, 0),
                    new Vector3(-1,  3, 0));

                return XRMesh.Create(triangle);
            }
            else
            {
                //     .3
                //    /|
                //   / |
                // 1.__.2
                VertexTriangle triangle1 = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 1, -1, 0),
                    new Vector3( 1,  1, 0));

                // 3.__.2
                //  | /
                //  |/
                // 1.
                VertexTriangle triangle2 = new(
                    new Vector3(-1, -1, 0),
                    new Vector3( 1,  1, 0),
                    new Vector3(-1,  1, 0));

                return XRMesh.Create(triangle1, triangle2);
            }
        }

        /// <summary>
        /// Renders a material to the screen using a fullscreen orthographic quad.
        /// </summary>
        /// <param name="mat">The material containing textures to render to this fullscreen quad.</param>
        public XRQuadFrameBuffer(XRMaterial mat, bool useTriangle = true, bool deriveRenderTargetsFromMaterial = true)
            : base(mat, deriveRenderTargetsFromMaterial)
        {
            mat.RenderOptions.CullMode = ECullMode.None;
            FullScreenMesh = new XRMeshRenderer(Mesh(useTriangle), mat);
            FullScreenMesh.Name = $"FullscreenQuad:{mat.Name ?? "Material"}";
            FullScreenMesh.GenerateAsync = false;
            FullScreenMesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
            FullScreenMesh.EnsureRenderPipelineVersionsCreated();
            FullScreenMesh.SettingUniforms += SetUniforms;

            string diagName = $"FullscreenQuad:{mat.Name ?? "Material"}";

            // Force simple program linking for fullscreen blits; shader pipelines may skip rendering
            // if no separable program is present on the material (common for utility shaders).
            var defaultVer = FullScreenMesh.GetDefaultVersion();
            var ovrVer = FullScreenMesh.GetOVRMultiViewVersion();
            var nvVer = FullScreenMesh.GetNVStereoVersion();

            defaultVer.AllowShaderPipelines = false;
            defaultVer.Name = diagName;
            ovrVer.AllowShaderPipelines = false;
            ovrVer.Name = diagName;
            nvVer.AllowShaderPipelines = false;
            nvVer.Name = diagName;

            // Pre-generate GL resources immediately when on the render thread to avoid
            // inline-generate fallback stalls on the first frame these quads are drawn.
            if (Engine.IsRenderThread)
                defaultVer.Generate();
        }

        public XRQuadFrameBuffer(
            XRMaterial material,
            bool useTriangle,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material, useTriangle, true, targets)
        {
        }

        public XRQuadFrameBuffer(
            XRMaterial material,
            bool useTriangle,
            bool deriveRenderTargetsFromMaterial,
            params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            : this(material, useTriangle, deriveRenderTargetsFromMaterial) => SetRenderTargets(targets);

        private void SetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => SettingUniforms?.Invoke(materialProgram);

        /// <summary>
        /// Renders the FBO to the entire region set by Engine.Rendering.State.PushRenderArea().
        /// </summary>
        public void Render(XRFrameBuffer? target = null, bool forceNoStereo = false)
        {
            target?.BindForWriting();

            var state = Engine.Rendering.State.RenderingPipelineState;
            if (state != null)
            {
                using (state.PushRenderingCamera(null))
                    FullScreenMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, forceNoStereo);
            }
            else
                FullScreenMesh.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, 1, forceNoStereo);
            target?.UnbindFromWriting();
        }
    }
}
