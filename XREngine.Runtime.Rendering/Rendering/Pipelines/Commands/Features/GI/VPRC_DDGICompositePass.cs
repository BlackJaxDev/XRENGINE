using System;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// DDGI composite pass. Reconstructs world position and normal from G-buffer depth and normal textures,
    /// samples diffuse irradiance from the active DDGI volume using sampleDDGI with Chebyshev visibility testing,
    /// modulates with material albedo, writes resolved diffuse GI to DDGITexture, and blends additively
    /// into ForwardPassFBOName using an XRQuadFrameBuffer.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGICompositePass : VPRC_DDGIComputePass
    {
        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public string AlbedoTextureName { get; set; } = DefaultRenderPipeline.AlbedoOpacityTextureName;
        public string RMSETextureName { get; set; } = DefaultRenderPipeline.RMSETextureName;
        public string OutputTextureName { get; set; } = DefaultRenderPipeline.DDGITextureName;
        public string CompositeQuadFBOName { get; set; } = DefaultRenderPipeline.DDGICompositeFBOName;
        public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;
        public string IrradianceAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIIrradianceAtlasTextureName;
        public string VisibilityAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIVisibilityAtlasTextureName;
        public string ProbeStateBufferName { get; set; } = DefaultRenderPipeline.DDGIProbeStateBufferName;
        public string RayBufferName { get; set; } = DefaultRenderPipeline.DDGIRayBufferName;
        public string HitBufferName { get; set; } = DefaultRenderPipeline.DDGIHitBufferName;
        public string AmbientOcclusionTextureName { get; set; } = DefaultRenderPipeline.AmbientOcclusionIntensityTextureName;

        public EDDGIDebugMode DebugMode { get; set; } = EDDGIDebugMode.None;

        private XRRenderProgram? _screenSampleProgram;
        private XRRenderProgram? _screenSampleProgramStereo;
        private const string CompositeDrawPassName = nameof(VPRC_DDGICompositePass) + "_Draw";
        private int _compositeDrawPassIndex = int.MinValue;

        protected override bool ShouldExecuteThisFrame()
            => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is
                IGlobalIlluminationPipelineProvider { UsesDDGI: true };

        protected override void ExecuteDDGI()
        {
            bool usesDDGI =
                ActivePipelineInstance.Pipeline is
                    IGlobalIlluminationPipelineProvider { UsesDDGI: true };
            if (!usesDDGI)
                return;

            IRuntimeRenderWorld? world = ActivePipelineInstance.RenderState.WindowViewport?.World
                ?? RuntimeEngine.Rendering.State.RenderingWorld;
            DDGIVolumeComponent? activeVolume = null;
            if (world is not null)
                DDGIVolumeComponent.Registry.TryGetFirstActive(world, out activeVolume);

            if (activeVolume is null || !activeVolume.VolumeEnabled || activeVolume.TotalProbeCount <= 0)
                return;

            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            if (activeVolume.UpdateMode == EDDGIUpdateMode.Baked && !context.PrepareBakedVolume(activeVolume))
                return;
            if (!context.BindResources(ActivePipelineInstance))
                return;

            bool stereo = ActivePipelineInstance.Pipeline is ISceneRenderPipelineFeatureProvider { Stereo: true };

            var depthTexture = ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName);
            var normalTexture = ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName);
            var albedoTexture = ActivePipelineInstance.GetTexture<XRTexture>(AlbedoTextureName);
            var rmseTexture = ActivePipelineInstance.GetTexture<XRTexture>(RMSETextureName);
            var outputTexture = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);
            var irradianceAtlas = ActivePipelineInstance.GetTexture<XRTexture>(IrradianceAtlasTextureName);
            var visibilityAtlas = ActivePipelineInstance.GetTexture<XRTexture>(VisibilityAtlasTextureName);
            var probeStateBuffer = ActivePipelineInstance.GetBuffer(ProbeStateBufferName);

            if (depthTexture is null || normalTexture is null || albedoTexture is null || rmseTexture is null || outputTexture is null ||
                irradianceAtlas is null || visibilityAtlas is null || probeStateBuffer is null)
                return;

            if (activeVolume.UpdateMode == EDDGIUpdateMode.Baked)
            {
                if (activeVolume.BakedAsset is null || !context.UploadBaked(activeVolume.BakedAsset))
                    return;
            }

            if (!context.HasInitializedResources)
                return;

            var compositeFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(CompositeQuadFBOName);
            string forwardTarget = ResolveForwardTarget();
            var forwardFbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(forwardTarget);
            if (compositeFbo is null || forwardFbo is null)
                return;

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            if (region.Width <= 0 || region.Height <= 0)
                return;

            int width = region.Width;
            int height = region.Height;

            var effectiveDebugMode = DebugMode != EDDGIDebugMode.None ? DebugMode : activeVolume.DebugMode;

            if (!EnsurePrograms(stereo))
                return;

            if (!TryResolveAuxiliaryPasses())
                return;

            // Do not author another sampled use unless the context has room to
            // retain its post-draw GPU lifetime receipt.
            if (!context.CanCompositeRead())
                return;

            if (stereo)
            {
                if (!DispatchScreenSampleStereo(context.State, depthTexture, normalTexture, albedoTexture, rmseTexture,
                    outputTexture, irradianceAtlas, visibilityAtlas, probeStateBuffer, width, height, (int)effectiveDebugMode))
                    return;
            }
            else
            {
                if (!DispatchScreenSampleMono(context.State, depthTexture, normalTexture, albedoTexture, rmseTexture,
                    outputTexture, irradianceAtlas, visibilityAtlas, probeStateBuffer, width, height, (int)effectiveDebugMode))
                    return;
            }

            // Debug views replace direct lighting; normal DDGI adds diffuse light.
            if (compositeFbo.Material?.RenderOptions.BlendModeAllDrawBuffers is { } blend)
                blend.RgbDstFactor = effectiveDebugMode == EDDGIDebugMode.None ? EBlendingFactor.One : EBlendingFactor.Zero;
            // The compute output must transition to sampled access before the draw.
            // A single graph pass cannot describe both accesses on Vulkan.
            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_compositeDrawPassIndex))
                compositeFbo.Render(forwardFbo, forceNoStereo: !stereo);
            if (!context.RecordCompositeUse())
                return;
            if (effectiveDebugMode != EDDGIDebugMode.None)
                context.MarkDiagnosticPresentation();
        }

        private bool TryResolveAuxiliaryPasses()
        {
            if (_compositeDrawPassIndex != int.MinValue)
                return true;
            if (ParentPipeline?.PassMetadata is { } metadata)
                foreach (RenderPassMetadata pass in metadata)
                {
                    if (pass.Name == CompositeDrawPassName)
                        _compositeDrawPassIndex = pass.PassIndex;
                }
            if (_compositeDrawPassIndex != int.MinValue)
                return true;

            Debug.RenderingWarningEvery("DDGI.Composite.MissingPass", TimeSpan.FromSeconds(2),
                "DDGI composition is waiting for its graphics render-graph pass.");
            return false;
        }

        private string ResolveForwardTarget()
            => ParentPipeline is DefaultRenderPipeline &&
                ForwardFBOName == DefaultRenderPipeline.ForwardPassFBOName && DefaultRenderPipeline.RuntimeEnableMsaaTargets
                ? DefaultRenderPipeline.ForwardPassMsaaFBOName : ForwardFBOName;

        private bool EnsurePrograms(bool stereo)
        {
            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            if (stereo)
            {
                _screenSampleProgramStereo = context.Program("ddgi_screen_sample_stereo");
                return DDGIFrameContext.IsReady(_screenSampleProgramStereo);
            }
            _screenSampleProgram = context.Program("ddgi_screen_sample");
            return DDGIFrameContext.IsReady(_screenSampleProgram);
        }

        private bool DispatchScreenSampleMono(
            DDGIVolumeRuntimeState state,
            XRTexture depthTex,
            XRTexture normalTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture outputTex,
            XRTexture irradianceAtlas,
            XRTexture visibilityAtlas,
            XRDataBuffer probeStateBuffer,
            int width,
            int height,
            int debugMode)
        {
            if (_screenSampleProgram is null)
                return false;

            var camera = ActivePipelineInstance.RenderState.SceneCamera
                ?? ActivePipelineInstance.RenderState.RenderingCamera;
            if (camera is null)
                return false;

            Matrix4x4 proj = camera.ProjectionMatrix;
            Matrix4x4.Invert(proj, out Matrix4x4 invProj);
            Matrix4x4 cameraToWorld = camera.Transform.RenderMatrix;
            Vector3 cameraPos = camera.Transform.RenderTranslation;


            _screenSampleProgram.Sampler("gDepth", depthTex, 0);
            _screenSampleProgram.Sampler("gNormal", normalTex, 1);
            _screenSampleProgram.Sampler("gAlbedo", albedoTex, 2);
            _screenSampleProgram.Sampler("uDDGIIrradianceAtlas", irradianceAtlas, 3);
            _screenSampleProgram.Sampler("uDDGIVisibilityAtlas", visibilityAtlas, 4);
            _screenSampleProgram.Sampler("gRMSE", rmseTex, 6);

            _screenSampleProgram.BindBuffer(probeStateBuffer, 4);
            _screenSampleProgram.BindImageTexture(0u, outputTex, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);

            _screenSampleProgram.Uniform("invProjMatrix", invProj);
            _screenSampleProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _screenSampleProgram.Uniform("cameraPos", cameraPos);
            _screenSampleProgram.Uniform("resolution", new IVector2(width, height));
            _screenSampleProgram.Uniform("uDebugMode", debugMode);

            _screenSampleProgram.Uniform("uGridMin", state.GridMin);
            _screenSampleProgram.Uniform("uProbeSpacing", state.ProbeSpacing);
            _screenSampleProgram.Uniform("uProbeCounts", state.ProbeCounts);
            _screenSampleProgram.Uniform("uNormalBias", state.NormalBias);
            _screenSampleProgram.Uniform("uViewBias", state.ViewBias);
            _screenSampleProgram.Uniform("uChebyshevPower", state.ChebyshevPower);
            _screenSampleProgram.Uniform("uIntensity", state.Intensity);
            _screenSampleProgram.Uniform("uTint", state.Tint);
            _screenSampleProgram.Uniform("DepthMode", (int)camera.DepthMode);
            _screenSampleProgram.Uniform("ClipDepthRange", (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
            _screenSampleProgram.Uniform("ClipSpaceYDirection", (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
            _screenSampleProgram.Uniform("FramebufferTextureYDirection", (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend));
            _screenSampleProgram.Uniform("uIrradianceAtlasSize", new Vector2(state.IrradianceAtlasWidth, state.IrradianceAtlasHeight));
            _screenSampleProgram.Uniform("uVisibilityAtlasSize", new Vector2(state.VisibilityAtlasWidth, state.VisibilityAtlasHeight));

            var aoTexture = ActivePipelineInstance.GetTexture<XRTexture>(AmbientOcclusionTextureName);
            bool useAO = state.ApplyAmbientOcclusion && aoTexture is not null;
            _screenSampleProgram.Uniform("uUseAO", useAO);
            if (useAO && aoTexture is not null)
            {
                _screenSampleProgram.Sampler("uAOTexture", aoTexture, 5);
            }

            DDGIVolumeRuntimeState.UploadCascadeUniforms(_screenSampleProgram, state);

            uint groupX = ((uint)width + 15u) / 16u;
            uint groupY = ((uint)height + 15u) / 16u;
            _screenSampleProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            return true;
        }

        private bool DispatchScreenSampleStereo(
            DDGIVolumeRuntimeState state,
            XRTexture depthTex,
            XRTexture normalTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture outputTex,
            XRTexture irradianceAtlas,
            XRTexture visibilityAtlas,
            XRDataBuffer probeStateBuffer,
            int width,
            int height,
            int debugMode)
        {
            if (_screenSampleProgramStereo is null)
                return false;

            var renderState = ActivePipelineInstance.RenderState;
            var leftCamera = renderState.SceneCamera ?? renderState.RenderingCamera;
            var rightCamera = renderState.StereoRightEyeCamera;
            if (leftCamera is null)
                return false;

            Matrix4x4 leftProj = leftCamera.ProjectionMatrix;
            Matrix4x4.Invert(leftProj, out Matrix4x4 leftInvProj);
            Matrix4x4 leftCameraToWorld = leftCamera.Transform.RenderMatrix;
            Vector3 leftCameraPos = leftCamera.Transform.RenderTranslation;

            Matrix4x4 rightProj = rightCamera?.ProjectionMatrix ?? leftProj;
            Matrix4x4.Invert(rightProj, out Matrix4x4 rightInvProj);
            Matrix4x4 rightCameraToWorld = rightCamera?.Transform.RenderMatrix ?? leftCameraToWorld;
            Vector3 rightCameraPos = rightCamera?.Transform.RenderTranslation ?? leftCameraPos;


            _screenSampleProgramStereo.Sampler("gDepth", depthTex, 0);
            _screenSampleProgramStereo.Sampler("gNormal", normalTex, 1);
            _screenSampleProgramStereo.Sampler("gAlbedo", albedoTex, 2);
            _screenSampleProgramStereo.Sampler("uDDGIIrradianceAtlas", irradianceAtlas, 3);
            _screenSampleProgramStereo.Sampler("uDDGIVisibilityAtlas", visibilityAtlas, 4);
            _screenSampleProgramStereo.Sampler("gRMSE", rmseTex, 6);

            _screenSampleProgramStereo.BindBuffer(probeStateBuffer, 4);
            _screenSampleProgramStereo.BindImageTexture(0u, outputTex, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);

            _screenSampleProgramStereo.Uniform("leftInvProjMatrix", leftInvProj);
            _screenSampleProgramStereo.Uniform("leftCameraToWorldMatrix", leftCameraToWorld);
            _screenSampleProgramStereo.Uniform("leftCameraPos", leftCameraPos);
            _screenSampleProgramStereo.Uniform("rightInvProjMatrix", rightInvProj);
            _screenSampleProgramStereo.Uniform("rightCameraToWorldMatrix", rightCameraToWorld);
            _screenSampleProgramStereo.Uniform("rightCameraPos", rightCameraPos);

            _screenSampleProgramStereo.Uniform("resolution", new IVector2(width, height));
            _screenSampleProgramStereo.Uniform("uDebugMode", debugMode);

            _screenSampleProgramStereo.Uniform("uGridMin", state.GridMin);
            _screenSampleProgramStereo.Uniform("uProbeSpacing", state.ProbeSpacing);
            _screenSampleProgramStereo.Uniform("uProbeCounts", state.ProbeCounts);
            _screenSampleProgramStereo.Uniform("uNormalBias", state.NormalBias);
            _screenSampleProgramStereo.Uniform("uViewBias", state.ViewBias);
            _screenSampleProgramStereo.Uniform("uChebyshevPower", state.ChebyshevPower);
            _screenSampleProgramStereo.Uniform("uIntensity", state.Intensity);
            _screenSampleProgramStereo.Uniform("uTint", state.Tint);
            _screenSampleProgramStereo.Uniform("DepthMode", (int)leftCamera.DepthMode);
            _screenSampleProgramStereo.Uniform("ClipDepthRange", (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
            _screenSampleProgramStereo.Uniform("ClipSpaceYDirection", (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
            _screenSampleProgramStereo.Uniform("FramebufferTextureYDirection", (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend));
            _screenSampleProgramStereo.Uniform("uIrradianceAtlasSize", new Vector2(state.IrradianceAtlasWidth, state.IrradianceAtlasHeight));
            _screenSampleProgramStereo.Uniform("uVisibilityAtlasSize", new Vector2(state.VisibilityAtlasWidth, state.VisibilityAtlasHeight));

            var aoTexture = ActivePipelineInstance.GetTexture<XRTexture>(AmbientOcclusionTextureName);
            bool useAO = state.ApplyAmbientOcclusion && aoTexture is not null;
            _screenSampleProgramStereo.Uniform("uUseAO", useAO);
            if (useAO && aoTexture is not null)
            {
                _screenSampleProgramStereo.Sampler("uAOTexture", aoTexture, 5);
            }

            DDGIVolumeRuntimeState.UploadCascadeUniforms(_screenSampleProgramStereo, state);

            uint groupX = ((uint)width + 15u) / 16u;
            uint groupY = ((uint)height + 15u) / 16u;
            _screenSampleProgramStereo.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            return true;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_DDGICompositePass), ERenderGraphPassStage.Compute);
            builder.SampleTexture(MakeTextureResource(DepthTextureName));
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.SampleTexture(MakeTextureResource(AlbedoTextureName));
            builder.SampleTexture(MakeTextureResource(RMSETextureName));
            builder.SampleTexture(MakeTextureResource(IrradianceAtlasTextureName));
            builder.SampleTexture(MakeTextureResource(VisibilityAtlasTextureName));
            builder.SampleTexture(MakeTextureResource(AmbientOcclusionTextureName));
            builder.ReadBuffer(ProbeStateBufferName);

            builder.WriteTexture(MakeTextureResource(OutputTextureName));

            var draw = context.GetOrCreateSyntheticPass(CompositeDrawPassName, ERenderGraphPassStage.Graphics);
            draw.DependsOn(builder.PassIndex);
            draw.UseEngineDescriptors();
            draw.UseMaterialDescriptors();
            draw.SampleTexture(MakeTextureResource(OutputTextureName));
            draw.UseColorAttachment(MakeFboColorResource(ResolveForwardTarget()), ERenderGraphAccess.ReadWrite,
                ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
        }
    }
}
