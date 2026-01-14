using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.GI;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Dispatches the ReSTIR compute passes and composites the result into the forward lighting target.
    /// </summary>
    public class VPRC_ReSTIRPass : ViewportRenderCommand
    {
        private const uint GroupSize = 16u;

        private XRRenderProgram? _initialProgram;
        private XRRenderProgram? _resampleProgram;
        private XRRenderProgram? _finalProgram;

        private XRDataBuffer? _initialReservoir;
        private XRDataBuffer? _temporalReservoir;
        private XRDataBuffer? _spatialReservoir;

        private uint _currentWidth;
        private uint _currentHeight;
        private uint _frameIndex;

        private readonly uint _reservoirStride = (uint)Marshal.SizeOf<RestirGI.Reservoir>();

        // Optional NV_ray_tracing path
        // If these are set (non-zero), this pass will attempt to bind and dispatch the RT pipeline via RestirGI.
        public uint RayTracingPipelineId { get; set; } = 0;
        public uint RayTracingSbtBufferId { get; set; } = 0;
        public uint RayTracingSbtOffset { get; set; } = 0;
        public uint RayTracingSbtStride { get; set; } = 0;

        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public string RestirOutputTextureName { get; set; } = DefaultRenderPipeline.RestirGITextureName;
        public string CompositeQuadFBOName { get; set; } = DefaultRenderPipeline.RestirCompositeFBOName;
        public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;

        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not DefaultRenderPipeline defaultPipeline || !defaultPipeline.UsesRestirGI)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            if (camera is null)
                return;

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            if (region.Width <= 0 || region.Height <= 0)
                return;

            uint width = (uint)region.Width;
            uint height = (uint)region.Height;

            if (!EnsurePrograms())
                return;

            if (!EnsureResources(width, height))
                return;

            if (ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName) is not XRTexture2D depthTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName) is not XRTexture2D normalTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(RestirOutputTextureName) is not XRTexture2D restirTex)
            {
                return;
            }

            XRQuadFrameBuffer? compositeFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(CompositeQuadFBOName);
            XRFrameBuffer? forwardFbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
            if (compositeFbo is null || forwardFbo is null)
                return;

            Matrix4x4 proj = camera.ProjectionMatrix;
            Matrix4x4.Invert(proj, out Matrix4x4 invProj);
            Matrix4x4 cameraToWorld = camera.Transform.RenderMatrix;
            Vector3 cameraPosition = camera.Transform.RenderTranslation;

            bool rtDispatched = TryRayTrace(width, height);

            if (!rtDispatched)
            {
                // Fallback to compute-based path
                DispatchInitial(width, height, invProj, cameraToWorld, cameraPosition, depthTex, normalTex);
                DispatchResample(width, height, invProj, cameraToWorld, depthTex, normalTex);
                DispatchFinal(width, height, restirTex);
            }

            compositeFbo.Render(forwardFbo);

            _frameIndex++;
        }

        private bool TryRayTrace(uint width, uint height)
        {
            // Vulkan-only optional RT path.
            if (!Engine.Rendering.State.IsVulkan)
                return false;

            // Only try when fully configured
            if (RayTracingPipelineId == 0 || RayTracingSbtBufferId == 0 || RayTracingSbtStride == 0)
                return false;

            if (!RestirGI.TryInit() || !RestirGI.TryBind(RayTracingPipelineId))
                return false;

            var parameters = RestirGI.TraceParameters.CreateSingleTable(
                RayTracingSbtBufferId,
                RayTracingSbtOffset,
                RayTracingSbtStride,
                width,
                height,
                1u);

            if (!RestirGI.TryDispatch(parameters))
                return false;

            // Ensure writes are visible for subsequent operations
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.TextureFetch);
            return true;
        }

        private bool EnsurePrograms()
        {
            if (_initialProgram is null)
            {
                _initialProgram = new XRRenderProgram(true, false,
                    ShaderHelper.LoadEngineShader(Path.Combine("Compute", "RESTIR", "InitialSampling.comp"), EShaderType.Compute));
            }

            if (_resampleProgram is null)
            {
                _resampleProgram = new XRRenderProgram(true, false,
                    ShaderHelper.LoadEngineShader(Path.Combine("Compute", "RESTIR", "GIResampling.comp"), EShaderType.Compute));
            }

            if (_finalProgram is null)
            {
                _finalProgram = new XRRenderProgram(true, false,
                    ShaderHelper.LoadEngineShader(Path.Combine("Compute", "RESTIR", "FinalShading.comp"), EShaderType.Compute));
            }

            return _initialProgram is not null && _resampleProgram is not null && _finalProgram is not null;
        }

        private bool EnsureResources(uint width, uint height)
        {
            if (_initialReservoir is not null && width == _currentWidth && height == _currentHeight)
                return true;

            uint elementCount = width * height;
            if (elementCount == 0)
                return false;

            _initialReservoir?.Destroy();
            _temporalReservoir?.Destroy();
            _spatialReservoir?.Destroy();

            _initialReservoir = CreateReservoirBuffer("RestirInitialReservoir", 3u, elementCount);
            _temporalReservoir = CreateReservoirBuffer("RestirTemporalReservoir", 4u, elementCount);
            _spatialReservoir = CreateReservoirBuffer("RestirSpatialReservoir", 5u, elementCount);

            _currentWidth = width;
            _currentHeight = height;
            return true;
        }

        private XRDataBuffer CreateReservoirBuffer(string name, uint bindingIndex, uint elementCount)
        {
            var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Struct, _reservoirStride, false, false)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = bindingIndex,
                DisposeOnPush = false,
                PadEndingToVec4 = false
            };
            buffer.PushData();
            return buffer;
        }

        private void DispatchInitial(uint width, uint height, Matrix4x4 invProj, Matrix4x4 cameraToWorld, Vector3 cameraPosition, XRTexture2D depthTex, XRTexture2D normalTex)
        {
            if (_initialProgram is null || _initialReservoir is null)
                return;

            _initialReservoir.BindTo(_initialProgram, 3u);
            _initialProgram.Sampler("gDepth", depthTex, 0);
            _initialProgram.Sampler("gNormal", normalTex, 1);
            _initialProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _initialProgram.Uniform("invRes", new Vector2(1.0f / width, 1.0f / height));
            _initialProgram.Uniform("frameIndex", _frameIndex);
            _initialProgram.Uniform("invProjMatrix", invProj);
            _initialProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _initialProgram.Uniform("cameraPosition", cameraPosition);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _initialProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchResample(uint width, uint height, Matrix4x4 invProj, Matrix4x4 cameraToWorld, XRTexture2D depthTex, XRTexture2D normalTex)
        {
            if (_resampleProgram is null || _initialReservoir is null || _temporalReservoir is null || _spatialReservoir is null)
                return;

            _initialReservoir.BindTo(_resampleProgram, 3u);
            _temporalReservoir.BindTo(_resampleProgram, 4u);
            _spatialReservoir.BindTo(_resampleProgram, 5u);
            _resampleProgram.Sampler("gDepth", depthTex, 0);
            _resampleProgram.Sampler("gNormal", normalTex, 1);
            _resampleProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _resampleProgram.Uniform("invRes", new Vector2(1.0f / width, 1.0f / height));
            _resampleProgram.Uniform("frameIndex", _frameIndex);
            _resampleProgram.Uniform("invProjMatrix", invProj);
            _resampleProgram.Uniform("cameraToWorldMatrix", cameraToWorld);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _resampleProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchFinal(uint width, uint height, XRTexture2D restirTex)
        {
            if (_finalProgram is null || _spatialReservoir is null)
                return;

            _spatialReservoir.BindTo(_finalProgram, 5u);
            _finalProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _finalProgram.BindImageTexture(6u, restirTex, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _finalProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
        }
    }
}
