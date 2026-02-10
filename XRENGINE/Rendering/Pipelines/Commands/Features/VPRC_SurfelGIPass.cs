using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Surfel-based GI (GIBS-inspired) initial implementation.
    ///
    /// This first pass implements:
    /// - Surfel spawning from the GBuffer in 16x16 tiles (approximation of the paper's coverage-driven spawning)
    /// - A very naive surfel gather to produce a non-zero GI texture for validation
    ///
    /// Persistence/recycling, non-linear grids, and ray-traced irradiance integration are subsequent steps.
    /// </summary>
    public class VPRC_SurfelGIPass : ViewportRenderCommand
    {
        private const uint GroupSize = 16u;

        private const uint CulledCommandFloats = 48u;

        // Keep this moderate to avoid memory spikes; 131072 * 64 bytes ~= 8 MB.
        public const uint MaxSurfelsConst = 131072u;

        public const uint GridDimXConst = 32u;
        public const uint GridDimYConst = 32u;
        public const uint GridDimZConst = 32u;
        public const uint GridMaxPerCellConst = 16u;

        // Coarse world-space grid centered at the camera.
        public const float GridHalfExtentConst = 50.0f;
        private const uint MaxSurfelAgeFrames = 300u;

        // Aliases for internal use (keep existing code working)
        private const uint MaxSurfels = MaxSurfelsConst;
        private const uint GridDimX = GridDimXConst;
        private const uint GridDimY = GridDimYConst;
        private const uint GridDimZ = GridDimZConst;
        private const uint GridMaxPerCell = GridMaxPerCellConst;
        private const float GridHalfExtent = GridHalfExtentConst;

        private XRRenderProgram? _initProgram;
        private XRRenderProgram? _recycleProgram;
        private XRRenderProgram? _resetGridProgram;
        private XRRenderProgram? _buildGridProgram;
        private XRRenderProgram? _spawnProgram;
        private XRRenderProgram? _shadeProgram;

        private XRDataBuffer? _surfelBuffer;
        private XRDataBuffer? _counterBuffer;
        private XRDataBuffer? _freeStackBuffer;
        private XRDataBuffer? _gridCountsBuffer;
        private XRDataBuffer? _gridIndicesBuffer;

        private bool _initialized;

        private uint _frameIndex;

        // Expose buffers and parameters for debug visualization
        public XRDataBuffer? SurfelBuffer => _surfelBuffer;
        public XRDataBuffer? CounterBuffer => _counterBuffer;
        public XRDataBuffer? FreeStackBuffer => _freeStackBuffer;
        public XRDataBuffer? GridCountsBuffer => _gridCountsBuffer;
        public XRDataBuffer? GridIndicesBuffer => _gridIndicesBuffer;

        /// <summary>
        /// Current grid origin (world-space position of grid corner).
        /// Updated each frame when Execute() runs.
        /// </summary>
        public Vector3 CurrentGridOrigin { get; private set; }

        /// <summary>
        /// Current cell size in world units.
        /// </summary>
        public float CurrentCellSize { get; private set; }

        public string DepthTextureName { get; set; } = DefaultRenderPipeline.DepthViewTextureName;
        public string NormalTextureName { get; set; } = DefaultRenderPipeline.NormalTextureName;
        public string AlbedoTextureName { get; set; } = DefaultRenderPipeline.AlbedoOpacityTextureName;
        public string TransformIdTextureName { get; set; } = DefaultRenderPipeline.TransformIdTextureName;

        public int SourceRenderPassIndex { get; set; } = (int)EDefaultRenderPass.OpaqueDeferred;

        public string OutputTextureName { get; set; } = DefaultRenderPipeline.SurfelGITextureName;
        public string CompositeQuadFBOName { get; set; } = DefaultRenderPipeline.SurfelGICompositeFBOName;
        public string ForwardFBOName { get; set; } = DefaultRenderPipeline.ForwardPassFBOName;

        [StructLayout(LayoutKind.Sequential)]
        private struct SurfelGPU
        {
            // Stored as local/object space for motion-stable surfel reuse.
            // World-space is reconstructed in shaders using meta.z (TransformId).
            public Vector4 PositionRadius; // xyz=localPos, w=worldRadius
            public Vector4 Normal; // xyz=localNormal
            public Vector4 Albedo;
            public Vector4 Meta; // x=frameIndex, y=reserved, z=reserved, w=reserved
        }

        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not DefaultRenderPipeline pipeline || !pipeline.UsesSurfelGI)
                return;

            // Mono-only for the first iteration.
            if (pipeline.Stereo)
            {
                var anyOutput = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);
                anyOutput?.Clear(ColorF4.Transparent);
                return;
            }

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

            EnsureBuffers();
            if (_surfelBuffer is null || _counterBuffer is null)
                return;

            if (ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName) is not XRTexture2D depthTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName) is not XRTexture2D normalTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(AlbedoTextureName) is not XRTexture2D albedoTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(TransformIdTextureName) is not XRTexture2D transformIdTex ||
                ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName) is not XRTexture2D outputTex)
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

            Vector3 cameraPos = camera.Transform.RenderTranslation;
            Vector3 gridOrigin = cameraPos - new Vector3(GridHalfExtent);
            float cellSize = (GridHalfExtent * 2.0f) / GridDimX;

            // Store for debug visualization access
            CurrentGridOrigin = gridOrigin;
            CurrentCellSize = cellSize;

            if (!_initialized)
            {
                DispatchInit();
                _initialized = true;
            }

            DispatchRecycle();
            DispatchResetGrid();
            DispatchBuildGrid(gridOrigin, cellSize);

            DispatchSpawn(width, height, invProj, cameraToWorld, depthTex, normalTex, albedoTex, transformIdTex, gridOrigin, cellSize);
            DispatchShade(width, height, invProj, cameraToWorld, depthTex, normalTex, albedoTex, outputTex, gridOrigin, cellSize);

            compositeFbo.Render(forwardFbo);

            _frameIndex++;
        }

        private bool EnsurePrograms()
        {
            if (_initProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/Init.comp", EShaderType.Compute);
                _initProgram = new XRRenderProgram(true, false, shader);
            }

            if (_recycleProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/Recycle.comp", EShaderType.Compute);
                _recycleProgram = new XRRenderProgram(true, false, shader);
            }

            if (_resetGridProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/ResetGrid.comp", EShaderType.Compute);
                _resetGridProgram = new XRRenderProgram(true, false, shader);
            }

            if (_buildGridProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/BuildGrid.comp", EShaderType.Compute);
                _buildGridProgram = new XRRenderProgram(true, false, shader);
            }

            if (_spawnProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/Spawn.comp", EShaderType.Compute);
                _spawnProgram = new XRRenderProgram(true, false, shader);
            }

            if (_shadeProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/SurfelGI/Shade.comp", EShaderType.Compute);
                _shadeProgram = new XRRenderProgram(true, false, shader);
            }

            return _initProgram is not null && _recycleProgram is not null && _resetGridProgram is not null && _buildGridProgram is not null && _spawnProgram is not null && _shadeProgram is not null;
        }

        private void EnsureBuffers()
        {
            if (_surfelBuffer is null)
            {
                _surfelBuffer = new XRDataBuffer(
                    "SurfelGI_Surfels",
                    EBufferTarget.ShaderStorageBuffer,
                    MaxSurfels,
                    EComponentType.Struct,
                    (uint)Marshal.SizeOf<SurfelGPU>(),
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    BindingIndexOverride = 0u,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                _surfelBuffer.PushData();
            }

            if (_counterBuffer is null)
            {
                // 4 ints so it naturally aligns to a vec4 in std430.
                _counterBuffer = new XRDataBuffer(
                    "SurfelGI_Counters",
                    EBufferTarget.ShaderStorageBuffer,
                    4u,
                    EComponentType.Int,
                    1u,
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    BindingIndexOverride = 1u,
                    DisposeOnPush = false,
                    PadEndingToVec4 = true
                };
                _counterBuffer.PushData();
            }

            if (_freeStackBuffer is null)
            {
                _freeStackBuffer = new XRDataBuffer(
                    "SurfelGI_FreeStack",
                    EBufferTarget.ShaderStorageBuffer,
                    MaxSurfels,
                    EComponentType.UInt,
                    1u,
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    BindingIndexOverride = 2u,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                _freeStackBuffer.PushData();
            }

            uint cellCount = GridDimX * GridDimY * GridDimZ;
            if (_gridCountsBuffer is null)
            {
                _gridCountsBuffer = new XRDataBuffer(
                    "SurfelGI_GridCounts",
                    EBufferTarget.ShaderStorageBuffer,
                    cellCount,
                    EComponentType.UInt,
                    1u,
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    BindingIndexOverride = 3u,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                _gridCountsBuffer.PushData();
            }

            if (_gridIndicesBuffer is null)
            {
                _gridIndicesBuffer = new XRDataBuffer(
                    "SurfelGI_GridIndices",
                    EBufferTarget.ShaderStorageBuffer,
                    cellCount * GridMaxPerCell,
                    EComponentType.UInt,
                    1u,
                    false,
                    false)
                {
                    Usage = EBufferUsage.DynamicDraw,
                    BindingIndexOverride = 4u,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                _gridIndicesBuffer.PushData();
            }
        }

        private void DispatchInit()
        {
            if (_initProgram is null || _surfelBuffer is null || _counterBuffer is null || _freeStackBuffer is null)
                return;

            _surfelBuffer.BindTo(_initProgram, 0u);
            _counterBuffer.BindTo(_initProgram, 1u);
            _freeStackBuffer.BindTo(_initProgram, 2u);
            _initProgram.Uniform("maxSurfels", MaxSurfels);

            uint groups = (MaxSurfels + 255u) / 256u;
            _initProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchRecycle()
        {
            if (_recycleProgram is null || _surfelBuffer is null || _counterBuffer is null || _freeStackBuffer is null)
                return;

            _surfelBuffer.BindTo(_recycleProgram, 0u);
            _counterBuffer.BindTo(_recycleProgram, 1u);
            _freeStackBuffer.BindTo(_recycleProgram, 2u);
            _recycleProgram.Uniform("maxSurfels", MaxSurfels);
            _recycleProgram.Uniform("frameIndex", _frameIndex);
            _recycleProgram.Uniform("maxAgeFrames", MaxSurfelAgeFrames);

            uint groups = (MaxSurfels + 255u) / 256u;
            _recycleProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchResetGrid()
        {
            if (_resetGridProgram is null || _gridCountsBuffer is null)
                return;

            uint cellCount = GridDimX * GridDimY * GridDimZ;
            _gridCountsBuffer.BindTo(_resetGridProgram, 3u);
            _resetGridProgram.Uniform("cellCount", cellCount);

            uint groups = (cellCount + 255u) / 256u;
            _resetGridProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchBuildGrid(Vector3 gridOrigin, float cellSize)
        {
            if (_buildGridProgram is null || _surfelBuffer is null || _gridCountsBuffer is null || _gridIndicesBuffer is null)
                return;

            BindCulledCommandsIfAvailable(_buildGridProgram);

            _surfelBuffer.BindTo(_buildGridProgram, 0u);
            _gridCountsBuffer.BindTo(_buildGridProgram, 3u);
            _gridIndicesBuffer.BindTo(_buildGridProgram, 4u);
            _buildGridProgram.Uniform("maxSurfels", MaxSurfels);
            _buildGridProgram.Uniform("gridOrigin", gridOrigin);
            _buildGridProgram.Uniform("cellSize", cellSize);
            _buildGridProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));
            _buildGridProgram.Uniform("maxPerCell", GridMaxPerCell);

            uint groups = (MaxSurfels + 255u) / 256u;
            _buildGridProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchSpawn(uint width, uint height, Matrix4x4 invProj, Matrix4x4 cameraToWorld, XRTexture2D depthTex, XRTexture2D normalTex, XRTexture2D albedoTex, XRTexture2D transformIdTex, Vector3 gridOrigin, float cellSize)
        {
            if (_spawnProgram is null || _surfelBuffer is null || _counterBuffer is null || _freeStackBuffer is null || _gridCountsBuffer is null || _gridIndicesBuffer is null)
                return;

            uint tileCountX = (width + GroupSize - 1u) / GroupSize;
            uint tileCountY = (height + GroupSize - 1u) / GroupSize;

            _surfelBuffer.BindTo(_spawnProgram, 0u);
            _counterBuffer.BindTo(_spawnProgram, 1u);
            _freeStackBuffer.BindTo(_spawnProgram, 2u);
            _gridCountsBuffer.BindTo(_spawnProgram, 3u);
            _gridIndicesBuffer.BindTo(_spawnProgram, 4u);

            BindCulledCommandsIfAvailable(_spawnProgram);

            _spawnProgram.Sampler("gDepth", depthTex, 0);
            _spawnProgram.Sampler("gNormal", normalTex, 1);
            _spawnProgram.Sampler("gAlbedo", albedoTex, 2);
            _spawnProgram.Sampler("gTransformId", transformIdTex, 3);
            _spawnProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _spawnProgram.Uniform("invProjMatrix", invProj);
            _spawnProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _spawnProgram.Uniform("frameIndex", _frameIndex);
            _spawnProgram.Uniform("maxSurfels", MaxSurfels);

            _spawnProgram.Uniform("gridOrigin", gridOrigin);
            _spawnProgram.Uniform("cellSize", cellSize);
            _spawnProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));
            _spawnProgram.Uniform("maxPerCell", GridMaxPerCell);

            _spawnProgram.DispatchCompute(tileCountX, tileCountY, 1u, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchShade(uint width, uint height, Matrix4x4 invProj, Matrix4x4 cameraToWorld, XRTexture2D depthTex, XRTexture2D normalTex, XRTexture2D albedoTex, XRTexture2D outputTex, Vector3 gridOrigin, float cellSize)
        {
            if (_shadeProgram is null || _surfelBuffer is null || _counterBuffer is null || _gridCountsBuffer is null || _gridIndicesBuffer is null)
                return;

            BindCulledCommandsIfAvailable(_shadeProgram);

            _surfelBuffer.BindTo(_shadeProgram, 0u);
            _counterBuffer.BindTo(_shadeProgram, 1u);
            _gridCountsBuffer.BindTo(_shadeProgram, 3u);
            _gridIndicesBuffer.BindTo(_shadeProgram, 4u);

            _shadeProgram.Sampler("gDepth", depthTex, 0);
            _shadeProgram.Sampler("gNormal", normalTex, 1);
            _shadeProgram.Sampler("gAlbedo", albedoTex, 2);
            _shadeProgram.BindImageTexture(3u, outputTex, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA16F);
            _shadeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
            _shadeProgram.Uniform("invProjMatrix", invProj);
            _shadeProgram.Uniform("cameraToWorldMatrix", cameraToWorld);
            _shadeProgram.Uniform("frameIndex", _frameIndex);
            _shadeProgram.Uniform("maxSurfels", MaxSurfels);

            _shadeProgram.Uniform("gridOrigin", gridOrigin);
            _shadeProgram.Uniform("cellSize", cellSize);
            _shadeProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));
            _shadeProgram.Uniform("maxPerCell", GridMaxPerCell);

            uint groupX = (width + GroupSize - 1u) / GroupSize;
            uint groupY = (height + GroupSize - 1u) / GroupSize;
            _shadeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.ShaderImageAccess);
        }

        private void BindCulledCommandsIfAvailable(XRRenderProgram program)
        {
            // NOTE: SurfelGI uses TransformId as the *command index*.
            // Therefore, we must bind the full commands buffer indexed by commandIndex,
            // not the compacted culled-visible commands list.
            var scene = ActivePipelineInstance.RenderState.Scene;
            var gpuScene = scene?.GPUCommands;
            XRDataBuffer? commands = gpuScene is null ? null : gpuScene.AllLoadedCommandsBuffer;
            if (commands is null)
            {
                program.Uniform("hasCulledCommands", false);
                program.Uniform("culledFloatCount", 0u);
                program.Uniform("culledCommandFloats", CulledCommandFloats);
                return;
            }

            commands.BindTo(program, 5u);
            program.Uniform("hasCulledCommands", true);
            // Shader interprets the SSBO as a flat float array; bounds checks are in float units.
            // ElementCount here is the number of commands, not the number of floats.
            program.Uniform("culledFloatCount", commands.ElementCount * CulledCommandFloats);
            program.Uniform("culledCommandFloats", CulledCommandFloats);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_SurfelGIPass), RenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(DepthTextureName));
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.SampleTexture(MakeTextureResource(AlbedoTextureName));
            builder.SampleTexture(MakeTextureResource(TransformIdTextureName));

            builder.ReadWriteBuffer("SurfelGI_Surfels");
            builder.ReadWriteBuffer("SurfelGI_Counters");
            builder.ReadWriteBuffer("SurfelGI_FreeStack");
            builder.ReadWriteBuffer("SurfelGI_GridCounts");
            builder.ReadWriteBuffer("SurfelGI_GridIndices");

            builder.ReadWriteTexture(MakeTextureResource(OutputTextureName));
            builder.SampleTexture(MakeTextureResource(OutputTextureName));
            builder.UseColorAttachment(MakeFboColorResource(ForwardFBOName));
        }
    }
}
