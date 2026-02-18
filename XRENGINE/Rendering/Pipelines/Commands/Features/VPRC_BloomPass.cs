using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using System;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies bloom to the last FBO.
    /// </summary>
    public class VPRC_BloomPass : ViewportRenderCommand
    {
        private static void LogGuardFailure(string location, string reason)
            => Debug.RenderingEvery(
                $"ResilienceGuard.Bloom.{location}",
                TimeSpan.FromSeconds(1),
                "[Bloom][RESILIENCE GUARD TRIGGERED] {0}: {1}",
                location,
                reason);

        private string GetBloomBlurShaderName() =>
            Stereo ? "BloomBlurStereo.fs" : 
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
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(RegenerateFBOs), "No active pipeline instance; skipping FBO regeneration.");
                return;
            }

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
            const int bloomMaxMipmapLevel = 4;
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
                t.LargestMipmapLevel = 0;
                t.SmallestAllowedMipmapLevel = bloomMaxMipmapLevel;
                t.OVRMultiViewParameters = new(0, 2u);
                t.Name = BloomOutputTextureName;
                t.SamplerName = BloomOutputTextureName;
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
                // Bloom relies on attaching/rendering into multiple mip levels (0..4).
                // For OpenGL, framebuffer textures must have storage allocated for those mip levels.
                // Mark as non-resizable so the GL backend uses immutable storage (TexStorage) with mip levels.
                t.Resizable = false;
                t.SizedInternalFormat = sizedInternalFormat;
                t.LargestMipmapLevel = 0;
                t.SmallestAllowedMipmapLevel = bloomMaxMipmapLevel;
                t.Name = BloomOutputTextureName;
                t.SamplerName = BloomOutputTextureName;
                t.MagFilter = ETexMagFilter.Linear;
                t.MinFilter = ETexMinFilter.LinearMipmapLinear;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                outputTexture = t;
            }

            instance.SetTexture(outputTexture);

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

            instance.SetFBO(blur1);
            instance.SetFBO(blur2);
            instance.SetFBO(blur4);
            instance.SetFBO(blur8);
            instance.SetFBO(blur16);
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(Execute), "No active pipeline instance; bloom pass skipped.");
                return;
            }

            var inputFBO = instance.GetFBO<XRQuadFrameBuffer>(InputFBOName);
            if (inputFBO is null)
            {
                LogGuardFailure(nameof(Execute), $"Input FBO '{InputFBOName}' not found; bloom pass skipped.");
                return;
            }

            var blur16 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur16FBOName);
            var blur8 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur8FBOName);
            var blur4 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur4FBOName);
            var blur2 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur2FBOName);
            var blur1 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur1FBOName);

            if (blur16 is null ||
                blur8 is null ||
                blur4 is null ||
                blur2 is null ||
                blur1 is null)
            {
                RegenerateFBOs(inputFBO.Width, inputFBO.Height);
                blur16 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur16FBOName);
                blur8 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur8FBOName);
                blur4 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur4FBOName);
                blur2 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur2FBOName);
                blur1 = instance.GetFBO<XRQuadFrameBuffer>(BloomBlur1FBOName);

                if (blur16 is null ||
                    blur8 is null ||
                    blur4 is null ||
                    blur2 is null ||
                    blur1 is null)
                {
                    LogGuardFailure(nameof(Execute), "Bloom blur FBO chain is incomplete after regeneration; skipping this frame.");
                    return;
                }
            }
            else if (inputFBO.Width != _lastWidth ||
                inputFBO.Height != _lastHeight)
                RegenerateFBOs(inputFBO.Width, inputFBO.Height);

            using (blur1!.BindForWritingState())
                inputFBO!.Render();

            var tex = instance.GetTexture<XRTexture>(BloomOutputTextureName);
            tex?.GenerateMipmapsGPU();

            BloomScaledPass(blur16!, BloomRect16, 4);
            BloomScaledPass(blur8!, BloomRect8, 3);
            BloomScaledPass(blur4!, BloomRect4, 2);
            BloomScaledPass(blur2!, BloomRect2, 1);
            //Don't blur original image, barely makes a difference to result
        }
        private void BloomScaledPass(XRQuadFrameBuffer fbo, BoundingRectangle rect, int mipmap)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(BloomScaledPass), "No active pipeline instance during scaled pass; skipping mip blur.");
                return;
            }

            using (fbo.BindForWritingState())
            {
                using (instance.RenderState.PushRenderArea(rect))
                {
                    // Blur this mip by sampling from the next higher-res mip to avoid read/write hazards.
                    int sourceMip = Math.Max(0, mipmap - 1);
                    BloomBlur(fbo, sourceMip, 0.0f);
                    BloomBlur(fbo, sourceMip, 1.0f);
                }
            }
        }
        private static void BloomBlur(XRQuadFrameBuffer? fbo, int sourceMip, float dir)
        {
            if (ReferenceEquals(fbo, null))
            {
                LogGuardFailure(nameof(BloomBlur), "Target FBO is null; blur step skipped.");
                return;
            }

            var mat = fbo?.Material;
            if (mat is not null)
            {
                mat.SetFloat(0, dir);
                mat.SetInt(1, sourceMip);
            }
            fbo?.Render();
        }

        private static void AttachBloomUniforms(params XRQuadFrameBuffer[] targets)
        {
            foreach (var target in targets)
                target.SettingUniforms += BloomBlurFbo_SettingUniforms;
        }

        private static void BloomBlurFbo_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(BloomBlurFbo_SettingUniforms), "No active pipeline instance while setting blur uniforms; using safe defaults.");
                program.Uniform("Radius", 1.0f);
                program.Uniform("UseThreshold", false);
                program.Uniform("BloomThreshold", 1.0f);
                program.Uniform("BloomSoftKnee", 0.5f);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                bloom.SetBlurPassUniforms(program);
                return;
            }

            program.Uniform("Radius", 1.0f);
            program.Uniform("UseThreshold", false);
            program.Uniform("BloomThreshold", 1.0f);
            program.Uniform("BloomSoftKnee", 0.5f);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_BloomPass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeFboColorResource(InputFBOName));
            builder.ReadWriteTexture(MakeTextureResource(BloomOutputTextureName));
        }
    }
}
