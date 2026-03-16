using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Fullscreen pass that detects "complex" MSAA pixels by comparing GBuffer samples.
    /// Writes stencil bit 2 (0x04) for pixels where MSAA samples diverge (geometric edges).
    /// Subsequent deferred lighting passes use the stencil to shade complex pixels per-sample.
    /// </summary>
    public class VPRC_MarkComplexMsaaPixels : ViewportRenderCommand
    {
        /// <summary>
        /// Stencil bit used to mark complex pixels. Bit 2 (0x04) avoids collision
        /// with bits 0-1 used by selection/hover highlighting.
        /// </summary>
        public const uint ComplexPixelStencilBit = 0x04;

        public string MsaaNormalTexture { get; set; } = DefaultRenderPipeline.MsaaNormalTextureName;
        public string MsaaDepthViewTexture { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;

        public VPRC_MarkComplexMsaaPixels SetOptions(string normalTexture, string depthViewTexture)
        {
            MsaaNormalTexture = normalTexture;
            MsaaDepthViewTexture = depthViewTexture;
            return this;
        }

        /// <summary>
        /// Dot-product threshold for normal sample divergence.
        /// Lower values are stricter (more pixels classified as complex).
        /// </summary>
        public float NormalThreshold { get; set; } = 0.99f;

        /// <summary>
        /// Absolute depth difference threshold for sample divergence.
        /// </summary>
        public float DepthThreshold { get; set; } = 0.001f;

        private XRMeshRenderer? _quadRenderer;
        private XRTexture? _normalTexCache;
        private XRTexture? _depthTexCache;

        protected override void Execute()
        {
            var normalTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaNormalTexture);
            var depthTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaDepthViewTexture);
            if (normalTex is null || depthTex is null)
                return;

            if (_quadRenderer is null || _normalTexCache != normalTex || _depthTexCache != depthTex)
            {
                _normalTexCache = normalTex;
                _depthTexCache = depthTex;
                _quadRenderer = CreateQuadRenderer(normalTex, depthTex);
            }

            _quadRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null);
        }

        private static XRMesh CreateFullscreenTriangle()
        {
            VertexTriangle triangle = new(
                new Vector3(-1, -1, 0),
                new Vector3( 3, -1, 0),
                new Vector3(-1,  3, 0));
            return XRMesh.Create(triangle);
        }

        private XRMeshRenderer CreateQuadRenderer(XRTexture normalTex, XRTexture depthTex)
        {
            XRShader shader = XRShader.EngineShader(
                Path.Combine(SceneShaderPath, "MarkComplexMsaaPixels.fs"),
                EShaderType.Fragment);

            XRTexture[] textures = [normalTex, depthTex];

            var stencilFace = new StencilTestFace
            {
                Function = EComparison.Always,
                Reference = (int)ComplexPixelStencilBit,
                ReadMask = 0xFF,
                WriteMask = ComplexPixelStencilBit,
                BothFailOp = EStencilOp.Keep,
                StencilPassDepthFailOp = EStencilOp.Keep,
                BothPassOp = EStencilOp.Replace, // Write complex bit on non-discarded fragments
            };

            XRMaterial mat = new(textures, shader)
            {
                RenderOptions = new RenderingParameters
                {
                    CullMode = ECullMode.None,
                    DepthTest = new DepthTest
                    {
                        Enabled = ERenderParamUsage.Disabled,
                        UpdateDepth = false,
                    },
                    StencilTest = new StencilTest
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        FrontFace = stencilFace,
                        BackFace = stencilFace,
                    },
                    // No color writes — this pass only modifies stencil.
                    WriteRed = false,
                    WriteGreen = false,
                    WriteBlue = false,
                    WriteAlpha = false,
                }
            };

            var mesh = CreateFullscreenTriangle();
            var renderer = new XRMeshRenderer(mesh, mat);
            renderer.SettingUniforms += (_, materialProgram) =>
            {
                uint sampleCount = Math.Max(1u, DefaultRenderPipeline.ResolveEffectiveMsaaSampleCount());
                materialProgram.Uniform("SampleCount", (int)sampleCount);
                materialProgram.Uniform("NormalThreshold", NormalThreshold);
                materialProgram.Uniform("DepthThreshold", DepthThreshold);
            };
            return renderer;
        }
    }
}
