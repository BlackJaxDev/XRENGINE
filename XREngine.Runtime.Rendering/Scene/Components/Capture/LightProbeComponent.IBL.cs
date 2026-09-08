using System.IO;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
        #region Capture and IBL Methods

        // Convolution is queued after the face viewport has left its render scope.
        // Keep an explicit owner alive for deferred draws, including static probes
        // which never create a capture viewport.
        private XRRenderPipelineInstance? _iblRenderPipeline;
        private Func<bool>? _requiredIblProducer;
        private bool _iblDestroyQueued;
        private bool _staticIblInitializationQueued;
        protected override bool HasPendingCaptureConsumer => _pendingIblOutput is not null;

        protected override XRTextureCube CreateEnvironmentColorCubemap(uint resolution)
            => new(resolution, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                Name = "LightProbeEnvColor",
                SmallestAllowedMipmapLevel = XRTexture.GetSmallestMipmapLevel(resolution, resolution),
                AutoGenerateMipmaps = false,
            };

        protected override bool ShouldEncodeEnvironmentToOctahedralMap()
            => !UseDirectCubemapIblGeneration;

        protected override bool ShouldInitializeCaptureResourcesOnActivate
            => false;

        protected override RenderCapturePolicy CaptureRenderPolicy
            => RenderCapturePolicy.LightProbe;

        protected override RenderPipelineOffscreenIntent AdvancedCaptureIntent
            => RenderPipelineOffscreenIntent.ReflectionProbe();

        protected override void InitializeForCapture()
        {
            if (IsIblProducerQuarantined)
                return;
            base.InitializeForCapture();
            ConfigureCaptureRenderPipelines();

            if (UseDirectCubemapIblGeneration)
            {
                if (EnvironmentTextureCubemap is null)
                    return;

                InitializeDynamicIblResources(
                    EnvironmentTextureCubemap,
                    GetOctaExtent(IrradianceResolution),
                    GetOctaExtent(Resolution));
                return;
            }

            if (EnvironmentTextureOctahedral is null)
                return;

            InitializeOctaIblResources(
                EnvironmentTextureOctahedral,
                "Scene3D\\IrradianceConvolutionOcta.fs",
                "Scene3D\\PrefilterOcta.fs",
                (int)Math.Max(EnvironmentTextureOctahedral.Width, EnvironmentTextureOctahedral.Height),
                GetOctaExtent(IrradianceResolution),
                GetOctaExtent(Resolution));
        }

        private void ConfigureCaptureRenderPipelines()
        {
            foreach (XRViewport? viewport in Viewports)
            {
                if (viewport is null)
                    continue;

                viewport.ApplyCapturePolicy(CaptureRenderPolicy);
                viewport.SetRenderPipelineFromCamera = false;
            }
        }

        public void InitializeStatic()
        {
            if (IsIblProducerQuarantined)
                return;
            if (!RuntimeEngine.IsRenderThread)
            {
                RuntimeEngine.EnqueueMainThreadTask(InitializeStatic, "LightProbe.InitializeStatic");
                return;
            }

            if (_pendingIblOutput is not null)
            {
                if (!_staticIblInitializationQueued)
                {
                    _staticIblInitializationQueued = true;
                    RuntimeEngine.AddRenderThreadCoroutine(ResumeStaticIblInitialization,
                        "LightProbe.ResumeStaticInitialization", RenderThreadJobKind.RenderPipelineResource);
                }
                return;
            }

            if (EnvironmentTextureEquirect is null)
                return;

            InitializeOctaIblResources(
                EnvironmentTextureEquirect,
                "Scene3D\\IrradianceConvolutionEquirectOcta.fs",
                "Scene3D\\PrefilterEquirectOcta.fs",
                (int)Math.Max(EnvironmentTextureEquirect.Width, EnvironmentTextureEquirect.Height),
                GetOctaExtent(IrradianceResolution),
                GetOctaExtent(Resolution));

            SynchronizeCaptureTextureWrites();
            CompleteIblGenerationAttempt(releaseTransientEnvironmentTexturesOnSuccess: false);
        }

        private void InitializeDynamicIblResources(
            XRTextureCube sourceCubemap,
            uint irradianceOctaExtent,
            uint prefilterOctaExtent)
        {
            DestroyIblMipTargets();
            // Directly convolve from the captured cubemap into the final octahedral outputs.
            DestroyCubemapConvolutionResources();
            _useCubemapConvolution = false;
            _prefilterSourceDimension = Math.Max(1, (int)sourceCubemap.Extent);

            RenderingParameters renderParams = CreateIblRenderParams();
            XRShader fullscreenVertex = GetFullscreenTriVertexShader();
            _pendingIrradianceExtent = irradianceOctaExtent;
            _pendingPrefilterExtent = prefilterOctaExtent;

            EnsureProbeFullscreenMaterial(
                ref _irradianceFBO,
                [],
                sourceCubemap,
                fullscreenVertex,
                GetIrradianceCubemapToOctaShader(),
                renderParams);

            EnsureProbeFullscreenMaterial(
                ref _prefilterFBO,
                CreateCubemapPrefilterShaderVars(_prefilterSourceDimension),
                sourceCubemap,
                fullscreenVertex,
                GetPrefilterCubemapToOctaShader(),
                renderParams);

            ConfigureIrradianceFramebufferTarget();
            ConfigurePrefilterFramebufferTarget();

            _irradianceSourceTexture = sourceCubemap;
            _prefilterSourceTexture = sourceCubemap;

        }

        private void InitializeOctaIblResources(
            XRTexture sourceTexture,
            string irradianceShaderPath,
            string prefilterShaderPath,
            int sourceDimension,
            uint irradianceExtent,
            uint prefilterExtent)
        {
            DestroyIblMipTargets();
            DestroyCubemapConvolutionResources();
            _useCubemapConvolution = false;
            _pendingIrradianceExtent = irradianceExtent;
            _pendingPrefilterExtent = prefilterExtent;

            ShaderVar[] prefilterVars = CreatePrefilterShaderVars(sourceDimension);
            _prefilterSourceDimension = Math.Max(1, sourceDimension);

            RenderingParameters renderParams = CreateIblRenderParams();

            XRShader fullscreenVertex = GetFullscreenTriVertexShader();
            XRShader irradianceFragment = ShaderHelper.LoadEngineShader(irradianceShaderPath, EShaderType.Fragment);
            XRShader prefilterFragment = ShaderHelper.LoadEngineShader(prefilterShaderPath, EShaderType.Fragment);

            EnsureProbeFullscreenMaterial(
                ref _irradianceFBO,
                [],
                sourceTexture,
                fullscreenVertex,
                irradianceFragment,
                renderParams);

            EnsureProbeFullscreenMaterial(
                ref _prefilterFBO,
                prefilterVars,
                sourceTexture,
                fullscreenVertex,
                prefilterFragment,
                renderParams);

            ConfigureIrradianceFramebufferTarget();
            ConfigurePrefilterFramebufferTarget();

            _irradianceSourceTexture = sourceTexture;
            _prefilterSourceTexture = sourceTexture;

        }


        private XRMaterial EnsureProbeFullscreenMaterial(
            ref XRQuadFrameBuffer? fbo,
            ShaderVar[] parameters,
            XRTexture sourceTexture,
            XRShader vertexShader,
            XRShader fragmentShader,
            RenderingParameters renderParams)
        {
            if (fbo is null)
            {
                XRMaterial createdMaterial = new(parameters, [sourceTexture], vertexShader, fragmentShader)
                {
                    RenderOptions = renderParams,
                };

                fbo = new XRQuadFrameBuffer(createdMaterial);
                ConfigureProbeFullscreenFramebuffer(fbo);
                return createdMaterial;
            }

            XRMaterial material = fbo.Material ?? fbo.FullScreenMesh.Material ?? new XRMaterial();
            material.Parameters = [.. parameters];
            material.Textures = [sourceTexture];
            material.Shaders = [vertexShader, fragmentShader];
            material.RenderOptions = renderParams;

            if (!ReferenceEquals(fbo.Material, material))
                fbo.Material = material;
            if (!ReferenceEquals(fbo.FullScreenMesh.Material, material))
                fbo.FullScreenMesh.Material = material;

            ConfigureProbeFullscreenFramebuffer(fbo);

            return material;
        }

        private static void ConfigureProbeFullscreenFramebuffer(XRQuadFrameBuffer fbo)
        {
            fbo.FullScreenMesh.SetShaderPipelinesAllowedForAllVersions(false);
        }

        private void ConfigureIrradianceFramebufferTarget()
        {
            if (_irradianceFBO is null)
                return;

            _irradianceFBO.Name = "LightProbe.Irradiance";
            _irradianceFBO.FullScreenMesh.Name = "LightProbe.Irradiance";

            if (IrradianceTexture is not null)
                _irradianceFBO.SetRenderTargets((IrradianceTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            _irradianceFBO.SettingUniforms -= BindIrradianceSourceSampler;
            _irradianceFBO.SettingUniforms += BindIrradianceSourceSampler;
        }

        private void ConfigurePrefilterFramebufferTarget()
        {
            if (_prefilterFBO is null)
                return;

            _prefilterFBO.Name = "LightProbe.Prefilter";
            _prefilterFBO.FullScreenMesh.Name = "LightProbe.Prefilter";

            if (PrefilterTexture is not null)
                _prefilterFBO.SetRenderTargets((PrefilterTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            _prefilterFBO.SettingUniforms -= BindPrefilterSourceSampler;
            _prefilterFBO.SettingUniforms += BindPrefilterSourceSampler;
        }

        private void DestroyCubemapConvolutionResources()
        {
            _irradianceCubeFBO?.Destroy();
            _irradianceCubeFBO = null;
            _prefilterCubeFBO?.Destroy();
            _prefilterCubeFBO = null;
            _irradianceTextureCubemap?.Destroy();
            _irradianceTextureCubemap = null;
            _prefilterTextureCubemap?.Destroy();
            _prefilterTextureCubemap = null;
        }

        private void DestroyIblResources()
        {
            if (!RuntimeEngine.IsRenderThread)
            {
                if (!_iblDestroyQueued)
                {
                    _iblDestroyQueued = true;
                    RuntimeEngine.AddRenderThreadCoroutine(DrainIblResourceDestruction,
                        "LightProbe.DestroyIblResources", RenderThreadJobKind.RenderPipelineResource);
                }
                return;
            }
            CancelPendingIblGenerationRetry();
            if (_quarantinedIblOutput is not null)
                return;
            if (_pendingIblOutput is { } pending)
            {
                if (pending.IsSubmittedWriterUncertain())
                {
                    QuarantineIblProducer(AbstractRenderer.Current!, pending);
                    return;
                }
                if (!pending.IsWriterComplete() && !pending.IsWriterRejected())
                {
                    if (!_iblDestroyQueued)
                    {
                        _iblDestroyQueued = true;
                        RuntimeEngine.AddRenderThreadCoroutine(DrainIblResourceDestruction,
                            "LightProbe.DestroyIblResources", RenderThreadJobKind.RenderPipelineResource);
                    }
                    return;
                }
            }
            DestroyIblMipTargets();

            if (_irradianceFBO is not null)
                _irradianceFBO.SettingUniforms -= BindIrradianceSourceSampler;
            if (_prefilterFBO is not null)
                _prefilterFBO.SettingUniforms -= BindPrefilterSourceSampler;

            _irradianceSourceTexture = null;
            _prefilterSourceTexture = null;
            _useCubemapConvolution = false;

            _irradianceFBO?.Destroy();
            _irradianceFBO = null;
            _prefilterFBO?.Destroy();
            _prefilterFBO = null;
            DestroyCubemapConvolutionResources();
            _iblRenderPipeline?.DestroyCache();
            _iblRenderPipeline = null;

            _pendingIblOutput?.ReleaseProducer();
            _pendingIblOutput = null;
            _activeIblOutput?.ReleaseProducer();
            _activeIblOutput = null;
            IrradianceTexture = null;
            PrefilterTexture = null;
            IblTexturesValid = false;
            CaptureVersion = 0;
            _previewSphereDirty = true;
        }

        private bool DrainIblResourceDestruction()
        {
            if (_pendingIblOutput is { } pending && !pending.IsSubmittedWriterUncertain() &&
                !pending.IsWriterComplete() && !pending.IsWriterRejected())
                return false;
            _iblDestroyQueued = false;
            DestroyIblResources();
            return true;
        }

        private bool ResumeStaticIblInitialization()
        {
            if (!IsActiveInHierarchy || IsIblProducerQuarantined)
            {
                _staticIblInitializationQueued = false;
                return true;
            }
            ResolvePendingIblOutputVersion();
            if (_pendingIblOutput is not null)
                return false;
            _staticIblInitializationQueued = false;
            InitializeStatic();
            return true;
        }

        private bool GenerateIrradianceInternal()
        {
            XRTexture2D? target = _pendingIblOutput?.Irradiance;
            if (_irradianceFBO is null || target is null)
                return false;

            if (_useCubemapConvolution)
            {
                if (_irradianceCubeFBO is null || _irradianceTextureCubemap is null)
                    return false;

                if (!RenderIrradianceCubemapInternal())
                    return false;
                SynchronizeCaptureTextureWrites();
            }

            XRQuadFrameBuffer[] mipTargets = GetIblMipTargets(prefilter: false, target);
            for (int mip = 0; mip < mipTargets.Length; mip++)
                if (!RunFullscreenProbePass(mipTargets[mip], Math.Max(1, (int)target.Width >> mip), Math.Max(1, (int)target.Height >> mip)))
                    return false;
            return true;
        }

        private bool GeneratePrefilterInternal()
        {
            XRTexture2D? target = _pendingIblOutput?.PrefilteredRadiance;
            if (_prefilterFBO is null || target is null)
                return false;

            if (_useCubemapConvolution)
            {
                if (_prefilterCubeFBO is null || _prefilterTextureCubemap is null)
                    return false;

                if (!RenderPrefilterCubemapInternal())
                    return false;
                SynchronizeCaptureTextureWrites();
            }

            int baseExtent = (int)Math.Max(target.Width, target.Height);
            XRQuadFrameBuffer[] mipTargets = GetIblMipTargets(prefilter: true, target);
            int maxMipLevels = mipTargets.Length;
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipWidth = Math.Max(1, baseExtent >> mip);
                int mipHeight = Math.Max(1, baseExtent >> mip);

                if (!RunFullscreenProbePass(mipTargets[mip], mipWidth, mipHeight))
                    return false;
            }

            return true;
        }

        private bool RenderIrradianceCubemapInternal()
        {
            if (_irradianceCubeFBO is null || _irradianceTextureCubemap is null)
                return false;

            if (!TryPrepareProbePass(_irradianceCubeFBO.FullScreenCubeMesh, _irradianceCubeFBO.Name))
                return false;

            int extent = Math.Max(1, (int)_irradianceTextureCubemap.Extent);
            using StateObject? renderArea = RuntimeEngine.Rendering.State.RenderingPipelineState?.PushRenderArea(extent, extent);
            if (renderArea is null)
                AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(extent, extent)));

            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                _irradianceCubeFBO.SetRenderTargets((_irradianceTextureCubemap, EFrameBufferAttachment.ColorAttachment0, 0, faceIndex));
                using var bindScope = _irradianceCubeFBO.BindForWritingState();
                RuntimeEngine.Rendering.State.ClearByBoundFBO(true, false, false);
                if (!_irradianceCubeFBO.RenderFullscreen((ECubemapFace)faceIndex))
                    return false;
            }

            return true;
        }

        private bool RenderPrefilterCubemapInternal()
        {
            if (_prefilterCubeFBO is null || _prefilterTextureCubemap is null || EnvironmentTextureCubemap is null)
                return false;

            if (!TryPrepareProbePass(_prefilterCubeFBO.FullScreenCubeMesh, _prefilterCubeFBO.Name))
                return false;

            int maxMipLevels = _prefilterTextureCubemap.Mipmaps.Length;
            int baseExtent = Math.Max(1, (int)_prefilterTextureCubemap.Extent);
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipExtent = Math.Max(1, baseExtent >> mip);
                float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);

                _prefilterCubeFBO.Material?.SetFloat(0, roughness);
                _prefilterCubeFBO.Material?.SetInt(1, _prefilterSourceDimension);

                using StateObject? renderArea = RuntimeEngine.Rendering.State.RenderingPipelineState?.PushRenderArea(mipExtent, mipExtent);
                if (renderArea is null)
                    AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipExtent, mipExtent)));

                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                {
                    _prefilterCubeFBO.SetRenderTargets((_prefilterTextureCubemap, EFrameBufferAttachment.ColorAttachment0, mip, faceIndex));
                    using var bindScope = _prefilterCubeFBO.BindForWritingState();
                    RuntimeEngine.Rendering.State.ClearByBoundFBO(true, false, false);
                    if (!_prefilterCubeFBO.RenderFullscreen((ECubemapFace)faceIndex))
                        return false;
                }
            }

            return true;
        }

        private void BindIrradianceSourceSampler(XRRenderProgram program)
            => BindIblSourceSampler(program, _irradianceSourceTexture);

        private void BindPrefilterSourceSampler(XRRenderProgram program)
            => BindIblSourceSampler(program, _prefilterSourceTexture);

        private static void BindIblSourceSampler(XRRenderProgram program, XRTexture? sourceTexture)
        {
            if (sourceTexture is null)
                return;

            sourceTexture.Bind();
            program.Sampler("Texture0", sourceTexture, 0);
        }

        private bool CompleteIblGenerationAttempt(
            bool releaseTransientEnvironmentTexturesOnSuccess,
            bool scheduleRetryOnFailure = true,
            bool logFailure = true)
        {
            bool previousLightProbePass = RuntimeEngine.Rendering.State.IsLightProbePass;
            RuntimeEngine.Rendering.State.IsLightProbePass = true;
            try
            {
                return CompleteIblGenerationAttemptCore(
                    releaseTransientEnvironmentTexturesOnSuccess,
                    scheduleRetryOnFailure,
                    logFailure);
            }
            finally
            {
                RuntimeEngine.Rendering.State.IsLightProbePass = previousLightProbePass;
            }
        }

        private bool CompleteIblGenerationAttemptCore(
            bool releaseTransientEnvironmentTexturesOnSuccess,
            bool scheduleRetryOnFailure,
            bool logFailure)
        {
            if (IsIblProducerQuarantined)
                return false;
            // Deferred mesh requests require a pipeline owner, not merely a pass
            // number. The face viewport's scope has already ended here.
            XRRenderPipelineInstance pipeline = _iblRenderPipeline ??= new(
                RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline());
            using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(pipeline);
            using IDisposable passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.PreRender);
            // The scope freezes the auxiliary graph publication used by both
            // the deferred draws and their ordered completion marker.
            using IDisposable? resourceScope = AbstractRenderer.Current?.EnterRenderPipelineFrameResourceScope(pipeline, viewport: null);
            if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan && resourceScope is null)
                throw new InvalidOperationException("Vulkan probe convolution requires an explicit render-graph resource scope.");
            if (!BeginIblOutputVersion())
            {
                _iblRegenerationRequested = true;
                _deferredReleaseTransientEnvironmentTextures |= releaseTransientEnvironmentTexturesOnSuccess;
                return false;
            }

            AbstractRenderer renderer = AbstractRenderer.Current
                ?? throw new InvalidOperationException("IBL convolution requires an active renderer.");
            bool success = renderer.TryExecuteRequiredGpuProducerBatch(
                _requiredIblProducer ??= GenerateRequiredIblOutputs,
                out XRGpuFence? retentionFence,
                out Exception? failure);
            LightProbeIblOutputGeneration produced = _pendingIblOutput
                ?? throw new InvalidOperationException("Required IBL output ownership was lost.");
            if (retentionFence is not null)
                produced.ArmWriterFence(retentionFence);
            else
            {
                _pendingIblOutput = null;
                if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan)
                    produced.DiscardUnwritten(); // Atomic Vulkan failure rolled back every queued writer.
                else
                    QuarantineIblProducer(renderer, produced);
                success = false;
            }
            if (failure is not null && logFailure)
                Debug.LogWarning($"[LightProbe] Required IBL producer failed: {failure}");
            if (success)
            {
                LightProbeIblOutputGeneration pending = _pendingIblOutput
                    ?? throw new InvalidOperationException("A fenced IBL output generation was unexpectedly cleared.");
                pending.MarkOutputValid();
                _pendingReleaseTransientEnvironmentTextures = releaseTransientEnvironmentTexturesOnSuccess;
                CancelPendingIblGenerationRetry();
            }
            else
            {
                _previewSphereDirty = true;
                CachePreviewSphere();
                bool retryScheduled = scheduleRetryOnFailure
                    && !IsIblProducerQuarantined
                    && IsActiveInHierarchy
                    && HasIblGenerationRetryResources()
                    && ScheduleIblGenerationRetry(releaseTransientEnvironmentTexturesOnSuccess);

                if (retryScheduled)
                {
                    Debug.Lighting(
                        $"[LightProbe] IBL generation deferred for '{SceneNode?.Name ?? GetType().Name}'. " +
                        "The complete convolution batch is not ready. " +
                        $"Retrying up to {MaxIblGenerationRetryAttempts} time(s) while captured environment textures are retained.");
                }
                else if (logFailure)
                {
                    Debug.LogWarning(
                        $"[LightProbe] IBL generation failed for '{SceneNode?.Name ?? GetType().Name}'. " +
                        "The complete convolution batch was rejected. " +
                        "Keeping captured environment textures and excluding this probe from GI until a valid IBL pass completes.");
                }
                // The failed pair remains pending until its exact writer fence
                // resolves, so partial queued writes cannot be reused or destroyed.
            }

            return success;
        }

        private bool GenerateRequiredIblOutputs()
        {
            bool irradiance = GenerateIrradianceInternal();
            bool prefilter = GeneratePrefilterInternal();
            return irradiance && prefilter;
        }

        private bool BeginIblOutputVersion()
        {
            if (_pendingIblOutput is not null || _pendingIrradianceExtent == 0u || _pendingPrefilterExtent == 0u)
                return false;
            unchecked { ++_nextIblOutputGeneration; }
            if (_nextIblOutputGeneration == 0u)
                _nextIblOutputGeneration = 1u;
            XRTexture2D irradiance = CreateIrradianceTexture(_pendingIrradianceExtent);
            try
            {
                _pendingIblOutput = new LightProbeIblOutputGeneration(
                    _nextIblOutputGeneration,
                    irradiance,
                    CreatePrefilterTexture(_pendingPrefilterExtent));
            }
            catch
            {
                irradiance.Destroy();
                throw;
            }
            return true;
        }

        private void ResolvePendingIblOutputVersion()
        {
            LightProbeIblOutputGeneration? pending = _pendingIblOutput;
            if (pending is null)
                return;
            if (pending.IsSubmittedWriterUncertain())
            {
                QuarantineIblProducer(AbstractRenderer.Current!, pending);
                return;
            }
            if (pending.IsWriterRejected())
            {
                _pendingIblOutput = null;
                pending.ReleaseProducer();
                ScheduleIblRetryAfterRejectedWriter();
                return;
            }
            if (!pending.IsWriterComplete())
                return;
            if (!pending.OutputValid)
            {
                _pendingIblOutput = null;
                pending.ReleaseProducer();
                ScheduleIblRetryAfterRejectedWriter();
                return;
            }

            LightProbeIblOutputGeneration? previous = _activeIblOutput;
            _activeIblOutput = pending;
            _pendingIblOutput = null;
            IrradianceTexture = pending.Irradiance;
            PrefilterTexture = pending.PrefilteredRadiance;
            IblTexturesValid = true;
            CaptureVersion = pending.Generation;
            _previewSphereDirty = true;
            previous?.ReleaseProducer();
            if (_pendingReleaseTransientEnvironmentTextures)
                ReleaseTransientEnvironmentTexturesAfterIblGeneration();
            _pendingReleaseTransientEnvironmentTextures = false;
            ScheduleDeferredIblRegeneration();
        }

        private void ScheduleIblRetryAfterRejectedWriter()
        {
            if (IsActiveInHierarchy && HasIblGenerationRetryResources())
                ScheduleIblGenerationRetry(_pendingReleaseTransientEnvironmentTextures);
            _pendingReleaseTransientEnvironmentTextures = false;
            ScheduleDeferredIblRegeneration();
        }

        private void ScheduleDeferredIblRegeneration()
        {
            if (!_iblRegenerationRequested)
                return;
            bool releaseTransient = _deferredReleaseTransientEnvironmentTextures;
            _iblRegenerationRequested = false;
            _deferredReleaseTransientEnvironmentTextures = false;
            if (IsActiveInHierarchy && HasIblGenerationRetryResources())
                ScheduleIblGenerationRetry(releaseTransient);
        }

        private void DiscardPendingIblOutputVersion()
        {
            LightProbeIblOutputGeneration? pending = _pendingIblOutput;
            _pendingIblOutput = null;
            pending?.ReleaseProducer();
        }

        private bool HasIblGenerationRetryResources()
        {
            if (_irradianceFBO is null || _prefilterFBO is null ||
                _irradianceSourceTexture is null || _prefilterSourceTexture is null)
            {
                return false;
            }

            return !_useCubemapConvolution ||
                (_irradianceCubeFBO is not null && _irradianceTextureCubemap is not null &&
                 _prefilterCubeFBO is not null && _prefilterTextureCubemap is not null);
        }

        private bool ScheduleIblGenerationRetry(bool releaseTransientEnvironmentTexturesOnSuccess)
        {
            _releaseTransientEnvironmentTexturesOnIblRetrySuccess |= releaseTransientEnvironmentTexturesOnSuccess;

            if (_iblRetryTimer.IsRunning)
                return false;

            _iblRetryAttempts = 0;
            _iblRetryTimer.StartMultiFire(
                RetryPendingIblGeneration,
                IblGenerationRetryInterval,
                MaxIblGenerationRetryAttempts,
                IblGenerationRetryInterval,
                ETickGroup.Late,
                (int)ETickOrder.Scene);
            return true;
        }

        private void CancelPendingIblGenerationRetry()
        {
            _iblRetryTimer.Cancel();
            _iblRetryAttempts = 0;
            _releaseTransientEnvironmentTexturesOnIblRetrySuccess = false;
        }

        private void RetryPendingIblGeneration()
        {
            if (!RuntimeEngine.IsRenderThread)
            {
                if (_iblRetryQueuedOnRenderThread)
                    return;

                _iblRetryQueuedOnRenderThread = true;
                RuntimeEngine.EnqueueMainThreadTask(() =>
                {
                    _iblRetryQueuedOnRenderThread = false;
                    RetryPendingIblGeneration();
                }, "LightProbe.RetryPendingIblGeneration");
                return;
            }

            if (!IsActiveInHierarchy || !HasIblGenerationRetryResources())
            {
                CancelPendingIblGenerationRetry();
                return;
            }

            _iblRetryAttempts++;
            bool finalAttempt = _iblRetryAttempts >= MaxIblGenerationRetryAttempts;
            RuntimeEngine.Rendering.State.IsLightProbePass = true;

            try
            {
                bool success = CompleteIblGenerationAttempt(
                    _releaseTransientEnvironmentTexturesOnIblRetrySuccess,
                    scheduleRetryOnFailure: false,
                    logFailure: finalAttempt);

                if (success || finalAttempt)
                    CancelPendingIblGenerationRetry();
            }
            finally
            {
                RuntimeEngine.Rendering.State.IsLightProbePass = false;
            }
        }

        private static bool TryPrepareProbePass(XRMeshRenderer mesh, string? name)
        {
            if (mesh.TryPrepareForRendering(out string reason, forceNoStereo: true))
                return true;
            Debug.RenderingWarningEvery($"LightProbe.Prepare.{mesh.ID}", TimeSpan.FromSeconds(1),
                "[LightProbe] Required pass '{0}' is not ready: {1}. {2}",
                name ?? "<unnamed>", reason, mesh.GetLastPrepareDetail(forceNoStereo: true));
            return false;
        }

        private static bool RunFullscreenProbePass(XRQuadFrameBuffer fbo, int width, int height)
        {
            if (!TryPrepareProbePass(fbo.FullScreenMesh, fbo.Name))
                return false;

            var pipelineState = RuntimeEngine.Rendering.State.RenderingPipelineState;
            BoundingRectangle previousCrop = pipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
            bool hadCrop = previousCrop.Width > 0 && previousCrop.Height > 0;
            bool rendered;

            using (fbo.BindForWritingState())
            {
                AbstractRenderer.Current?.SetCroppingEnabled(false);

                using StateObject? renderArea = pipelineState?.PushRenderArea(width, height);
                if (renderArea is null)
                    AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));

                RuntimeEngine.Rendering.State.ClearColor(ColorF4.Black);
                RuntimeEngine.Rendering.State.ClearByBoundFBO();
                rendered = fbo.Render(null, true);
            }

            if (hadCrop)
            {
                AbstractRenderer.Current?.SetCroppingEnabled(true);
                AbstractRenderer.Current?.CropRenderArea(previousCrop);
            }

            return rendered;
        }

        private void ReleaseTransientEnvironmentTexturesAfterIblGeneration()
        {
            if (!ReleaseTransientEnvironmentTexturesAfterCapture)
                return;

            if (PreviewDisplay == ERenderPreview.Environment
                && (PreviewEnabled || (AutoShowPreviewOnSelect && IsSceneNodeSelected())))
            {
                CachePreviewSphere();
                return;
            }

            ReleaseCapturedEnvironmentTextures(releaseCubemap: true, releaseOctahedral: true);
            CachePreviewSphere();
        }

        public override void Render()
        {
            if (IsIblProducerQuarantined)
                return;
            if (CaptureRenderPolicy.RenderShadows)
                _registeredWorld?.Lights.EnsureShadowMapsCurrentForCapture(false);
            RuntimeEngine.Rendering.State.IsLightProbePass = true;

            try
            {
                base.Render();

                // Only run IBL generation + version bump when a complete cubemap cycle finishes.
                // Progressive mode renders one face per Render() call; the base re-enqueues
                // for remaining faces automatically.
                if (!LastRenderCompletedCycle)
                    return;

                SynchronizeCaptureTextureWrites();
                CompleteIblGenerationAttempt(releaseTransientEnvironmentTexturesOnSuccess: true);
            }
            finally
            {
                RuntimeEngine.Rendering.State.IsLightProbePass = false;
            }
        }

        /// <summary>
        /// Resolves the writer with a current renderer context. The next world
        /// swap captures this publication into immutable global-resource input.
        /// </summary>
        internal void PublishCompletedIblOutput()
            => ResolvePendingIblOutputVersion();

        /// <summary>Returns one coherent active IBL generation for publication capture.</summary>
        public bool TryGetActiveIblOutput(out LightProbeIblOutputGeneration generation)
        {
            generation = _activeIblOutput!;
            return generation is not null && IblTexturesValid && CaptureVersion != 0u;
        }

        public override ECaptureStepResult ExecuteCaptureFace(int faceIndex)
        {
            if (IsIblProducerQuarantined)
                return ECaptureStepResult.Cancelled;
            RuntimeEngine.Rendering.State.IsLightProbePass = true;
            try
            {
                return base.ExecuteCaptureFace(faceIndex);
            }
            finally
            {
                RuntimeEngine.Rendering.State.IsLightProbePass = false;
            }
        }

        public override ECaptureStepResult FinalizeCubemapCapture()
        {
            if (IsIblProducerQuarantined)
                return ECaptureStepResult.Cancelled;
            if (CaptureRenderPolicy.RenderShadows)
                _registeredWorld?.Lights.EnsureShadowMapsCurrentForCapture(false);
            RuntimeEngine.Rendering.State.IsLightProbePass = true;

            try
            {
                ECaptureStepResult result = base.FinalizeCubemapCapture();
                if (result != ECaptureStepResult.Completed)
                    return result;
                SynchronizeCaptureTextureWrites();
                CompleteIblGenerationAttempt(releaseTransientEnvironmentTexturesOnSuccess: true);
                return ECaptureStepResult.Completed;
            }
            finally
            {
                RuntimeEngine.Rendering.State.IsLightProbePass = false;
            }
        }

        #endregion

        #region Static Helper Methods

        private static ShaderVar[] CreatePrefilterShaderVars(int sourceDimension)
            =>
            [
                new ShaderFloat(0.0f, "Roughness"),
                new ShaderInt(Math.Max(1, sourceDimension), "SourceDim"),
            ];

        private static ShaderVar[] CreateCubemapPrefilterShaderVars(int sourceDimension)
            =>
            [
                new ShaderFloat(0.0f, "Roughness"),
                new ShaderInt(Math.Max(1, sourceDimension), "CubemapDim"),
            ];

        private static Vector3 ClampHalfExtents(Vector3 extents)
            => new(
                MathF.Max(0.0001f, MathF.Abs(extents.X)),
                MathF.Max(0.0001f, MathF.Abs(extents.Y)),
                MathF.Max(0.0001f, MathF.Abs(extents.Z)));

        private static Vector3 ClampBoxInnerExtents(Vector3 inner, Vector3 outer)
            => new(
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.X), outer.X)),
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.Y), outer.Y)),
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.Z), outer.Z)));

        private static float ClampNonNegative(float value, float maxInclusive)
            => MathF.Max(0.0f, MathF.Min(value, maxInclusive));

        private static uint GetOctaExtent(uint baseResolution)
            => Math.Max(1u, baseResolution * OctahedralResolutionMultiplier);

        private static XRTexture2D CreateIrradianceTexture(uint extent)
            => ConfigureFullMipChain(new XRTexture2D(extent, extent, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                AutoGenerateMipmaps = false,
                Name = "LightProbeIrradianceOcta",
            });

        private static XRTexture2D CreatePrefilterTexture(uint extent)
            => ConfigureFullMipChain(new XRTexture2D(extent, extent, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                AutoGenerateMipmaps = false,
                Name = "LightProbePrefilterOcta",
            });

        private static XRTexture2D ConfigureFullMipChain(XRTexture2D texture)
        {
            texture.SmallestAllowedMipmapLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height);
            return texture;
        }

        private static RenderingParameters CreateIblRenderParams()
            => new()
            {
                DepthTest = new() { Enabled = ERenderParamUsage.Disabled },
                StencilTest = new() { Enabled = ERenderParamUsage.Disabled },
                CullMode = ECullMode.None,
                WriteRed = true,
                WriteGreen = true,
                WriteBlue = true,
                WriteAlpha = true,
            };

        private static XRShader GetFullscreenTriVertexShader()
            => s_fullscreenTriVertexShader ??= ShaderHelper.LoadEngineShader("Scene3D\\FullscreenTri.vs", EShaderType.Vertex);

        private static XRShader GetFullscreenCubeVertexShader()
            => s_fullscreenCubeVertexShader ??= LoadEngineShaderOrFallback("Scene3D\\Cubemap.vs", EShaderType.Vertex, FullscreenCubeVertexShaderSource);

        private static XRShader GetCubemapToOctaShader()
            => s_cubemapToOctaShader ??= LoadEngineShaderOrFallback("Scene3D\\CubemapToOctahedron.fs", EShaderType.Fragment, CubemapToOctaShaderSource);

        private static XRShader GetIrradianceCubemapShader()
            => s_irradianceCubemapFragmentShader ??= LoadEngineShaderOrFallback("Scene3D\\IrradianceConvolution.fs", EShaderType.Fragment, IrradianceCubemapFragmentShaderSource);

        private static XRShader GetIrradianceCubemapToOctaShader()
            => s_irradianceCubemapToOctaFragmentShader ??= ShaderHelper.LoadEngineShader("Scene3D\\IrradianceConvolutionCubemapOcta.fs", EShaderType.Fragment);

        private static XRShader GetPrefilterCubemapShader()
            => s_prefilterCubemapFragmentShader ??= LoadEngineShaderOrFallback("Scene3D\\Prefilter.fs", EShaderType.Fragment, PrefilterCubemapFragmentShaderSource);

        private static XRShader GetPrefilterCubemapToOctaShader()
            => s_prefilterCubemapToOctaFragmentShader ??= ShaderHelper.LoadEngineShader("Scene3D\\PrefilterCubemapOcta.fs", EShaderType.Fragment);

        private static XRShader LoadEngineShaderOrFallback(string relativePath, EShaderType type, string fallbackSource)
        {
            try
            {
                return ShaderHelper.LoadEngineShader(relativePath, type);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LightProbe] Falling back to embedded shader source for '{relativePath}': {ex.Message}");
                string shaderPath = GetFallbackShaderPath(relativePath);
                return new XRShader(type, new TextFile(shaderPath) { Text = fallbackSource });
            }
        }

        private static string GetFallbackShaderPath(string relativePath)
        {
            string normalizedRelativePath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine("Build", "CommonAssets", "Shaders", normalizedRelativePath));
        }

        #endregion
    }
}
