using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using System;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Progressive downsample-upsample bloom pass (Jimenez 2014, physically-based).
    /// <para>
    /// Pipeline:
    /// 1. Copy HDR scene -> bloom texture mip 0
    /// 2. Downsample chain (mip 0->1->2->3->4) with 13-tap filter + Karis average on first level
    /// 3. Upsample chain (mip 4->3->2->1) with 9-tap tent filter + additive blend
    /// 4. PostProcess reads bloom mip 1 (accumulated result) with BloomStrength
    /// </para>
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

        private string GetDownsampleShaderName() =>
            Stereo ? "BloomDownsampleStereo.fs" : "BloomDownsample.fs";

        private string GetUpsampleShaderName() =>
            Stereo ? "BloomUpsampleStereo.fs" : "BloomUpsample.fs";

        // FBO names for the initial copy (mip 0 write target).
        public const string BloomMip0FBOName = "BloomMip0FBO";

        // Downsample FBO names (write targets for mips 1-4).
        public const string BloomDS1FBOName = "BloomDS1FBO";
        public const string BloomDS2FBOName = "BloomDS2FBO";
        public const string BloomDS3FBOName = "BloomDS3FBO";
        public const string BloomDS4FBOName = "BloomDS4FBO";

        // Upsample FBO names (write targets for mips 3-1, blended with existing content).
        public const string BloomUS3FBOName = "BloomUS3FBO";
        public const string BloomUS2FBOName = "BloomUS2FBO";
        public const string BloomUS1FBOName = "BloomUS1FBO";

        public BoundingRectangle BloomRect4;
        public BoundingRectangle BloomRect3;
        public BoundingRectangle BloomRect2;
        public BoundingRectangle BloomRect1;

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

        private const int BloomMaxMipmapLevel = 4;

        private XRTexture CreateBloomTexture(uint width, uint height,
            EPixelInternalFormat internalFormat, ESizedInternalFormat sizedInternalFormat,
            EPixelFormat pixelFormat, EPixelType pixelType)
        {
            if (Stereo)
            {
                var t = XRTexture2DArray.CreateFrameBufferTexture(
                    2, width, height, internalFormat, pixelFormat, pixelType);
                t.Resizable = false;
                t.SizedInternalFormat = sizedInternalFormat;
                t.LargestMipmapLevel = 0;
                t.SmallestAllowedMipmapLevel = BloomMaxMipmapLevel;
                t.OVRMultiViewParameters = new(0, 2u);
                t.Name = BloomOutputTextureName;
                t.SamplerName = BloomOutputTextureName;
                t.MagFilter = ETexMagFilter.Linear;
                t.MinFilter = ETexMinFilter.LinearMipmapLinear;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                return t;
            }
            else
            {
                var t = XRTexture2D.CreateFrameBufferTexture(
                    width, height, internalFormat, pixelFormat, pixelType);
                t.Resizable = false;
                t.SizedInternalFormat = sizedInternalFormat;
                t.LargestMipmapLevel = 0;
                t.SmallestAllowedMipmapLevel = BloomMaxMipmapLevel;
                t.Name = BloomOutputTextureName;
                t.SamplerName = BloomOutputTextureName;
                t.MagFilter = ETexMagFilter.Linear;
                t.MinFilter = ETexMinFilter.LinearMipmapLinear;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                return t;
            }
        }

        private static RenderingParameters NoDepthTestParams() => new()
        {
            DepthTest =
            {
                Enabled = ERenderParamUsage.Unchanged,
                UpdateDepth = false,
                Function = EComparison.Always,
            }
        };

        private static RenderingParameters UpsampleBlendParams() => new()
        {
            DepthTest =
            {
                Enabled = ERenderParamUsage.Unchanged,
                UpdateDepth = false,
                Function = EComparison.Always,
            },
            // Additive blending: src * ONE + dst * ONE
            // Each upsample level adds its tent-filtered contribution to the
            // existing downsample content at the target mip.  After the full chain,
            // mip 1 contains the accumulated multi-scale bloom result.
            BlendModeAllDrawBuffers = new BlendMode()
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = EBlendingFactor.One,
                RgbDstFactor = EBlendingFactor.One,
                AlphaSrcFactor = EBlendingFactor.One,
                AlphaDstFactor = EBlendingFactor.One,
                RgbEquation = EBlendEquationMode.FuncAdd,
                AlphaEquation = EBlendEquationMode.FuncAdd,
            }
        };

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

            _lastWidth = width;
            _lastHeight = height;

            // Mip render areas (half-size per level).
            BloomRect1.Width = (int)(width * 0.5f);
            BloomRect1.Height = (int)(height * 0.5f);
            BloomRect2.Width = (int)(width * 0.25f);
            BloomRect2.Height = (int)(height * 0.25f);
            BloomRect3.Width = (int)(width * 0.125f);
            BloomRect3.Height = (int)(height * 0.125f);
            BloomRect4.Width = (int)(width * 0.0625f);
            BloomRect4.Height = (int)(height * 0.0625f);

            bool useHdr = Engine.Rendering.Settings.OutputHDR;
            var internalFormat = useHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8;
            var sizedInternalFormat = useHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;
            var pixelFormat = EPixelFormat.Rgba;
            var pixelType = useHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte;

            XRTexture outputTexture = CreateBloomTexture(width, height,
                internalFormat, sizedInternalFormat, pixelFormat, pixelType);

            instance.SetTexture(outputTexture);

            if (outputTexture is not IFrameBufferAttachement outputAttach)
                throw new InvalidOperationException("Output texture is not an IFrameBufferAttachement.");

            string downsampleShader = Path.Combine(SceneShaderPath, GetDownsampleShaderName());
            string upsampleShader = Path.Combine(SceneShaderPath, GetUpsampleShaderName());

            // --- Downsample material ---
            XRMaterial downsampleMat = new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderBool(false, "UseThreshold"),
                    new ShaderBool(false, "UseKarisAverage"),
                ],
                [outputTexture],
                XRShader.EngineShader(downsampleShader, EShaderType.Fragment))
            {
                RenderOptions = NoDepthTestParams()
            };

            // --- Upsample material (additive blend into existing mip content) ---
            XRMaterial upsampleMat = new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderFloat(1.0f, "Radius"),
                ],
                [outputTexture],
                XRShader.EngineShader(upsampleShader, EShaderType.Fragment))
            {
                RenderOptions = UpsampleBlendParams()
            };

            // --- Mip 0 write target (for initial HDR scene copy) ---
            // Uses the downsample material but is only bound for writing; the
            // inputFBO material is what actually renders into it.
            var mip0 = new XRQuadFrameBuffer(downsampleMat) { Name = BloomMip0FBOName };
            mip0.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            instance.SetFBO(mip0);

            // --- Downsample FBOs (target mips 1-4) ---
            var ds1 = new XRQuadFrameBuffer(downsampleMat) { Name = BloomDS1FBOName };
            var ds2 = new XRQuadFrameBuffer(downsampleMat) { Name = BloomDS2FBOName };
            var ds3 = new XRQuadFrameBuffer(downsampleMat) { Name = BloomDS3FBOName };
            var ds4 = new XRQuadFrameBuffer(downsampleMat) { Name = BloomDS4FBOName };

            ds1.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 1, -1));
            ds2.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 2, -1));
            ds3.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 3, -1));
            ds4.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 4, -1));

            ds1.SettingUniforms += DownsampleLevel1_SettingUniforms;
            ds2.SettingUniforms += DownsampleLevelN_SettingUniforms;
            ds3.SettingUniforms += DownsampleLevelN_SettingUniforms;
            ds4.SettingUniforms += DownsampleLevelN_SettingUniforms;

            instance.SetFBO(ds1);
            instance.SetFBO(ds2);
            instance.SetFBO(ds3);
            instance.SetFBO(ds4);

            // --- Upsample FBOs (target mips 3-1, blended with downsample content) ---
            var us3 = new XRQuadFrameBuffer(upsampleMat) { Name = BloomUS3FBOName };
            var us2 = new XRQuadFrameBuffer(upsampleMat) { Name = BloomUS2FBOName };
            var us1 = new XRQuadFrameBuffer(upsampleMat) { Name = BloomUS1FBOName };

            us3.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 3, -1));
            us2.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 2, -1));
            us1.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 1, -1));

            us3.SettingUniforms += UpsampleFbo_SettingUniforms;
            us2.SettingUniforms += UpsampleFbo_SettingUniforms;
            us1.SettingUniforms += UpsampleFbo_SettingUniforms;

            instance.SetFBO(us3);
            instance.SetFBO(us2);
            instance.SetFBO(us1);
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

            var mip0 = instance.GetFBO<XRQuadFrameBuffer>(BloomMip0FBOName);
            if (mip0 is null ||
                inputFBO.Width != _lastWidth ||
                inputFBO.Height != _lastHeight)
            {
                RegenerateFBOs(inputFBO.Width, inputFBO.Height);
                mip0 = instance.GetFBO<XRQuadFrameBuffer>(BloomMip0FBOName);
                if (mip0 is null)
                {
                    LogGuardFailure(nameof(Execute), "Bloom FBO chain is incomplete after regeneration; skipping this frame.");
                    return;
                }
            }

            var ds1 = instance.GetFBO<XRQuadFrameBuffer>(BloomDS1FBOName);
            var ds2 = instance.GetFBO<XRQuadFrameBuffer>(BloomDS2FBOName);
            var ds3 = instance.GetFBO<XRQuadFrameBuffer>(BloomDS3FBOName);
            var ds4 = instance.GetFBO<XRQuadFrameBuffer>(BloomDS4FBOName);
            var us3 = instance.GetFBO<XRQuadFrameBuffer>(BloomUS3FBOName);
            var us2 = instance.GetFBO<XRQuadFrameBuffer>(BloomUS2FBOName);
            var us1 = instance.GetFBO<XRQuadFrameBuffer>(BloomUS1FBOName);

            if (ds1 is null || ds2 is null || ds3 is null || ds4 is null ||
                us3 is null || us2 is null || us1 is null)
            {
                LogGuardFailure(nameof(Execute), "Bloom FBO chain is incomplete; skipping this frame.");
                return;
            }

            // Step 1: Copy HDR scene into bloom texture mip 0.
            using (mip0.BindForWritingState())
                inputFBO.Render();

            // Step 2: Progressive downsample chain (mip 0→1→2→3→4).
            // Each level reads from the previous mip using a 13-tap filter.
            // The first level (0→1) also applies bright-pass threshold + Karis average.
            DownsamplePass(ds1, BloomRect1, 0);  // mip 0 → mip 1 (+ threshold + Karis)
            DownsamplePass(ds2, BloomRect2, 1);  // mip 1 → mip 2
            DownsamplePass(ds3, BloomRect3, 2);  // mip 2 → mip 3
            DownsamplePass(ds4, BloomRect4, 3);  // mip 3 → mip 4

            // Step 3: Progressive upsample chain (mip 4→3→2→1).
            // Each level tent-filters the lower-res mip and additively blends
            // into the existing downsample content at the target mip.
            // After this chain, mip 1 contains the accumulated bloom result.
            UpsamplePass(us3, BloomRect3, 4);  // mip 4 -> add into mip 3
            UpsamplePass(us2, BloomRect2, 3);  // mip 3 -> add into mip 2
            UpsamplePass(us1, BloomRect1, 2);  // mip 2 -> add into mip 1
        }

        /// <summary>
        /// Runs a single downsample pass: reads <paramref name="sourceMip"/>, writes to the FBO's target mip.
        /// </summary>
        private void DownsamplePass(XRQuadFrameBuffer fbo, BoundingRectangle rect, int sourceMip)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(DownsamplePass), "No active pipeline instance during downsample; skipping.");
                return;
            }

            fbo.Material?.SetInt(0, sourceMip); // SourceLOD
            using (fbo.BindForWritingState())
            using (instance.RenderState.PushRenderArea(rect))
                fbo.Render();
        }

        /// <summary>
        /// Runs a single upsample pass: reads <paramref name="sourceMip"/>, tent-filters it, and
        /// additively blends into the FBO's target mip (which already contains the downsample result).
        /// </summary>
        private void UpsamplePass(XRQuadFrameBuffer fbo, BoundingRectangle rect, int sourceMip)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(UpsamplePass), "No active pipeline instance during upsample; skipping.");
                return;
            }

            fbo.Material?.SetInt(0, sourceMip); // SourceLOD
            using (fbo.BindForWritingState())
            using (instance.RenderState.PushRenderArea(rect))
                fbo.Render();
        }

        // --- Uniform callbacks ---

        /// <summary>
        /// First downsample level: applies threshold + Karis average.
        /// </summary>
        private static void DownsampleLevel1_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(DownsampleLevel1_SettingUniforms), "No active pipeline instance; using safe defaults.");
                SetDefaultDownsampleUniforms(program, 0, true);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                bloom.SetDownsampleUniforms(program, 0, firstLevel: true);
                return;
            }

            SetDefaultDownsampleUniforms(program, 0, true);
        }

        /// <summary>
        /// Subsequent downsample levels: no threshold, no Karis.
        /// </summary>
        private static void DownsampleLevelN_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(DownsampleLevelN_SettingUniforms), "No active pipeline instance; using safe defaults.");
                SetDefaultDownsampleUniforms(program, 0, false);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                // SourceLOD is set dynamically in DownsamplePass via SetInt; just set the static uniforms.
                bloom.SetDownsampleUniforms(program, 0, firstLevel: false);
                return;
            }

            SetDefaultDownsampleUniforms(program, 0, false);
        }

        private static void SetDefaultDownsampleUniforms(XRRenderProgram program, int sourceLod, bool firstLevel)
        {
            program.Uniform("SourceLOD", sourceLod);
            program.Uniform("UseThreshold", firstLevel);
            program.Uniform("BloomThreshold", 1.0f);
            program.Uniform("BloomSoftKnee", 0.5f);
            program.Uniform("BloomIntensity", 1.0f);
            program.Uniform("UseKarisAverage", firstLevel);
        }

        private static void UpsampleFbo_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(UpsampleFbo_SettingUniforms), "No active pipeline instance; using safe defaults.");
                program.Uniform("SourceLOD", 0);
                program.Uniform("Radius", 1.0f);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                bloom.SetUpsampleUniforms(program, 0);
                return;
            }

            program.Uniform("SourceLOD", 0);
            program.Uniform("Radius", 1.0f);
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
