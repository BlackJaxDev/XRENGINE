using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using System;
using System.Collections.Generic;

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

        private string GetCopyShaderName() =>
            Stereo ? "BloomCopyStereo.fs" : "BloomCopy.fs";

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

        private const string BloomCopyScopeName = "Bloom Copy Input->Mip0";
        private const string BloomDownsampleFallbackScopeName = "Bloom Downsample shader=BloomDownsample.fs";
        private const string BloomDownsampleStereoFallbackScopeName = "Bloom Downsample shader=BloomDownsampleStereo.fs";
        private const string BloomUpsampleFallbackScopeName = "Bloom Upsample shader=BloomUpsample.fs";
        private const string BloomUpsampleStereoFallbackScopeName = "Bloom Upsample shader=BloomUpsampleStereo.fs";
        private const string BloomSourceSamplerName = "SourceTexture";
        private const string BloomCopyPassName = "Bloom_Copy_Mip0";
        private static readonly bool BloomDebugSolidOutput =
            string.Equals(Environment.GetEnvironmentVariable("XRE_BLOOM_DEBUG_SOLID"), "1", StringComparison.OrdinalIgnoreCase);
        private static readonly bool BloomDiagEnabled =
            string.Equals(Environment.GetEnvironmentVariable("XRE_BLOOM_DIAG"), "1", StringComparison.OrdinalIgnoreCase);

        private static readonly string[] BloomDownsampleScopeNames =
        [
            "Bloom Downsample mip0->1 shader=BloomDownsample.fs",
            "Bloom Downsample mip1->2 shader=BloomDownsample.fs",
            "Bloom Downsample mip2->3 shader=BloomDownsample.fs",
            "Bloom Downsample mip3->4 shader=BloomDownsample.fs",
        ];

        private static readonly string[] BloomDownsampleStereoScopeNames =
        [
            "Bloom Downsample mip0->1 shader=BloomDownsampleStereo.fs",
            "Bloom Downsample mip1->2 shader=BloomDownsampleStereo.fs",
            "Bloom Downsample mip2->3 shader=BloomDownsampleStereo.fs",
            "Bloom Downsample mip3->4 shader=BloomDownsampleStereo.fs",
        ];

        private static readonly string[] BloomUpsampleScopeNames =
        [
            string.Empty,
            string.Empty,
            "Bloom Upsample mip2->1 shader=BloomUpsample.fs",
            "Bloom Upsample mip3->2 shader=BloomUpsample.fs",
            "Bloom Upsample mip4->3 shader=BloomUpsample.fs",
        ];

        private static readonly string[] BloomUpsampleStereoScopeNames =
        [
            string.Empty,
            string.Empty,
            "Bloom Upsample mip2->1 shader=BloomUpsampleStereo.fs",
            "Bloom Upsample mip3->2 shader=BloomUpsampleStereo.fs",
            "Bloom Upsample mip4->3 shader=BloomUpsampleStereo.fs",
        ];

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
        private XRTexture? _bloomSourceViewTexture;
        private XRTexture? _bloomCopySourceTexture;
        private readonly Dictionary<int, XRTexture> _bloomSourceMipViews = [];

        private const int BloomMaxMipmapLevel = 4;

        public override string GpuProfilingName
            => $"{base.GpuProfilingName}[{InputFBOName}->{BloomOutputTextureName}; {(Stereo ? "stereo" : "mono")}]";

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
                Enabled = ERenderParamUsage.Disabled,
                UpdateDepth = false,
                Function = EComparison.Always,
            },
            StencilTest =
            {
                Enabled = ERenderParamUsage.Disabled,
            },
            BlendModeAllDrawBuffers = BlendMode.Disabled(),
            RequiredEngineUniforms = EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy
        };

        private static RenderingParameters UpsampleBlendParams() => new()
        {
            DepthTest =
            {
                Enabled = ERenderParamUsage.Disabled,
                UpdateDepth = false,
                Function = EComparison.Always,
            },
            StencilTest =
            {
                Enabled = ERenderParamUsage.Disabled,
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
            },
            RequiredEngineUniforms = EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy
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

        private static string GetDownsamplePassName(int targetMipLevel)
            => $"Bloom_Downsample_Mip{targetMipLevel - 1}_to_{targetMipLevel}";

        private static string GetUpsamplePassName(int sourceMipLevel)
            => $"Bloom_Upsample_Mip{sourceMipLevel}_to_{sourceMipLevel - 1}";

        private BoundingRectangle GetBloomRect(int mipLevel)
            => mipLevel switch
            {
                1 => BloomRect1,
                2 => BloomRect2,
                3 => BloomRect3,
                4 => BloomRect4,
                _ => default,
            };

        private string GetDownsampleScopeName(int sourceMip)
        {
            string[] names = Stereo ? BloomDownsampleStereoScopeNames : BloomDownsampleScopeNames;
            return (uint)sourceMip < (uint)names.Length
                ? names[sourceMip]
                : Stereo ? BloomDownsampleStereoFallbackScopeName : BloomDownsampleFallbackScopeName;
        }

        private string GetUpsampleScopeName(int sourceMip)
        {
            string[] names = Stereo ? BloomUpsampleStereoScopeNames : BloomUpsampleScopeNames;
            return (uint)sourceMip < (uint)names.Length && !string.IsNullOrEmpty(names[sourceMip])
                ? names[sourceMip]
                : Stereo ? BloomUpsampleStereoFallbackScopeName : BloomUpsampleFallbackScopeName;
        }

        private void RegenerateFBOs(uint width, uint height, XRTexture? sourceTexture)
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

            (EPixelInternalFormat internalFormat, ESizedInternalFormat sizedInternalFormat, EPixelFormat pixelFormat, EPixelType pixelType) =
                ResolveBloomTextureFormat(sourceTexture, instance);

            XRTexture outputTexture = ResolveOrCreateBloomTexture(instance, width, height, _activeBloomMaxMip,
                internalFormat, sizedInternalFormat, pixelFormat, pixelType);

            _bloomSourceMipViews.Clear();
            _bloomSourceViewTexture = outputTexture;

            if (outputTexture is not IFrameBufferAttachement outputAttach)
                throw new InvalidOperationException("Output texture is not an IFrameBufferAttachement.");

            string copyShader = Path.Combine(SceneShaderPath, GetCopyShaderName());
            string downsampleShader = Path.Combine(SceneShaderPath, GetDownsampleShaderName());
            string upsampleShader = Path.Combine(SceneShaderPath, GetUpsampleShaderName());

            // --- Mip 0 write target (for initial HDR scene copy) ---
            var mip0 = new XRQuadFrameBuffer(CreateCopyMaterial(copyShader)) { Name = BloomMip0FBOName };
            mip0.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            mip0.SettingUniforms += BloomCopy_SettingUniforms;
            instance.SetFBO(mip0);

            // --- Downsample FBOs (target mips 1..N) ---
            for (int mipLevel = 1; mipLevel <= _activeBloomMaxMip; ++mipLevel)
            {
                var downsampleFbo = new XRQuadFrameBuffer(CreateDownsampleMaterial(outputTexture, mipLevel - 1, downsampleShader)) { Name = GetDownsampleFboName(mipLevel) };
                downsampleFbo.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, mipLevel, -1));
                downsampleFbo.SettingUniforms += mipLevel == 1
                    ? DownsampleLevel1_SettingUniforms
                    : DownsampleLevelN_SettingUniforms;
                instance.SetFBO(downsampleFbo);
            }

            // --- Upsample FBOs (target mips N-1..1, blended with downsample content) ---
            for (int targetMipLevel = _activeBloomMaxMip - 1; targetMipLevel >= 1; --targetMipLevel)
            {
                int sourceMipLevel = targetMipLevel + 1;
                var upsampleFbo = new XRQuadFrameBuffer(CreateUpsampleMaterial(outputTexture, sourceMipLevel, upsampleShader)) { Name = GetUpsampleFboName(targetMipLevel) };
                upsampleFbo.SetRenderTargets((outputAttach, EFrameBufferAttachment.ColorAttachment0, targetMipLevel, -1));
                upsampleFbo.SettingUniforms += UpsampleFbo_SettingUniforms;
                instance.SetFBO(upsampleFbo);
            }
        }

        private XRTexture ResolveOrCreateBloomTexture(
            XRRenderPipelineInstance instance,
            uint width,
            uint height,
            int maxMipLevel,
            EPixelInternalFormat internalFormat,
            ESizedInternalFormat sizedInternalFormat,
            EPixelFormat pixelFormat,
            EPixelType pixelType)
        {
            XRTexture? existing = instance.GetTexture<XRTexture>(BloomOutputTextureName);
            if (IsBloomTextureCompatible(existing, width, height, maxMipLevel, sizedInternalFormat))
                return existing!;

            XRTexture outputTexture = CreateBloomTexture(width, height, maxMipLevel,
                internalFormat, sizedInternalFormat, pixelFormat, pixelType);
            instance.SetTexture(outputTexture);
            return outputTexture;
        }

        private bool IsBloomTextureCompatible(
            XRTexture? texture,
            uint width,
            uint height,
            int maxMipLevel,
            ESizedInternalFormat sizedInternalFormat)
        {
            if (texture is null)
                return false;

            if (Stereo)
                return texture is XRTexture2DArray arrayTexture &&
                    arrayTexture.Width == width &&
                    arrayTexture.Height == height &&
                    arrayTexture.SizedInternalFormat == sizedInternalFormat &&
                    arrayTexture.LargestMipmapLevel == 0 &&
                    arrayTexture.SmallestAllowedMipmapLevel >= maxMipLevel;

            return texture is XRTexture2D monoTexture &&
                monoTexture.Width == width &&
                monoTexture.Height == height &&
                monoTexture.SizedInternalFormat == sizedInternalFormat &&
                monoTexture.LargestMipmapLevel == 0 &&
                monoTexture.SmallestAllowedMipmapLevel >= maxMipLevel;
        }

        private XRMaterial CreateDownsampleMaterial(XRTexture outputTexture, int sourceMip, string downsampleShader)
            => new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderBool(false, "UseThreshold"),
                    new ShaderBool(false, "UseKarisAverage"),
                    new ShaderBool(false, "DebugSolidOutput"),
                ],
                [GetOrCreateBloomSourceMipView(outputTexture, sourceMip)],
                XRShader.EngineShader(downsampleShader, EShaderType.Fragment))
            {
                RenderOptions = NoDepthTestParams()
            };

        private XRMaterial CreateCopyMaterial(string copyShader)
            => new(
                Array.Empty<XRTexture?>(),
                XRShader.EngineShader(copyShader, EShaderType.Fragment))
            {
                RenderOptions = NoDepthTestParams()
            };

        private XRMaterial CreateUpsampleMaterial(XRTexture outputTexture, int sourceMip, string upsampleShader)
            => new(
                [
                    new ShaderInt(0, "SourceLOD"),
                    new ShaderFloat(1.0f, "Radius"),
                    new ShaderFloat(0.7f, "Scatter"),
                ],
                [GetOrCreateBloomSourceMipView(outputTexture, sourceMip)],
                XRShader.EngineShader(upsampleShader, EShaderType.Fragment))
            {
                RenderOptions = UpsampleBlendParams()
            };

        protected override void Execute()
        {
            // Scene captures (light probes, reflection probes) don't need bloom.
            if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

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

            if (!VPRCSourceTextureHelpers.TryResolveColorTexture(
                    instance,
                    null,
                    InputFBOName,
                    out XRTexture? inputTexture,
                    out string resolveFailure) ||
                inputTexture is null)
            {
                LogGuardFailure(nameof(Execute), $"Input FBO '{InputFBOName}' has no bloom source texture: {resolveFailure}");
                return;
            }

            var mip0 = instance.GetFBO<XRQuadFrameBuffer>(BloomMip0FBOName);
            if (mip0 is null ||
                inputFBO.Width != _lastWidth ||
                inputFBO.Height != _lastHeight)
            {
                RegenerateFBOs(inputFBO.Width, inputFBO.Height, inputTexture);
                mip0 = instance.GetFBO<XRQuadFrameBuffer>(BloomMip0FBOName);
                if (mip0 is null)
                {
                    LogGuardFailure(nameof(Execute), "Bloom FBO chain is incomplete after regeneration; skipping this frame.");
                    return;
                }
            }

            // Step 1: Copy HDR scene into bloom texture mip 0.
            {
                _bloomCopySourceTexture = inputTexture;
                int copyPassIndex = ResolvePassIndex(BloomCopyPassName);
                using var copyPassScope = copyPassIndex != int.MinValue
                    ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(copyPassIndex)
                    : default;
                using (RenderPipelineGpuProfiler.Instance.StartScope(BloomCopyScopeName))
                using (mip0.BindForWritingState())
                {
                    bool rendered = mip0.Render();
                    LogBloomRenderResult("CopyMip0", mip0, default, copyPassIndex, 0, rendered);
                }
            }

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

            int passIndex = ResolvePassIndex(GetDownsamplePassName(sourceMip + 1));
            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;
            using (RenderPipelineGpuProfiler.Instance.StartScope(GetDownsampleScopeName(sourceMip)))
            using (instance.RenderState.PushRenderArea(rect))
            using (fbo.BindForWritingState())
            {
                bool rendered = fbo.Render();
                LogBloomRenderResult("Downsample", fbo, rect, passIndex, sourceMip, rendered);
            }
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

            int passIndex = ResolvePassIndex(GetUpsamplePassName(sourceMip));
            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;
            using (RenderPipelineGpuProfiler.Instance.StartScope(GetUpsampleScopeName(sourceMip)))
            using (instance.RenderState.PushRenderArea(rect))
            using (fbo.BindForWritingState())
            {
                bool rendered = fbo.Render();
                LogBloomRenderResult("Upsample", fbo, rect, passIndex, sourceMip, rendered);
            }
        }

        private static void LogBloomRenderResult(
            string phase,
            XRQuadFrameBuffer fbo,
            BoundingRectangle rect,
            int passIndex,
            int sourceMip,
            bool rendered)
        {
            if (!BloomDiagEnabled)
                return;

            bool prepared = fbo.FullScreenMesh.TryPrepareForRendering(out string reason);
            string detail = fbo.FullScreenMesh.GetLastPrepareDetail();
            var area = RuntimeEngine.Rendering.State.RenderArea;
            Debug.RenderingEvery(
                $"BloomDiag.{phase}.{fbo.Name}.{sourceMip}",
                TimeSpan.FromSeconds(1),
                "[BloomDiag] phase={0} fbo='{1}' pass={2} sourceMip={3} rendered={4} prepared={5} reason='{6}' detail='{7}' rect=({8},{9},{10},{11}) area=({12},{13},{14},{15}) material='{16}' mesh='{17}' targets={18}",
                phase,
                fbo.Name ?? "<unnamed>",
                passIndex,
                sourceMip,
                rendered,
                prepared,
                reason,
                detail,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                area.X,
                area.Y,
                area.Width,
                area.Height,
                fbo.Material?.Name ?? "<null>",
                fbo.FullScreenMesh.Name ?? "<null>",
                fbo.Targets?.Length ?? 0);
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            foreach (RenderPassMetadata pass in metadata)
            {
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return pass.PassIndex;
            }

            return int.MinValue;
        }

        private XRTexture GetOrCreateBloomSourceMipView(XRTexture bloomTexture, int sourceMip)
        {
            int mip = Math.Clamp(sourceMip, 0, Math.Max(_activeBloomMaxMip, 0));
            if (!ReferenceEquals(_bloomSourceViewTexture, bloomTexture))
            {
                _bloomSourceMipViews.Clear();
                _bloomSourceViewTexture = bloomTexture;
            }

            if (_bloomSourceMipViews.TryGetValue(mip, out XRTexture? cachedView))
                return cachedView;

            XRTexture view = bloomTexture switch
            {
                XRTexture2D texture2D => CreateBloomSourceMipView(texture2D, mip),
                XRTexture2DArray textureArray => CreateBloomSourceMipView(textureArray, mip),
                _ => bloomTexture
            };

            _bloomSourceMipViews[mip] = view;
            return view;
        }

        private XRTexture2DView CreateBloomSourceMipView(XRTexture2D texture, int mip)
        {
            var view = new XRTexture2DView(
                texture,
                (uint)mip,
                1u,
                texture.SizedInternalFormat,
                array: false,
                texture.MultiSample)
            {
                Name = $"{BloomOutputTextureName}.Mip{mip}.SourceView",
                SamplerName = BloomSourceSamplerName,
                MagFilter = texture.MagFilter,
                MinFilter = ETexMinFilter.Linear,
                UWrap = texture.UWrap,
                VWrap = texture.VWrap,
            };
            return view;
        }

        private XRTexture2DArrayView CreateBloomSourceMipView(XRTexture2DArray texture, int mip)
        {
            var view = new XRTexture2DArrayView(
                texture,
                (uint)mip,
                1u,
                0u,
                Math.Max(texture.Depth, 1u),
                texture.SizedInternalFormat,
                array: texture.Depth > 1,
                texture.MultiSample)
            {
                Name = $"{BloomOutputTextureName}.Mip{mip}.SourceView",
                SamplerName = BloomSourceSamplerName,
                MagFilter = texture.MagFilter,
                MinFilter = ETexMinFilter.Linear,
                UWrap = texture.UWrap,
                VWrap = texture.VWrap,
            };
            return view;
        }

        private static (EPixelInternalFormat InternalFormat, ESizedInternalFormat SizedInternalFormat, EPixelFormat PixelFormat, EPixelType PixelType)
            ResolveBloomTextureFormat(XRTexture? sourceTexture, XRRenderPipelineInstance instance)
        {
            if (sourceTexture is XRTexture2D source2D && source2D.Mipmaps.Length > 0)
            {
                Mipmap2D mip = source2D.Mipmaps[0];
                return (mip.InternalFormat, source2D.SizedInternalFormat, mip.PixelFormat, mip.PixelType);
            }

            if (sourceTexture is XRTexture2DArray sourceArray && sourceArray.Mipmaps is { Length: > 0 } mips)
            {
                Mipmap2D mip = mips[0];
                return (mip.InternalFormat, sourceArray.SizedInternalFormat, mip.PixelFormat, mip.PixelType);
            }

            bool useHdr = instance.EffectiveOutputHDRThisFrame ?? DefaultRenderPipeline.ResolveOutputHDR();
            return useHdr
                ? (EPixelInternalFormat.Rgba16f, ESizedInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
                : (EPixelInternalFormat.Rgba8, ESizedInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        }

        // --- Uniform callbacks ---

        /// <summary>
        /// First downsample level: applies threshold + Karis average.
        /// </summary>
        private void DownsampleLevel1_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(DownsampleLevel1_SettingUniforms), "No active pipeline instance; using safe defaults.");
                SetDefaultDownsampleUniforms(program, true);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                bloom.SetDownsampleUniforms(program, firstLevel: true);
                program.Uniform("DebugSolidOutput", BloomDebugSolidOutput);
                return;
            }

            SetDefaultDownsampleUniforms(program, true);
        }

        /// <summary>
        /// Subsequent downsample levels: no threshold, no Karis.
        /// </summary>
        private void DownsampleLevelN_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(DownsampleLevelN_SettingUniforms), "No active pipeline instance; using safe defaults.");
                SetDefaultDownsampleUniforms(program, false);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                // SourceLOD stays at zero because each material samples through a one-mip view.
                bloom.SetDownsampleUniforms(program, firstLevel: false);
                program.Uniform("DebugSolidOutput", BloomDebugSolidOutput);
                return;
            }

            SetDefaultDownsampleUniforms(program, false);
        }

        private static void SetDefaultDownsampleUniforms(XRRenderProgram program, bool firstLevel)
        {
            // SourceLOD stays at zero because each material samples through a one-mip view.
            // Apply threshold on the first downsample level to extract only bright areas.
            program.Uniform("UseThreshold", firstLevel);
            program.Uniform("BloomThreshold", 0.138f);
            program.Uniform("BloomSoftKnee", 0.5f);
            program.Uniform("BloomIntensity", 0.530f);
            program.Uniform("Luminance", RuntimeEngine.Rendering.Settings.DefaultLuminance);
            program.Uniform("UseKarisAverage", firstLevel);
            program.Uniform("DebugSolidOutput", BloomDebugSolidOutput);
        }

        private void UpsampleFbo_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(UpsampleFbo_SettingUniforms), "No active pipeline instance; using safe defaults.");
                // SourceLOD stays at zero because each material samples through a one-mip view.
                program.Uniform("Radius", 1.0f);
                program.Uniform("Scatter", 0.7f);
                return;
            }

            var camera = instance.RenderState.SceneCamera;
            var bloomStage = camera?.GetPostProcessStageState<BloomSettings>();
            if (bloomStage?.TryGetBacking(out BloomSettings? bloom) == true && bloom is not null)
            {
                bloom.SetUpsampleUniforms(program);
                return;
            }

            // SourceLOD stays at zero because each material samples through a one-mip view.
            program.Uniform("Radius", 1.0f);
            program.Uniform("Scatter", 0.7f);
        }

        private void BloomCopy_SettingUniforms(XRRenderProgram program)
        {
            if (_bloomCopySourceTexture is not null)
                program.Sampler(BloomSourceSamplerName, _bloomCopySourceTexture, 0);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            string bloomTexture = MakeTextureResource(BloomOutputTextureName);

            var copy = context.GetOrCreateSyntheticPass(BloomCopyPassName, ERenderGraphPassStage.Graphics);
            copy.SampleTexture(MakeFboColorResource(InputFBOName));
            copy.UseColorAttachmentMip(
                bloomTexture,
                0u,
                ERenderGraphAccess.Write,
                ERenderPassLoadOp.DontCare,
                ERenderPassStoreOp.Store);

            for (int targetMipLevel = 1; targetMipLevel <= BloomMaxMipmapLevel; targetMipLevel++)
            {
                var downsample = context.GetOrCreateSyntheticPass(GetDownsamplePassName(targetMipLevel), ERenderGraphPassStage.Graphics);
                downsample.SampleTextureMip(bloomTexture, (uint)(targetMipLevel - 1));
                downsample.UseColorAttachmentMip(
                    bloomTexture,
                    (uint)targetMipLevel,
                    ERenderGraphAccess.Write,
                    ERenderPassLoadOp.DontCare,
                    ERenderPassStoreOp.Store);
            }

            for (int sourceMipLevel = BloomMaxMipmapLevel; sourceMipLevel >= 2; sourceMipLevel--)
            {
                int targetMipLevel = sourceMipLevel - 1;
                var upsample = context.GetOrCreateSyntheticPass(GetUpsamplePassName(sourceMipLevel), ERenderGraphPassStage.Graphics);
                upsample.SampleTextureMip(bloomTexture, (uint)sourceMipLevel);
                upsample.UseColorAttachmentMip(
                    bloomTexture,
                    (uint)targetMipLevel,
                    ERenderGraphAccess.ReadWrite,
                    ERenderPassLoadOp.Load,
                    ERenderPassStoreOp.Store);
            }
        }
    }
}
