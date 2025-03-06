using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_MSVO : ViewportRenderCommand
    {
        public string MSVOIntensityTextureName { get; set; } = "SSAOFBOTexture";
        public string MSVOFBOName { get; set; } = "SSAOFBO";
        public string MSVOBlurFBOName { get; set; } = "SSAOBlurFBO";
        public string GBufferFBOFBOName { get; set; } = "GBufferFBO";
        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSITextureName { get; set; } = "RMSI";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";

        public Vector4 ScaleFactors { get; set; } = new(0.1f, 0.2f, 0.4f, 0.8f);

        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmsi, string depthStencil)
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSITextureName = rmsi;
            DepthStencilTextureName = depthStencil;
        }
        public void SetOutputNames(string ssaoIntensityTexture, string ssaoFBO, string ssaoBlurFBO, string gBufferFBO)
        {
            MSVOIntensityTextureName = ssaoIntensityTexture;
            MSVOFBOName = ssaoFBO;
            MSVOBlurFBOName = ssaoBlurFBO;
            GBufferFBOFBOName = gBufferFBO;
        }

        private int _lastWidth = 0;
        private int _lastHeight = 0;

        protected override void Execute()
        {
            XRTexture? normalTex = Pipeline.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = Pipeline.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = Pipeline.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmsiTex = Pipeline.GetTexture<XRTexture>(RMSITextureName);
            XRTexture? depthStencilTex = Pipeline.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmsiTex is null ||
                depthStencilTex is null)
                return;

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            if (width == _lastWidth &&
                height == _lastHeight)
                return;

            RegenerateFBOs(
                normalTex,
                depthViewTex,
                albedoTex,
                rmsiTex,
                depthStencilTex,
                width,
                height);
        }

        private void RegenerateFBOs(
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmsiTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            Debug.Out($"MSVO: Regenerating FBOs for {width}x{height}");
            _lastWidth = width;
            _lastHeight = height;

            XRTexture2D msvoTex = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            msvoTex.Name = MSVOIntensityTextureName;
            msvoTex.MinFilter = ETexMinFilter.Nearest;
            msvoTex.MagFilter = ETexMagFilter.Nearest;
            Pipeline.SetTexture(msvoTex);

            RenderingParameters renderParams = new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

            var ssaoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "MSVOGen.fs"), EShaderType.Fragment);

            XRTexture[] msvoGenTexRefs =
            [
                normalTex,
                depthViewTex,
            ];

            XRMaterial ssaoGenMat = new(msvoGenTexRefs, ssaoGenShader) { RenderOptions = renderParams };

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmsiTex is not IFrameBufferAttachement rmsiAttach)
                throw new ArgumentException("RMSI texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer msvoGenFBO = new(ssaoGenMat, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmsiAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = MSVOFBOName
            };
            msvoGenFBO.SettingUniforms += MSVOGen_SetUniforms;

            XRFrameBuffer gbufferFBO = new((msvoTex, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = GBufferFBOFBOName
            };

            Pipeline.SetFBO(msvoGenFBO);
            Pipeline.SetFBO(gbufferFBO);
        }

        private void MSVOGen_SetUniforms(XRRenderProgram program)
        {
            program.Uniform("ScaleFactors", ScaleFactors);

            var rc = Pipeline.RenderState.SceneCamera;
            if (rc is null)
                return;

            rc.SetUniforms(program);
            rc.SetAmbientOcclusionUniforms(program);

            program.Uniform(EEngineUniform.ScreenWidth.ToString(), (float)Pipeline.RenderState.CurrentRenderRegion.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), (float)Pipeline.RenderState.CurrentRenderRegion.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), 0.0f);
        }
    }
}
