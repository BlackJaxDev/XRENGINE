using System.IO;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Samples baked light volumes into a screen-space GI texture and composites the result into the forward target.
    /// Supports both mono and stereo (VR) rendering modes.
    /// </summary>
    public class VPRC_LightVolumesPass : ViewportRenderCommand
    {
        private const uint GroupSize = 16u;
        private const string MonoShaderPath = "Compute/GI/LightVolumes/LightVolumes.comp";
        private const string StereoShaderPath = "Compute/GI/LightVolumes/LightVolumesStereo.comp";

        private XRRenderProgram? _computeProgram;
        private XRRenderProgram? _computeProgramStereo;
        private uint _frameIndex;

        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public string OutputTextureName { get; set; } = DefaultRenderPipeline.LightVolumeGITextureName;
        public string CompositeQuadFBOName { get; set; } = DefaultRenderPipeline.LightVolumeCompositeFBOName;
        public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;

        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not DefaultRenderPipeline pipeline || !pipeline.UsesLightVolumes)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            var world = ActivePipelineInstance.RenderState.WindowViewport?.World;
            if (camera is null || world is null)
                return;

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            if (region.Width <= 0 || region.Height <= 0)
                return;

            // Determine if we're in stereo mode
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

            if (!LightVolumeComponent.Registry.TryGetFirstActive(world, out LightVolumeComponent? volume) || volume is null)
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            if (volume.VolumeTexture is not XRTexture3D volumeTexture)
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            if (!volume.TryGetWorldToLocal(out Matrix4x4 worldToLocal))
            {
                outputTexture.Clear(ColorF4.Transparent);
                return;
            }

            if (stereo)
            {
                // Stereo rendering path - textures should be XRTexture2DArray
                if (depthTexture is not XRTexture2DArray depthArray ||
                    normalTexture is not XRTexture2DArray normalArray ||
                    outputTexture is not XRTexture2DArray outputArray)
                {
                    outputTexture.Clear(ColorF4.Transparent);
                    return;
                }

                DispatchComputeStereo(camera, region, depthArray, normalArray, outputArray, volumeTexture, volume, worldToLocal);
            }
            else
            {
                // Mono rendering path - textures should be XRTexture2D
                if (depthTexture is not XRTexture2D depthTex ||
                    normalTexture is not XRTexture2D normalTex ||
                    outputTexture is not XRTexture2D outputTex)
                {
                    outputTexture.Clear(ColorF4.Transparent);
                    return;
                }

                DispatchCompute(camera, region, depthTex, normalTex, outputTex, volumeTexture, volume, worldToLocal);
            }

            compositeFbo.Render(forwardFbo);

            _frameIndex++;
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
            else
            {
                if (_computeProgram is not null)
                    return true;

                var shader = XRShader.EngineShader(MonoShaderPath, EShaderType.Compute);
                _computeProgram = new XRRenderProgram(true, false, shader);
                return _computeProgram is not null;
            }
        }

        private void DispatchCompute(XRCamera camera, BoundingRectangle region, XRTexture2D depthTex, XRTexture2D normalTex, XRTexture2D outputTex, XRTexture3D volumeTex, LightVolumeComponent volume, Matrix4x4 worldToLocal)
        {
            if (_computeProgram is null)
                return;

            uint width = (uint)region.Width;
            uint height = (uint)region.Height;

            Matrix4x4 proj = camera.ProjectionMatrix;
            Matrix4x4.Invert(proj, out Matrix4x4 invProj);
            Matrix4x4 cameraToWorld = camera.Transform.RenderMatrix;

            _computeProgram.Sampler("gDepth", depthTex, 0);
            _computeProgram.Sampler("gNormal", normalTex, 1);
            _computeProgram.Sampler("LightVolumeTex", volumeTex, 3);
            _computeProgram.BindImageTexture(2u, outputTex, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);

            _computeProgram.Uniform("invProjMatrix", invProj);
            _computeProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _computeProgram.Uniform("cameraPosition", camera.Transform.RenderTranslation);
            _computeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _computeProgram.Uniform("frameIndex", _frameIndex);
            _computeProgram.Uniform("volumeWorldToLocal", worldToLocal);
            _computeProgram.Uniform("volumeHalfExtents", volume.HalfExtents);
            _computeProgram.Uniform("volumeTint", volume.Tint);
            _computeProgram.Uniform("volumeIntensity", volume.Intensity);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _computeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        }

        private void DispatchComputeStereo(XRCamera camera, BoundingRectangle region, XRTexture2DArray depthTex, XRTexture2DArray normalTex, XRTexture2DArray outputTex, XRTexture3D volumeTex, LightVolumeComponent volume, Matrix4x4 worldToLocal)
        {
            if (_computeProgramStereo is null)
                return;

            var renderState = ActivePipelineInstance.RenderState;
            var leftCamera = camera;
            var rightCamera = renderState.StereoRightEyeCamera;

            uint width = (uint)region.Width;
            uint height = (uint)region.Height;

            // Left eye matrices
            Matrix4x4 leftProj = leftCamera.ProjectionMatrix;
            Matrix4x4.Invert(leftProj, out Matrix4x4 leftInvProj);
            Matrix4x4 leftCameraToWorld = leftCamera.Transform.RenderMatrix;

            // Right eye matrices (fallback to left if right not available)
            Matrix4x4 rightProj = rightCamera?.ProjectionMatrix ?? leftProj;
            Matrix4x4.Invert(rightProj, out Matrix4x4 rightInvProj);
            Matrix4x4 rightCameraToWorld = rightCamera?.Transform.RenderMatrix ?? leftCameraToWorld;

            _computeProgramStereo.Sampler("gDepth", depthTex, 0);
            _computeProgramStereo.Sampler("gNormal", normalTex, 1);
            _computeProgramStereo.Sampler("LightVolumeTex", volumeTex, 3);
            _computeProgramStereo.BindImageTexture(2u, outputTex, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);

            // Left eye uniforms
            _computeProgramStereo.Uniform("leftInvProjMatrix", leftInvProj);
            _computeProgramStereo.Uniform("leftCameraToWorldMatrix", leftCameraToWorld);
            _computeProgramStereo.Uniform("leftCameraPosition", leftCamera.Transform.RenderTranslation);

            // Right eye uniforms
            _computeProgramStereo.Uniform("rightInvProjMatrix", rightInvProj);
            _computeProgramStereo.Uniform("rightCameraToWorldMatrix", rightCameraToWorld);
            _computeProgramStereo.Uniform("rightCameraPosition", rightCamera?.Transform.RenderTranslation ?? leftCamera.Transform.RenderTranslation);

            // Common uniforms
            _computeProgramStereo.Uniform("resolution", new IVector2((int)width, (int)height));
            _computeProgramStereo.Uniform("frameIndex", _frameIndex);
            _computeProgramStereo.Uniform("volumeWorldToLocal", worldToLocal);
            _computeProgramStereo.Uniform("volumeHalfExtents", volume.HalfExtents);
            _computeProgramStereo.Uniform("volumeTint", volume.Tint);
            _computeProgramStereo.Uniform("volumeIntensity", volume.Intensity);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _computeProgramStereo.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_LightVolumesPass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(DepthTextureName));
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.ReadWriteTexture(MakeTextureResource(OutputTextureName));
            builder.SampleTexture(MakeTextureResource(OutputTextureName));
            builder.UseColorAttachment(MakeFboColorResource(ForwardFBOName));
        }
    }
}
