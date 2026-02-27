using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Samples cascaded 3D radiance volumes into a screen-space GI texture and composites the result into the forward target.
    /// Implementation is fully native to XRENGINE.
    /// Features:
    /// - Priority-based cascade selection (prefers higher resolution cascades)
    /// - Normal-based sampling offset to reduce light leaking
    /// - Temporal accumulation for stability
    /// - Half-resolution rendering option for performance
    /// - Debug visualization modes
    /// </summary>
    public class VPRC_RadianceCascadesPass : ViewportRenderCommand
    {
        private const uint GroupSize = 16u;
        private const string MonoShaderPath = "Compute/GI/RadianceCascades/RadianceCascades.comp";
        private const string StereoShaderPath = "Compute/GI/RadianceCascades/RadianceCascadesStereo.comp";

        private XRRenderProgram? _computeProgram;
        private XRRenderProgram? _computeProgramStereo;
        private XRTexture3D? _fallbackCascade;
        private XRTexture2D? _historyTextureA;
        private XRTexture2D? _historyTextureB;
        private XRTexture2DArray? _historyTextureStereoA;
        private XRTexture2DArray? _historyTextureStereoB;
        private bool _useHistoryA = true;
        private uint _frameIndex;
        private int _lastHistoryWidth;
        private int _lastHistoryHeight;

        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public string OutputTextureName { get; set; } = DefaultRenderPipeline.RadianceCascadeGITextureName;
        public string CompositeQuadFBOName { get; set; } = DefaultRenderPipeline.RadianceCascadeCompositeFBOName;
        public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;

        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not DefaultRenderPipeline pipeline || !pipeline.UsesRadianceCascades)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            var world = ActivePipelineInstance.RenderState.WindowViewport?.World;
            if (camera is null || world is null)
                return;

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            if (region.Width <= 0 || region.Height <= 0)
                return;

            bool stereo = pipeline.Stereo;

            if (!EnsureProgram(stereo))
                return;

            var depthTexture = ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName);
            var normalTexture = ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName);
            var outputTexture = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);

            if (depthTexture is null || normalTexture is null || outputTexture is null)
                return;

            XRQuadFrameBuffer? compositeFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(CompositeQuadFBOName);
            XRFrameBuffer? forwardFbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
            if (compositeFbo is null || forwardFbo is null)
                return;

            if (!RadianceCascadeComponent.Registry.TryGetFirstActive(world, out RadianceCascadeComponent? cascadeComponent) || cascadeComponent is null)
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            var cascades = cascadeComponent.GetActiveCascades();
            if (cascades.Count == 0)
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            if (!cascadeComponent.TryGetWorldToLocal(out Matrix4x4 worldToLocal))
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            // Determine render resolution (half-res or full-res)
            int renderWidth = cascadeComponent.HalfResolution ? region.Width / 2 : region.Width;
            int renderHeight = cascadeComponent.HalfResolution ? region.Height / 2 : region.Height;

            if (stereo)
            {
                if (depthTexture is not XRTexture2DArray depthArray ||
                    normalTexture is not XRTexture2DArray normalArray ||
                    outputTexture is not XRTexture2DArray outputArray)
                {
                    outputTexture.Clear(ColorF4.Transparent);
                    return;
                }

                EnsureHistoryTextureStereo(renderWidth, renderHeight);
                DispatchComputeStereo(camera, region, depthArray, normalArray, outputArray, cascades, cascadeComponent, worldToLocal, renderWidth, renderHeight);
            }
            else
            {
                if (depthTexture is not XRTexture2D depthTex ||
                    normalTexture is not XRTexture2D normalTex ||
                    outputTexture is not XRTexture2D outputTex)
                {
                    outputTexture.Clear(ColorF4.Transparent);
                    return;
                }

                EnsureHistoryTexture(renderWidth, renderHeight);
                DispatchCompute(camera, region, depthTex, normalTex, outputTex, cascades, cascadeComponent, worldToLocal, renderWidth, renderHeight);
            }

            compositeFbo.Render(forwardFbo);
            SwapHistoryBuffers();
            _frameIndex++;
        }

        private void EnsureHistoryTexture(int width, int height)
        {
            if (_historyTextureA is not null && _lastHistoryWidth == width && _lastHistoryHeight == height)
                return;

            _historyTextureA?.Destroy();
            _historyTextureB?.Destroy();
            
            _historyTextureA = new XRTexture2D((uint)width, (uint)height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Name = "RadianceCascadeHistoryA",
                SamplerName = "gHistory",
            };
            _historyTextureB = new XRTexture2D((uint)width, (uint)height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Name = "RadianceCascadeHistoryB",
                SamplerName = "gHistory",
            };
            _lastHistoryWidth = width;
            _lastHistoryHeight = height;
        }

        private void EnsureHistoryTextureStereo(int width, int height)
        {
            if (_historyTextureStereoA is not null && _lastHistoryWidth == width && _lastHistoryHeight == height)
                return;

            _historyTextureStereoA?.Destroy();
            _historyTextureStereoB?.Destroy();
            
            _historyTextureStereoA = new XRTexture2DArray((uint)width, (uint)height, 2, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Name = "RadianceCascadeHistoryStereoA",
                SamplerName = "gHistory",
            };
            _historyTextureStereoB = new XRTexture2DArray((uint)width, (uint)height, 2, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Name = "RadianceCascadeHistoryStereoB",
                SamplerName = "gHistory",
            };
            _lastHistoryWidth = width;
            _lastHistoryHeight = height;
        }

        private XRTexture2D? GetCurrentHistoryTexture() => _useHistoryA ? _historyTextureA : _historyTextureB;
        private XRTexture2D? GetPreviousHistoryTexture() => _useHistoryA ? _historyTextureB : _historyTextureA;
        private XRTexture2DArray? GetCurrentHistoryTextureStereo() => _useHistoryA ? _historyTextureStereoA : _historyTextureStereoB;
        private XRTexture2DArray? GetPreviousHistoryTextureStereo() => _useHistoryA ? _historyTextureStereoB : _historyTextureStereoA;

        private void SwapHistoryBuffers()
        {
            _useHistoryA = !_useHistoryA;
        }

        private bool EnsureProgram(bool stereo)
        {
            if (stereo)
            {
                if (_computeProgramStereo is not null)
                    return true;

                var shader = XRShader.EngineShader(StereoShaderPath, EShaderType.Compute);
                _computeProgramStereo = new XRRenderProgram(true, false, shader);
                return _computeProgramStereo is not null;
            }

            if (_computeProgram is not null)
                return true;

            var monoShader = XRShader.EngineShader(MonoShaderPath, EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, monoShader);
            return _computeProgram is not null;
        }

        private XRTexture3D GetFallbackCascade()
        {
            if (_fallbackCascade is not null)
                return _fallbackCascade;

            XRTexture3D tex = new(1, 1, 1, ColorF4.Black)
            {
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
                SamplerName = "RadianceCascadeFallback",
                Name = "RadianceCascadeFallback",
            };

            _fallbackCascade = tex;
            return tex;
        }

        private static void PopulateCascadeUniforms(XRRenderProgram program, IReadOnlyList<RadianceCascadeLevel> cascades)
        {
            for (int i = 0; i < RadianceCascadeComponent.MaxCascades; i++)
            {
                var halfExtents = Vector3.Zero;
                float intensity = 0.0f;

                if (i < cascades.Count)
                {
                    var cascade = cascades[i];
                    halfExtents = cascade.HalfExtents;
                    intensity = cascade.Intensity;
                }

                program.Uniform($"cascadeHalfExtents{i}", new Vector4(halfExtents, intensity));
            }

            program.Uniform("cascadeCount", cascades.Count);
        }

        private void BindCascadeTextures(XRRenderProgram program, IReadOnlyList<RadianceCascadeLevel> cascades)
        {
            XRTexture3D fallback = GetFallbackCascade();

            for (int i = 0; i < RadianceCascadeComponent.MaxCascades; i++)
            {
                XRTexture3D tex = i < cascades.Count && cascades[i].RadianceTexture is XRTexture3D valid
                    ? valid
                    : fallback;
                program.Sampler($"RadianceCascade{i}", tex, (int)(3 + i));
            }
        }

        private void DispatchCompute(XRCamera camera, BoundingRectangle region, XRTexture2D depthTex, XRTexture2D normalTex, XRTexture2D outputTex,
            IReadOnlyList<RadianceCascadeLevel> cascades, RadianceCascadeComponent component, Matrix4x4 worldToLocal, int renderWidth, int renderHeight)
        {
            if (_computeProgram is null)
                return;

            uint width = (uint)renderWidth;
            uint height = (uint)renderHeight;

            Matrix4x4 proj = camera.ProjectionMatrix;
            Matrix4x4.Invert(proj, out Matrix4x4 invProj);
            Matrix4x4 cameraToWorld = camera.Transform.RenderMatrix;

            _computeProgram.Sampler("gDepth", depthTex, 0);
            _computeProgram.Sampler("gNormal", normalTex, 1);
            _computeProgram.BindImageTexture(2u, outputTex, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.RGBA16F);

            // Bind previous frame's history texture for temporal accumulation (ping-pong)
            var historyTex = GetPreviousHistoryTexture();
            if (historyTex is not null && component.TemporalBlendFactor > 0.0f)
                _computeProgram.Sampler("gHistory", historyTex, 7);

            BindCascadeTextures(_computeProgram, cascades);
            PopulateCascadeUniforms(_computeProgram, cascades);

            _computeProgram.Uniform("invProjMatrix", invProj);
            _computeProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _computeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _computeProgram.Uniform("frameIndex", _frameIndex);
            _computeProgram.Uniform("volumeWorldToLocal", worldToLocal);
            _computeProgram.Uniform("volumeTintIntensity", new Vector4(component.Tint.R, component.Tint.G, component.Tint.B, component.Intensity));

            // New uniforms for improvements
            _computeProgram.Uniform("temporalBlendFactor", _frameIndex > 0 ? component.TemporalBlendFactor : 0.0f);
            _computeProgram.Uniform("normalOffsetScale", component.NormalOffsetScale);
            _computeProgram.Uniform("debugMode", (int)component.DebugMode);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _computeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        }

        private void DispatchComputeStereo(XRCamera leftCamera, BoundingRectangle region, XRTexture2DArray depthTex, XRTexture2DArray normalTex, XRTexture2DArray outputTex,
            IReadOnlyList<RadianceCascadeLevel> cascades, RadianceCascadeComponent component, Matrix4x4 worldToLocal, int renderWidth, int renderHeight)
        {
            if (_computeProgramStereo is null)
                return;

            var renderState = ActivePipelineInstance.RenderState;
            var rightCamera = renderState.StereoRightEyeCamera;

            uint width = (uint)renderWidth;
            uint height = (uint)renderHeight;

            Matrix4x4 leftProj = leftCamera.ProjectionMatrix;
            Matrix4x4.Invert(leftProj, out Matrix4x4 leftInvProj);
            Matrix4x4 leftCameraToWorld = leftCamera.Transform.RenderMatrix;

            Matrix4x4 rightProj = rightCamera?.ProjectionMatrix ?? leftProj;
            Matrix4x4.Invert(rightProj, out Matrix4x4 rightInvProj);
            Matrix4x4 rightCameraToWorld = rightCamera?.Transform.RenderMatrix ?? leftCameraToWorld;

            _computeProgramStereo.Sampler("gDepth", depthTex, 0);
            _computeProgramStereo.Sampler("gNormal", normalTex, 1);
            _computeProgramStereo.BindImageTexture(2u, outputTex, 0, true, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.RGBA16F);

            // Bind previous frame's history texture for temporal accumulation (ping-pong)
            var historyTexStereo = GetPreviousHistoryTextureStereo();
            if (historyTexStereo is not null && component.TemporalBlendFactor > 0.0f)
                _computeProgramStereo.Sampler("gHistory", historyTexStereo, 7);

            BindCascadeTextures(_computeProgramStereo, cascades);
            PopulateCascadeUniforms(_computeProgramStereo, cascades);

            _computeProgramStereo.Uniform("leftInvProjMatrix", leftInvProj);
            _computeProgramStereo.Uniform("leftCameraToWorldMatrix", leftCameraToWorld);
            _computeProgramStereo.Uniform("rightInvProjMatrix", rightInvProj);
            _computeProgramStereo.Uniform("rightCameraToWorldMatrix", rightCameraToWorld);

            _computeProgramStereo.Uniform("resolution", new IVector2((int)width, (int)height));
            _computeProgramStereo.Uniform("frameIndex", _frameIndex);
            _computeProgramStereo.Uniform("volumeWorldToLocal", worldToLocal);
            _computeProgramStereo.Uniform("volumeTintIntensity", new Vector4(component.Tint.R, component.Tint.G, component.Tint.B, component.Intensity));

            // New uniforms for improvements
            _computeProgramStereo.Uniform("temporalBlendFactor", _frameIndex > 0 ? component.TemporalBlendFactor : 0.0f);
            _computeProgramStereo.Uniform("normalOffsetScale", component.NormalOffsetScale);
            _computeProgramStereo.Uniform("debugMode", (int)component.DebugMode);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _computeProgramStereo.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_RadianceCascadesPass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(DepthTextureName));
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.ReadWriteTexture(MakeTextureResource(OutputTextureName));
            builder.SampleTexture(MakeTextureResource(OutputTextureName));
            builder.UseColorAttachment(MakeFboColorResource(ForwardFBOName));
        }
    }
}
