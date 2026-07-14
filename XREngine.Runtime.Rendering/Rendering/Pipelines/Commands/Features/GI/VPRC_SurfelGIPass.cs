using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Controls which SSBO the surfel shaders use for world-matrix lookups.
    /// </summary>
    public enum ESurfelTransformSource
    {
        /// <summary>
        /// Bind GPUScene.TransformBuffer directly (16 floats per transform, binding 6).
        /// </summary>
        GpuCommands,

        /// <summary>
        /// Historical transform-atlas mode. Phase C aliases this to GPUScene.TransformBuffer.
        /// </summary>
        CompactTransformAtlas,
    }

    /// <summary>
    /// Surfel-based GI (GIBS-inspired) initial implementation.
    ///
    /// This first pass implements:
    /// - Surfel spawning from the GBuffer in 16x16 tiles (approximation of the paper's coverage-driven spawning)
    /// - A very naive surfel gather to produce a non-zero GI texture for validation
    ///
    /// Persistence/recycling, non-linear grids, and ray-traced irradiance integration are subsequent steps.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_SurfelGIPass : ViewportRenderCommand
    {
        private static void LogGuardFailure(string location, string reason)
            => Debug.RenderingEvery(
                $"SurfelGI.{location}",
                TimeSpan.FromSeconds(1),
                "[SurfelGI][RESILIENCE GUARD TRIGGERED] {0}: {1}",
                location,
                reason);

        private static string DescribeTexture(XRTexture? texture)
            => texture is null
                ? "missing"
                : $"{texture.Name ?? "<unnamed>"} ({texture.GetType().Name})";

        private static string DescribeFbo(XRFrameBuffer? fbo)
            => fbo is null
                ? "missing"
                : $"{fbo.Name ?? "<unnamed>"} ({fbo.GetType().Name})";

        private const uint GroupSize = 16u;

        private const uint CulledCommandFloats = GPUScene.CommandFloatCount;

        // Keep this moderate to avoid memory spikes; 131072 * 64 bytes ~= 8 MB.
        public const uint MaxSurfelsConst = 131072u;

        public const uint GridDimXConst = 32u;
        public const uint GridDimYConst = 32u;
        public const uint GridDimZConst = 32u;
        public const uint GridMaxPerCellConst = 16u;

        internal const string SurfelBufferName = "SurfelGI_Surfels";
        internal const string CounterBufferName = "SurfelGI_Counters";
        internal const string FreeStackBufferName = "SurfelGI_FreeStack";
        internal const string GridCountsBufferName = "SurfelGI_GridCounts";
        internal const string GridIndicesBufferName = "SurfelGI_GridIndices";

        internal static readonly uint SurfelStride = (uint)Marshal.SizeOf<SurfelGPU>();
        internal const uint CounterCount = 4u;
        internal const uint ScalarStride = sizeof(uint);
        internal const uint GridCellCount = GridDimXConst * GridDimYConst * GridDimZConst;
        internal const uint GridIndexCount = GridCellCount * GridMaxPerCellConst;

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

        private XRDataBuffer<SurfelGPU>? _surfelBuffer;
        private XRDataBuffer<int>? _counterBuffer;
        private XRDataBuffer<uint>? _freeStackBuffer;
        private XRDataBuffer<uint>? _gridCountsBuffer;
        private XRDataBuffer<uint>? _gridIndicesBuffer;
        private XRDataBuffer<Matrix4x4>? _transformAtlasBuffer;
        private uint _transformAtlasElementCount;

        private bool _initialized;

        private uint _frameIndex;

        /// <summary>
        /// Selects whether surfel shaders read world matrices from the full GPU commands
        /// buffer or from a compact transform-only SSBO built each frame.
        /// Defaults to <see cref="ESurfelTransformSource.GpuCommands"/>.
        /// </summary>
        public ESurfelTransformSource TransformSource { get; set; } = ESurfelTransformSource.GpuCommands;

        // Expose buffers and parameters for debug visualization
        public XRDataBuffer? SurfelBuffer => _surfelBuffer;
        public XRDataBuffer? CounterBuffer => _counterBuffer;
        public XRDataBuffer? FreeStackBuffer => _freeStackBuffer;
        public XRDataBuffer? GridCountsBuffer => _gridCountsBuffer;
        public XRDataBuffer? GridIndicesBuffer => _gridIndicesBuffer;
        public XRDataBuffer? TransformAtlasBuffer => _transformAtlasBuffer;
        public uint TransformAtlasElementCount => _transformAtlasElementCount;

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

        protected override bool ShouldExecuteThisFrame()
            => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline switch
            {
                DefaultRenderPipeline pipeline => pipeline.UsesSurfelGI,
                DefaultRenderPipeline2 pipeline => pipeline.UsesSurfelGI,
                _ => false,
            };

        protected override void Execute()
        {
            bool usesSurfelGI;
            bool stereo;
            switch (ActivePipelineInstance.Pipeline)
            {
                case DefaultRenderPipeline pipeline:
                    usesSurfelGI = pipeline.UsesSurfelGI;
                    stereo = pipeline.Stereo;
                    break;
                case DefaultRenderPipeline2 pipeline:
                    usesSurfelGI = pipeline.UsesSurfelGI;
                    stereo = pipeline.Stereo;
                    break;
                default:
                    usesSurfelGI = false;
                    stereo = false;
                    break;
            }
            if (!usesSurfelGI)
                return;

            // Mono-only for the first iteration.
            if (stereo)
            {
                var anyOutput = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);
                anyOutput?.Clear(ColorF4.Transparent);
                return;
            }

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            if (camera is null)
            {
                LogGuardFailure(nameof(Execute), "Scene camera unavailable; mono Surfel GI skipped.");
                return;
            }

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            if (region.Width <= 0 || region.Height <= 0)
            {
                LogGuardFailure(nameof(Execute), $"Invalid render region {region.Width}x{region.Height}; mono Surfel GI skipped.");
                return;
            }

            uint width = (uint)region.Width;
            uint height = (uint)region.Height;

            if (!EnsurePrograms())
            {
                LogGuardFailure(nameof(Execute), "Compute programs failed to initialize for mono Surfel GI.");
                return;
            }

            if (!RefreshDeclaredBuffers())
            {
                LogGuardFailure(nameof(Execute), "Required declared Surfel GI buffers are unavailable in the active pipeline generation.");
                return;
            }

            XRTexture? depthInput = ActivePipelineInstance.GetTexture<XRTexture>(DepthTextureName);
            XRTexture? normalInput = ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? albedoInput = ActivePipelineInstance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? transformIdInput = ActivePipelineInstance.GetTexture<XRTexture>(TransformIdTextureName);
            XRTexture? outputInput = ActivePipelineInstance.GetTexture<XRTexture>(OutputTextureName);

            if (depthInput is not XRTexture2D depthTex ||
                normalInput is not XRTexture2D normalTex ||
                albedoInput is not XRTexture2D albedoTex ||
                transformIdInput is not XRTexture2D transformIdTex ||
                outputInput is not XRTexture2D outputTex)
            {
                LogGuardFailure(
                    nameof(Execute),
                    $"Mono Surfel GI inputs unavailable. depth={DescribeTexture(depthInput)}, normal={DescribeTexture(normalInput)}, albedo={DescribeTexture(albedoInput)}, transformId={DescribeTexture(transformIdInput)}, output={DescribeTexture(outputInput)}, deferredMsaa={DefaultRenderPipeline.RuntimeEnableMsaaDeferred}, stereo={stereo}");
                return;
            }

            XRQuadFrameBuffer? compositeFbo = ActivePipelineInstance.GetFBO<XRQuadFrameBuffer>(CompositeQuadFBOName);
            XRFrameBuffer? forwardFbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(ForwardFBOName);
            if (compositeFbo is null || forwardFbo is null)
            {
                LogGuardFailure(
                    nameof(Execute),
                    $"Mono Surfel GI composite targets unavailable. composite={DescribeFbo(compositeFbo)}, forward={DescribeFbo(forwardFbo)}, deferredMsaa={DefaultRenderPipeline.RuntimeEnableMsaaDeferred}");
                return;
            }

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
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/Init.comp", EShaderType.Compute);
                _initProgram = new XRRenderProgram(true, false, shader);
            }

            if (_recycleProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/Recycle.comp", EShaderType.Compute);
                _recycleProgram = new XRRenderProgram(true, false, shader);
            }

            if (_resetGridProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/ResetGrid.comp", EShaderType.Compute);
                _resetGridProgram = new XRRenderProgram(true, false, shader);
            }

            if (_buildGridProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/BuildGrid.comp", EShaderType.Compute);
                _buildGridProgram = new XRRenderProgram(true, false, shader);
            }

            if (_spawnProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/Spawn.comp", EShaderType.Compute);
                _spawnProgram = new XRRenderProgram(true, false, shader);
            }

            if (_shadeProgram is null)
            {
                var shader = XRShader.EngineShader("Compute/GI/SurfelGI/Shade.comp", EShaderType.Compute);
                _shadeProgram = new XRRenderProgram(true, false, shader);
            }

            return _initProgram is not null && _recycleProgram is not null && _resetGridProgram is not null && _buildGridProgram is not null && _spawnProgram is not null && _shadeProgram is not null;
        }

        private bool RefreshDeclaredBuffers()
        {
            var surfels = ActivePipelineInstance.GetBuffer(SurfelBufferName) as XRDataBuffer<SurfelGPU>;
            var counters = ActivePipelineInstance.GetBuffer(CounterBufferName) as XRDataBuffer<int>;
            var freeStack = ActivePipelineInstance.GetBuffer(FreeStackBufferName) as XRDataBuffer<uint>;
            var gridCounts = ActivePipelineInstance.GetBuffer(GridCountsBufferName) as XRDataBuffer<uint>;
            var gridIndices = ActivePipelineInstance.GetBuffer(GridIndicesBufferName) as XRDataBuffer<uint>;

            bool generationChanged =
                !ReferenceEquals(_surfelBuffer, surfels) ||
                !ReferenceEquals(_counterBuffer, counters) ||
                !ReferenceEquals(_freeStackBuffer, freeStack) ||
                !ReferenceEquals(_gridCountsBuffer, gridCounts) ||
                !ReferenceEquals(_gridIndicesBuffer, gridIndices);

            _surfelBuffer = surfels;
            _counterBuffer = counters;
            _freeStackBuffer = freeStack;
            _gridCountsBuffer = gridCounts;
            _gridIndicesBuffer = gridIndices;

            if (generationChanged)
                _initialized = false;

            return surfels is not null && counters is not null && freeStack is not null && gridCounts is not null && gridIndices is not null;
        }

        internal static XRDataBuffer CreateDeclaredSurfelBuffer()
            => CreateDeclaredBuffer<SurfelGPU>(SurfelBufferName, MaxSurfelsConst, 0u, padEndingToVec4: false);

        internal static XRDataBuffer CreateDeclaredCounterBuffer()
            => CreateDeclaredBuffer<int>(CounterBufferName, CounterCount, 1u, padEndingToVec4: true);

        internal static XRDataBuffer CreateDeclaredFreeStackBuffer()
            => CreateDeclaredBuffer<uint>(FreeStackBufferName, MaxSurfelsConst, 2u, padEndingToVec4: false);

        internal static XRDataBuffer CreateDeclaredGridCountsBuffer()
            => CreateDeclaredBuffer<uint>(GridCountsBufferName, GridCellCount, 3u, padEndingToVec4: false);

        internal static XRDataBuffer CreateDeclaredGridIndicesBuffer()
            => CreateDeclaredBuffer<uint>(GridIndicesBufferName, GridIndexCount, 4u, padEndingToVec4: false);

        private static XRDataBuffer<T> CreateDeclaredBuffer<T>(string name, uint elementCount, uint bindingIndex, bool padEndingToVec4)
            where T : unmanaged
        {
            var buffer = new XRDataBuffer<T>(name, EBufferTarget.ShaderStorageBuffer, elementCount)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = bindingIndex,
                DisposeOnPush = false,
                PadEndingToVec4 = padEndingToVec4
            };
            buffer.PushData();
            return buffer;
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
            var scene = ActivePipelineInstance.RenderState.Scene;
            var gpuScene = scene?.GPUCommands;
            XRDataBuffer? transforms = gpuScene?.TransformBuffer;
            if (transforms is null)
            {
                program.Uniform("useTransformAtlas", false);
                program.Uniform("transformAtlasCount", 0u);
                program.Uniform("hasCulledCommands", false);
                program.Uniform("culledFloatCount", 0u);
                program.Uniform("culledCommandFloats", CulledCommandFloats);
                return;
            }

            transforms.BindTo(program, 6u);
            program.Uniform("useTransformAtlas", true);
            program.Uniform("transformAtlasCount", transforms.ElementCount * 16u);
            program.Uniform("hasCulledCommands", false);
            program.Uniform("culledFloatCount", 0u);
            program.Uniform("culledCommandFloats", CulledCommandFloats);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_SurfelGIPass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(DepthTextureName));
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.SampleTexture(MakeTextureResource(AlbedoTextureName));
            builder.SampleTexture(MakeTextureResource(TransformIdTextureName));

            builder.ReadWriteBuffer(SurfelBufferName);
            builder.ReadWriteBuffer(CounterBufferName);
            builder.ReadWriteBuffer(FreeStackBufferName);
            builder.ReadWriteBuffer(GridCountsBufferName);
            builder.ReadWriteBuffer(GridIndicesBufferName);

            builder.ReadWriteTexture(MakeTextureResource(OutputTextureName));
            builder.SampleTexture(MakeTextureResource(OutputTextureName));
            builder.UseColorAttachment(MakeFboColorResource(ForwardFBOName));
        }
    }
}
