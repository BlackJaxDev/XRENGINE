using System.IO;
using System.Numerics;
using XREngine.Core.Files;
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
                AutoGenerateMipmaps = false,
            };

        protected override bool ShouldEncodeEnvironmentToOctahedralMap()
            => !UseDirectCubemapIblGeneration;

        protected override bool ShouldInitializeCaptureResourcesOnActivate
            => false;

        protected override void InitializeForCapture()
        {
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

                if (viewport.Camera is not null)
                {
                    viewport.Camera.OutputHDROverride = true;
                    viewport.Camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
                    viewport.Camera.MsaaSampleCountOverride = 1u;
                    viewport.Camera.TsrRenderScaleOverride = 1.0f;
                }

                viewport.RenderPipeline ??= Engine.Rendering.NewRenderPipeline();
                viewport.SetRenderPipelineFromCamera = false;
            }
        }

        public void InitializeStatic()
        {
            if (EnvironmentTextureEquirect is null)
                return;

            InitializeOctaIblResources(
                EnvironmentTextureEquirect,
                "Scene3D\\IrradianceConvolutionEquirectOcta.fs",
                "Scene3D\\PrefilterEquirectOcta.fs",
                (int)Math.Max(EnvironmentTextureEquirect.Width, EnvironmentTextureEquirect.Height),
                GetOctaExtent(IrradianceResolution),
                GetOctaExtent(Resolution));
        }

        private void InitializeDynamicIblResources(
            XRTextureCube sourceCubemap,
            uint irradianceOctaExtent,
            uint prefilterOctaExtent)
        {
            // Directly convolve from the captured cubemap into the final octahedral outputs.
            DestroyCubemapConvolutionResources();
            _useCubemapConvolution = false;
            _prefilterSourceDimension = Math.Max(1, (int)sourceCubemap.Extent);

            RenderingParameters renderParams = CreateIblRenderParams();
            XRShader fullscreenVertex = GetFullscreenTriVertexShader();
            bool outputsRecreated = false;

            outputsRecreated |= EnsureIblOutputTexture(ref _irradianceTexture, irradianceOctaExtent, CreateIrradianceTexture);
            outputsRecreated |= EnsureIblOutputTexture(ref _prefilterTexture, prefilterOctaExtent, CreatePrefilterTexture);

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

            if (outputsRecreated)
            {
                _previewSphereDirty = true;
                CachePreviewSphere();
            }
        }

        private void InitializeOctaIblResources(
            XRTexture sourceTexture,
            string irradianceShaderPath,
            string prefilterShaderPath,
            int sourceDimension,
            uint irradianceExtent,
            uint prefilterExtent)
        {
            DestroyCubemapConvolutionResources();
            _useCubemapConvolution = false;
            bool outputsRecreated = false;
            outputsRecreated |= EnsureIblOutputTexture(ref _irradianceTexture, irradianceExtent, CreateIrradianceTexture);
            outputsRecreated |= EnsureIblOutputTexture(ref _prefilterTexture, prefilterExtent, CreatePrefilterTexture);

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

            if (outputsRecreated)
            {
                _previewSphereDirty = true;
                CachePreviewSphere();
            }
        }

        private bool EnsureIblOutputTexture(ref XRTexture2D? texture, uint extent, Func<uint, XRTexture2D> factory)
        {
            if (texture is not null && texture.Width == extent && texture.Height == extent)
                return false;

            texture?.Destroy();
            texture = factory(extent);
            return true;
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

            return material;
        }

        private void ConfigureIrradianceFramebufferTarget()
        {
            if (_irradianceFBO is null || IrradianceTexture is null)
                return;

            _irradianceFBO.SetRenderTargets((IrradianceTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            _irradianceFBO.SettingUniforms -= BindIrradianceSourceSampler;
            _irradianceFBO.SettingUniforms += BindIrradianceSourceSampler;
        }

        private void ConfigurePrefilterFramebufferTarget()
        {
            if (_prefilterFBO is null || PrefilterTexture is null)
                return;

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

            IrradianceTexture?.Destroy();
            IrradianceTexture = null;
            PrefilterTexture?.Destroy();
            PrefilterTexture = null;
            _previewSphereDirty = true;
        }

        private void GenerateIrradianceInternal()
        {
            if (_irradianceFBO is null || IrradianceTexture is null)
                return;

            if (_useCubemapConvolution)
            {
                if (_irradianceCubeFBO is null || _irradianceTextureCubemap is null)
                    return;

                RenderIrradianceCubemapInternal();
                SynchronizeCaptureTextureWrites();
            }

            int width = (int)Math.Max(1u, IrradianceTexture.Width);
            int height = (int)Math.Max(1u, IrradianceTexture.Height);
            _irradianceFBO.SetRenderTargets((IrradianceTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            RunFullscreenProbePass(_irradianceFBO, width, height);

            IrradianceTexture.GenerateMipmapsGPU();
        }

        private void GeneratePrefilterInternal()
        {
            if (_prefilterFBO is null || PrefilterTexture is null)
                return;

            if (_useCubemapConvolution)
            {
                if (_prefilterCubeFBO is null || _prefilterTextureCubemap is null)
                    return;

                RenderPrefilterCubemapInternal();
                SynchronizeCaptureTextureWrites();
            }

            int baseExtent = (int)Math.Max(PrefilterTexture.Width, PrefilterTexture.Height);
            int maxMipLevels = PrefilterTexture.SmallestMipmapLevel + 1;
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipWidth = Math.Max(1, baseExtent >> mip);
                int mipHeight = Math.Max(1, baseExtent >> mip);

                if (!_useCubemapConvolution)
                {
                    float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);
                    _prefilterFBO.Material?.SetFloat(0, roughness);
                    _prefilterFBO.Material?.SetInt(1, _prefilterSourceDimension);
                }

                _prefilterFBO.SetRenderTargets((PrefilterTexture, EFrameBufferAttachment.ColorAttachment0, mip, -1));
                RunFullscreenProbePass(_prefilterFBO, mipWidth, mipHeight);
            }
        }

        private void RenderIrradianceCubemapInternal()
        {
            if (_irradianceCubeFBO is null || _irradianceTextureCubemap is null)
                return;

            int extent = Math.Max(1, (int)_irradianceTextureCubemap.Extent);
            using StateObject? renderArea = Engine.Rendering.State.RenderingPipelineState?.PushRenderArea(extent, extent);
            if (renderArea is null)
                AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(extent, extent)));

            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                _irradianceCubeFBO.SetRenderTargets((_irradianceTextureCubemap, EFrameBufferAttachment.ColorAttachment0, 0, faceIndex));
                using var bindScope = _irradianceCubeFBO.BindForWritingState();
                Engine.Rendering.State.ClearByBoundFBO(true, false, false);
                _irradianceCubeFBO.RenderFullscreen((ECubemapFace)faceIndex);
            }
        }

        private void RenderPrefilterCubemapInternal()
        {
            if (_prefilterCubeFBO is null || _prefilterTextureCubemap is null || EnvironmentTextureCubemap is null)
                return;

            int maxMipLevels = _prefilterTextureCubemap.Mipmaps.Length;
            int baseExtent = Math.Max(1, (int)_prefilterTextureCubemap.Extent);
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipExtent = Math.Max(1, baseExtent >> mip);
                float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);

                _prefilterCubeFBO.Material?.SetFloat(0, roughness);
                _prefilterCubeFBO.Material?.SetInt(1, _prefilterSourceDimension);

                using StateObject? renderArea = Engine.Rendering.State.RenderingPipelineState?.PushRenderArea(mipExtent, mipExtent);
                if (renderArea is null)
                    AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipExtent, mipExtent)));

                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                {
                    _prefilterCubeFBO.SetRenderTargets((_prefilterTextureCubemap, EFrameBufferAttachment.ColorAttachment0, mip, faceIndex));
                    using var bindScope = _prefilterCubeFBO.BindForWritingState();
                    Engine.Rendering.State.ClearByBoundFBO(true, false, false);
                    _prefilterCubeFBO.RenderFullscreen((ECubemapFace)faceIndex);
                }
            }
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

        private static void RunFullscreenProbePass(XRQuadFrameBuffer fbo, int width, int height)
        {
            var pipelineState = Engine.Rendering.State.RenderingPipelineState;
            BoundingRectangle previousCrop = pipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
            bool hadCrop = previousCrop.Width > 0 && previousCrop.Height > 0;

            using (fbo.BindForWritingState())
            {
                AbstractRenderer.Current?.SetCroppingEnabled(false);

                using StateObject? renderArea = pipelineState?.PushRenderArea(width, height);
                if (renderArea is null)
                    AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));

                Engine.Rendering.State.ClearByBoundFBO();
                fbo.Render(null, true);
            }

            if (hadCrop)
            {
                AbstractRenderer.Current?.SetCroppingEnabled(true);
                AbstractRenderer.Current?.CropRenderArea(previousCrop);
            }
        }

        private void ReleaseTransientEnvironmentTexturesAfterIblGeneration()
        {
            if (!ReleaseTransientEnvironmentTexturesAfterCapture)
                return;

            ReleaseCapturedEnvironmentTextures(releaseCubemap: true, releaseOctahedral: true);
            CachePreviewSphere();
        }

        public override void Render()
        {
            _registeredWorld?.Lights.EnsureShadowMapsCurrentForCapture(false);
            Engine.Rendering.State.IsLightProbePass = true;

            try
            {
                base.Render();

                // Only run IBL generation + version bump when a complete cubemap cycle finishes.
                // Progressive mode renders one face per Render() call; the base re-enqueues
                // for remaining faces automatically.
                if (!LastRenderCompletedCycle)
                    return;

                SynchronizeCaptureTextureWrites();
                GenerateIrradianceInternal();
                GeneratePrefilterInternal();
                ReleaseTransientEnvironmentTexturesAfterIblGeneration();
                CaptureVersion++;
            }
            finally
            {
                Engine.Rendering.State.IsLightProbePass = false;
            }
        }

        public override void ExecuteCaptureFace(int faceIndex)
        {
            Engine.Rendering.State.IsLightProbePass = true;
            try
            {
                base.ExecuteCaptureFace(faceIndex);
            }
            finally
            {
                Engine.Rendering.State.IsLightProbePass = false;
            }
        }

        public override void FinalizeCubemapCapture()
        {
            EnsureCaptureResourcesInitialized();
            _registeredWorld?.Lights.EnsureShadowMapsCurrentForCapture(false);
            Engine.Rendering.State.IsLightProbePass = true;

            try
            {
                base.FinalizeCubemapCapture();
                SynchronizeCaptureTextureWrites();
                GenerateIrradianceInternal();
                GeneratePrefilterInternal();
                ReleaseTransientEnvironmentTexturesAfterIblGeneration();
                CaptureVersion++;
            }
            finally
            {
                Engine.Rendering.State.IsLightProbePass = false;
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
            => ConfigureFullMipChain(new XRTexture2D(extent, extent, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb8,
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
                WriteRed = true,
                WriteGreen = true,
                WriteBlue = true,
                WriteAlpha = false,
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
