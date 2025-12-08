using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies bloom to the last FBO.
    /// </summary>
    public class VPRC_BloomPass : ViewportRenderCommand
    {
        private string GetBloomBlurShaderName() =>
            //Stereo ? "BloomBlurStereo.fs" : 
            "BloomBlur.fs";

        public const string BloomBlur1FBOName = "BloomBlurFBO1";
        public const string BloomBlur2FBOName = "BloomBlurFBO2";
        public const string BloomBlur4FBOName = "BloomBlurFBO4";
        public const string BloomBlur8FBOName = "BloomBlurFBO8";
        public const string BloomBlur16FBOName = "BloomBlurFBO16";

        public BoundingRectangle BloomRect16;
        public BoundingRectangle BloomRect8;
        public BoundingRectangle BloomRect4;
        public BoundingRectangle BloomRect2;
        //public BoundingRectangle BloomRect1;

        /// <summary>
        /// The name of the FBO that will be used as input for the bloom pass.
        /// </summary>
        public string InputFBOName { get; set; } = "BloomInputFBO";

        /// <summary>
        /// This is the texture that will contain the final bloom output.
        /// </summary>
        public string BloomOutputTextureName { get; set; } = "BloomOutputTexture";

        public bool Stereo { get; set; }

        public void SetTargetFBONames(string inputFBOName, string outputTextureName, bool stereo)
        {
            InputFBOName = inputFBOName;
            BloomOutputTextureName = outputTextureName;
            Stereo = stereo;
        }

        private uint _lastWidth = 0u;
        private uint _lastHeight = 0u;

        private void RegenerateFBOs(uint width, uint height)
        {
            width = Math.Max(1u, width);
            height = Math.Max(1u, height);

            //Debug.Out($"Regenerating bloom pass FBOs at {width} x {height}.");

            _lastWidth = width;
            _lastHeight = height;

            BloomRect16.Width = (int)(width * 0.0625f);
            BloomRect16.Height = (int)(height * 0.0625f);
            BloomRect8.Width = (int)(width * 0.125f);
            BloomRect8.Height = (int)(height * 0.125f);
            BloomRect4.Width = (int)(width * 0.25f);
            BloomRect4.Height = (int)(height * 0.25f);
            BloomRect2.Width = (int)(width * 0.5f);
            BloomRect2.Height = (int)(height * 0.5f);
            //BloomRect1.Width = width;
            //BloomRect1.Height = height;

            bool useHdr = Engine.Rendering.Settings.OutputHDR;
            var internalFormat = useHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8;
            var sizedInternalFormat = useHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;
            var pixelFormat = EPixelFormat.Rgba;
            var pixelType = useHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte;

            XRTexture outputTexture;
            if (Stereo)
            {
                var t = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    width,
                    height,
                    internalFormat,
                    pixelFormat,
                    pixelType);
                t.Resizable = false;
                t.SizedInternalFormat = sizedInternalFormat;
                t.OVRMultiViewParameters = new(0, 2u);
                t.Name = BloomOutputTextureName;
                t.MagFilter = ETexMagFilter.Linear;
                t.MinFilter = ETexMinFilter.LinearMipmapLinear;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                outputTexture = t;
            }
            else
            {
                var t = XRTexture2D.CreateFrameBufferTexture(
                    width,
                    height,
                    internalFormat,
                    pixelFormat,
                    pixelType);
                //t.Resizable = false;
                t.SizedInternalFormat = sizedInternalFormat;
                t.Name = BloomOutputTextureName;
                t.MagFilter = ETexMagFilter.Linear;
                t.MinFilter = ETexMinFilter.LinearMipmapLinear;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                outputTexture = t;
            }

            ActivePipelineInstance.SetTexture(outputTexture);

            XRMaterial bloomBlurMat = new
            (
                [
                    new ShaderFloat(0.0f, "Ping"),
                    new ShaderInt(0, "LOD"),
                    new ShaderFloat(1.0f, "Radius"),
                ],
                [outputTexture],
                XRShader.EngineShader(Path.Combine(SceneShaderPath, GetBloomBlurShaderName()), EShaderType.Fragment))
            {
                RenderOptions = new RenderingParameters()
                {
                    DepthTest =
                    {
                        Enabled = ERenderParamUsage.Unchanged,
                        UpdateDepth = false,
                        Function = EComparison.Always,
                    }
                }
            };

            var blur1 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur1FBOName };
            var blur2 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur2FBOName };
            var blur4 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur4FBOName };
            var blur8 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur8FBOName };
            var blur16 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur16FBOName };

            AttachBloomUniforms(blur1, blur2, blur4, blur8, blur16);

            if (outputTexture is not IFrameBufferAttachement outputAttach)
                throw new InvalidOperationException("Output texture is not an IFrameBufferAttachement.");

            blur1.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            blur2.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 1, -1));
            blur4.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 2, -1));
            blur8.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 3, -1));
            blur16.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 4, -1));

            ActivePipelineInstance.SetFBO(blur1);
            ActivePipelineInstance.SetFBO(blur2);
            ActivePipelineInstance.SetFBO(blur4);
            ActivePipelineInstance.SetFBO(blur8);
            ActivePipelineInstance.SetFBO(blur16);
        }

        protected override void Execute()
        {
            var inputFBO = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(InputFBOName);
            if (inputFBO is null)
                return;

            var blur16 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur16FBOName);
            var blur8 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur8FBOName);
            var blur4 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur4FBOName);
            var blur2 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur2FBOName);
            var blur1 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur1FBOName);

            if (blur16 is null ||
                blur8 is null ||
                blur4 is null ||
                blur2 is null ||
                blur1 is null)
            {
                RegenerateFBOs(inputFBO.Width, inputFBO.Height);
                blur16 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur16FBOName);
                blur8 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur8FBOName);
                blur4 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur4FBOName);
                blur2 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur2FBOName);
                blur1 = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(BloomBlur1FBOName);
            }
            else if (inputFBO.Width != _lastWidth ||
                inputFBO.Height != _lastHeight)
                RegenerateFBOs(inputFBO.Width, inputFBO.Height);

            using (blur1!.BindForWritingState())
                inputFBO!.Render();

            var tex = ActivePipelineInstance.GetTexture<XRTexture>(BloomOutputTextureName);
            tex?.GenerateMipmapsGPU();

            BloomScaledPass(blur16!, BloomRect16, 4);
            BloomScaledPass(blur8!, BloomRect8, 3);
            BloomScaledPass(blur4!, BloomRect4, 2);
            BloomScaledPass(blur2!, BloomRect2, 1);
            //Don't blur original image, barely makes a difference to result
        }
        private void BloomScaledPass(XRQuadFrameBuffer fbo, BoundingRectangle rect, int mipmap)
        {
            using (fbo.BindForWritingState())
            {
                using (ActivePipelineInstance.RenderState.PushRenderArea(rect))
                {
                    // Blur this mip by sampling from the next higher-res mip to avoid read/write hazards.
                    int sourceMip = Math.Max(0, mipmap - 1);
                    BloomBlur(fbo, sourceMip, 0.0f);
                    BloomBlur(fbo, sourceMip, 1.0f);
                }
            }
        }
        private static void BloomBlur(XRQuadFrameBuffer fbo, int sourceMip, float dir)
        {
            var mat = fbo.Material;
            if (mat is not null)
            {
                mat.SetFloat(0, dir);
                mat.SetInt(1, sourceMip);
            }
            fbo.Render();
        }

        private static void AttachBloomUniforms(params XRQuadFrameBuffer[] targets)
        {
            foreach (var target in targets)
                target.SettingUniforms += BloomBlurFbo_SettingUniforms;
        }

        private static void BloomBlurFbo_SettingUniforms(XRRenderProgram program)
        {
            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true)
            {
                bloom.SetBlurPassUniforms(program);
                return;
            }

            program.Uniform("Radius", 1.0f);
            program.Uniform("UseThreshold", false);
            program.Uniform("BloomThreshold", 1.0f);
            program.Uniform("BloomSoftKnee", 0.5f);
        }
    }
}
