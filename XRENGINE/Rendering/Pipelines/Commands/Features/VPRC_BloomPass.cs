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
    [RenderPipelineScriptCommand]
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
        private int _activeBloomMaxMip = 0;

        private const int BloomMaxMipmapLevel = 4;

        private XRTexture CreateBloomTexture(uint width, uint height, int maxMipLevel,
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
                t.SmallestAllowedMipmapLevel = maxMipLevel;
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
                t.SmallestAllowedMipmapLevel = maxMipLevel;
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

        private static int ResolveBloomMaxMip(uint width, uint height)
            => Math.Min(BloomMaxMipmapLevel, XRTexture.GetSmallestMipmapLevel(width, height));

        private static string GetDownsampleFboName(int mipLevel)
            => mipLevel switch
            {
                1 => BloomDS1FBOName,
                2 => BloomDS2FBOName,
                3 => BloomDS3FBOName,
                4 => BloomDS4FBOName,
                _ => throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel, "Bloom downsample mip level is out of range."),
            };

        private static string GetUpsampleFboName(int targetMipLevel)
            => targetMipLevel switch
            {
                1 => BloomUS1FBOName,
                2 => BloomUS2FBOName,
                3 => BloomUS3FBOName,
                _ => throw new ArgumentOutOfRangeException(nameof(targetMipLevel), targetMipLevel, "Bloom upsample target mip level is out of range."),
            };

        private BoundingRectangle GetBloomRect(int mipLevel)
            => mipLevel switch
            {
                1 => BloomRect1,
                2 => BloomRect2,
                3 => BloomRect3,
                4 => BloomRect4,
                _ => default,
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
            _activeBloomMaxMip = ResolveBloomMaxMip(width, height);

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

            XRTexture outputTexture = CreateBloomTexture(width, height, _activeBloomMaxMip,
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

            // --- Downsample FBOs (target mips 1..N) ---
            for (int mipLevel = 1; mipLevel <= _activeBloomMaxMip; ++mipLevel)
            {
                var downsampleFbo = new XRQuadFrameBuffer(downsampleMat) { Name = GetDownsampleFboName(mipLevel) };
                downsampleFbo.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, mipLevel, -1));
                downsampleFbo.SettingUniforms += mipLevel == 1
                    ? DownsampleLevel1_SettingUniforms
                    : DownsampleLevelN_SettingUniforms;
                instance.SetFBO(downsampleFbo);
            }

            // --- Upsample FBOs (target mips N-1..1, blended with downsample content) ---
            for (int targetMipLevel = _activeBloomMaxMip - 1; targetMipLevel >= 1; --targetMipLevel)
            {
                var upsampleFbo = new XRQuadFrameBuffer(upsampleMat) { Name = GetUpsampleFboName(targetMipLevel) };
                upsampleFbo.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, targetMipLevel, -1));
                upsampleFbo.SettingUniforms += UpsampleFbo_SettingUniforms;
                instance.SetFBO(upsampleFbo);
            }
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

            // Step 1: Copy HDR scene into bloom texture mip 0.
            using (mip0.BindForWritingState())
                inputFBO.Render();

            if (_activeBloomMaxMip < 1)
                return;

            for (int mipLevel = 1; mipLevel <= _activeBloomMaxMip; ++mipLevel)
            {
                XRQuadFrameBuffer? downsampleFbo = instance.GetFBO<XRQuadFrameBuffer>(GetDownsampleFboName(mipLevel));
                if (downsampleFbo is null)
                {
                    LogGuardFailure(nameof(Execute), $"Bloom downsample FBO for mip {mipLevel} is missing; skipping this frame.");
                    return;
                }

                DownsamplePass(downsampleFbo, GetBloomRect(mipLevel), mipLevel - 1);
            }

            for (int sourceMipLevel = _activeBloomMaxMip; sourceMipLevel >= 2; --sourceMipLevel)
            {
                int targetMipLevel = sourceMipLevel - 1;
                XRQuadFrameBuffer? upsampleFbo = instance.GetFBO<XRQuadFrameBuffer>(GetUpsampleFboName(targetMipLevel));
                if (upsampleFbo is null)
                {
                    LogGuardFailure(nameof(Execute), $"Bloom upsample FBO for mip {targetMipLevel} is missing; skipping this frame.");
                    return;
                }

                UpsamplePass(upsampleFbo, GetBloomRect(targetMipLevel), sourceMipLevel);
            }
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

        private void BindBloomSourceTexture(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            XRTexture? bloomTexture = instance?.GetTexture<XRTexture>(BloomOutputTextureName);
            if (bloomTexture is null)
            {
                program.SuppressFallbackSamplerWarning("SourceTexture");
                return;
            }

            program.Sampler("SourceTexture", bloomTexture, 0);
        }

        /// <summary>
        /// First downsample level: applies threshold + Karis average.
        /// </summary>
        private void DownsampleLevel1_SettingUniforms(XRRenderProgram program)
        {
            BindBloomSourceTexture(program);

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
        private void DownsampleLevelN_SettingUniforms(XRRenderProgram program)
        {
            BindBloomSourceTexture(program);

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

        private void UpsampleFbo_SettingUniforms(XRRenderProgram program)
        {
            BindBloomSourceTexture(program);

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
