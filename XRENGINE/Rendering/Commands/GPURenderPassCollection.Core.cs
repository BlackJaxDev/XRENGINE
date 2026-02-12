using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// GPU-based render pass orchestration (core fields & properties).
    /// See other partial class files for culling, sorting, indirect build, materials, and initialization logic.
    /// </summary>
    public sealed partial class GPURenderPassCollection : XRBase, IDisposable
    {
        /// <summary>
        /// Expected stride for DrawElementsIndirectCommand (5 uints = 20 bytes).
        /// This must match the OpenGL spec and the shader's DRAW_COMMAND_UINTS.
        /// </summary>
        private const uint ExpectedIndirectCommandStride = 20;
        
        private static readonly uint _indirectCommandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        private static readonly uint _indirectCommandComponentCount = _indirectCommandStride / sizeof(uint);

        static GPURenderPassCollection()
        {
            // Static assertion: DrawElementsIndirectCommand must be exactly 20 bytes (5 uints)
            // to match OpenGL spec and shader layout. Struct packing issues will cause MDI failures.
            if (_indirectCommandStride != ExpectedIndirectCommandStride)
            {
                throw new InvalidOperationException(
                    $"DrawElementsIndirectCommand struct size mismatch! Expected {ExpectedIndirectCommandStride} bytes, got {_indirectCommandStride}. " +
                    $"Check [StructLayout(Pack = 1)] attribute and field types.");
            }

            // Validate ViewSet payload sizes so CPU/GPU packing stays in sync.
            GPUViewSetLayout.ValidateRuntimeLayout();
        }

        // Verbose debug infra (shared across partials)
        private static volatile bool _verbose = true;

        private int _renderPass = 0;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        /// <summary>
        /// The resulting culled commands that will be rendered in this pass.
        /// </summary>
        public XRDataBuffer CulledSceneToRenderBuffer
            => _culledSceneToRenderBuffer ??= MakeCulledSceneToRenderBuffer(GPUScene.MinCommandCount);

        /// <summary>
        /// Debug switches for diagnosing the indirect rendering pipeline.
        /// Call <see cref="ConfigureIndirectDebug"/> to modify at runtime.
        /// </summary>
        public sealed class IndirectDebugSettings
        {
            /// <summary>
            /// Forces the CPU fallback draw-count path to run every frame.
            /// Useful for validating whether GPU-written counts are corrupt.
            /// </summary>
            public bool ForceCpuFallbackCount { get; set; }

            /// <summary>
            /// When true, the indirect count buffer is never bound and the renderer
            /// always falls back to glMultiDrawElementsIndirect.
            /// </summary>
            public bool DisableCountDrawPath { get; set; }

            /// <summary>
            /// Skips clearing stale entries in the indirect buffer tail to measure impact.
            /// </summary>
            public bool SkipIndirectTailClear { get; set; } = false;

            /// <summary>
            /// Dumps the first few indirect commands and draw count whenever they change.
            /// </summary>
            public bool DumpIndirectArguments { get; set; } = true;

            /// <summary>
            /// Rebuilds indirect draw commands on the CPU using the scene's mesh metadata instead
            /// of the GPU compute shader. Useful for confirming VAO/material state independent of
            /// the GPU build path.
            /// </summary>
            public bool ForceCpuIndirectBuild { get; set; } = false;

            /// <summary>
            /// Logs writes into the shared draw-count buffer for visibility.
            /// </summary>
            public bool LogCountBufferWrites { get; set; } = true;

            /// <summary>
            /// Forces parameter buffers to remap if mapping state looks stale.
            /// </summary>
            public bool ForceParameterRemap { get; set; }

            /// <summary>
            /// Validates buffer sizes/strides before issuing indirect draws.
            /// </summary>
            public bool ValidateBufferLayouts { get; set; } = true;

            /// <summary>
            /// Enables additional sanity checks against freed/invalid GPU objects.
            /// </summary>
            public bool ValidateLiveHandles { get; set; } = true;

            /// <summary>
            /// Disables CPU-side readback of the GPU draw-count buffer to avoid costly map/unmap cycles.
            /// When disabled, systems relying on TryReadDrawCount will fall back to conservative defaults.
            /// </summary>
            public bool DisableCpuReadbackCount { get; set; } = true;

            /// <summary>
            /// Enables CPU-side mapping/inspection of the culled command buffer to build material batches.
            /// Emergency diagnostics fallback only; default render flow should stay GPU-driven.
            /// </summary>
            public bool EnableCpuBatching { get; set; } = false;

            /// <summary>
            /// Dumps a snapshot of source commands before the GPU copy shader runs.
            /// </summary>
            public bool ProbeSourceCommandsBeforeCopy { get; set; } = true;

            /// <summary>
            /// Number of commands to sample when <see cref="ProbeSourceCommandsBeforeCopy"/> is enabled.
            /// </summary>
            public uint ProbeSourceCommandCount { get; set; } = 8;

            /// <summary>
            /// Enables bounds checking for the copy shader's atomic counter to detect overflow.
            /// </summary>
            public bool ValidateCopyCommandAtomicBounds { get; set; } = true;
        }

        private static readonly IndirectDebugSettings _indirectDebug = new();
        public static IndirectDebugSettings IndirectDebug => _indirectDebug;

        public static void ConfigureIndirectDebug(Action<IndirectDebugSettings> configure)
        {
            if (configure is null)
                return;

            lock (_indirectDebug)
                configure(_indirectDebug);
        }

        private static readonly HashSet<string> _debugCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Lifecycle",
            "Buffers",
            "Culling",
            "Draw",
            "VAO",
            "Shaders",
            "Timing",
            "Validation",
            "Sorting",
            "Indirect",
            "SoA",
            "Materials",
            "Stats",
            "General"
        };

        public static void SetVerbose(bool enabled)
            => _verbose = enabled;

        public static void EnableCategory(string cat)
        {
            if (string.IsNullOrWhiteSpace(cat))
                return;

            lock(_debugCategories)
                _debugCategories.Add(cat);
        }

        public static void DisableCategory(string cat)
        {
            if (string.IsNullOrWhiteSpace(cat))
                return;

            lock (_debugCategories)
                _debugCategories.Remove(cat);
        }

        public static void SetCategories(IEnumerable<string> cats)
        {
            lock (_debugCategories)
            {
                _debugCategories.Clear();
                foreach (var c in cats.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!string.IsNullOrWhiteSpace(c))
                        _debugCategories.Add(c);
            }
        }
        
        private XRRenderPipelineInstance? _ownerPipeline;

        [System.Diagnostics.Conditional("DEBUG")]
        private void Dbg(string msg, string cat = "General")
        {
            if (!_verbose)
                return;

            if (!(Engine.EffectiveSettings.EnableGpuIndirectDebugLogging))
                return;

            bool enabled;
            lock (_debugCategories)
                enabled = _debugCategories.Contains(cat) || _debugCategories.Contains("All");

            if (enabled)
            {
                // Use the new structured logger for richer output
                var category = MapCategoryToLogCategory(cat);
                Log(category, LogLevel.Debug, $"{FormatDebugPrefix(cat)} {msg}");
            }
        }

        private static LogCategory MapCategoryToLogCategory(string cat)
        {
            return cat.ToLowerInvariant() switch
            {
                "lifecycle" => LogCategory.Lifecycle,
                "buffers" => LogCategory.Buffers,
                "culling" => LogCategory.Culling,
                "sorting" => LogCategory.Sorting,
                "indirect" => LogCategory.Indirect,
                "soa" => LogCategory.Buffers | LogCategory.Culling,
                "materials" => LogCategory.Materials,
                "stats" => LogCategory.Stats,
                "draw" => LogCategory.Draw,
                "vao" => LogCategory.VAO,
                "shaders" => LogCategory.Shaders,
                "timing" => LogCategory.Timing,
                "validation" => LogCategory.Validation,
                _ => LogCategory.Lifecycle
            };
        }

        private string FormatDebugPrefix(string cat)
        {
            XRRenderPipelineInstance? pipeline = _ownerPipeline ?? Engine.Rendering.State.CurrentRenderingPipeline;
            string descriptor = pipeline?.DebugDescriptor ?? "Pipeline=<none>";
            return $"[GPURenderPass/{cat}] {descriptor} Pass={RenderPass}";
        }

        internal void SetDebugContext(XRRenderPipelineInstance? pipeline, int passIndex)
        {
            _ownerPipeline = pipeline;
            RenderPass = passIndex;
        }

        // Primary working buffers
        private XRDataBuffer? _sortedCommandBuffer;        // Sorted output buffer (some algorithms)
        //private XRDataBuffer? _histogramBuffer;            // Histogram for direct radix sort
        /// <summary>
        /// The finalized indirect draw command buffer for this render pass to use in the multi draw indirect command.
        /// </summary>
    private XRDataBuffer? _indirectDrawBuffer;         // DrawElementsIndirectCommand array
    private XRDataBuffer? _culledSceneToRenderBuffer;  // Compacted visible commands
    private XRDataBuffer? _passFilterDebugBuffer;      // Optional GPU pass-filter instrumentation

        // Synchronization & lifecycle
        private readonly Lock _lock = new();
        private bool _disposed = false;
        private bool _initialized = false;
        private uint _lastMaxCommands = 0;
        private bool _buffersMapped = false;

        // Configuration flags
        //public bool UseSoA { get; set; } = false;  // Experimental Structure-of-Arrays path
        //public bool UseHiZ { get; set; } = false;  // HiZ acceleration (requires UseSoA)

        private static XRDataBuffer MakeCulledSceneToRenderBuffer(uint capacity)
        {
            XRDataBuffer buffer = new(
                $"CulledCommandsBuffer",
                EBufferTarget.ShaderStorageBuffer,
                capacity,
                EComponentType.Float,
                GPUScene.CommandFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.StreamDraw, //We're copying commands from the gpu scene buffer to this one every frame, preferably culled using the camera
                DisposeOnPush = false,
                Resizable = false,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                RangeFlags = EBufferMapRangeFlags.Read,
            };
            buffer.Generate();
            return buffer;
        }

        private GPUSortAlgorithm _sortAlgorithm = GPUSortAlgorithm.Bitonic;
        public GPUSortAlgorithm SortAlgorithm
        {
            get => _sortAlgorithm;
            set => SetField(ref _sortAlgorithm, value);
        }

        private GPUSortDirection _sortDirection = GPUSortDirection.Ascending;
        public GPUSortDirection SortDirection
        {
            get => _sortDirection;
            set => SetField(ref _sortDirection, value);
        }

        private bool _sortByDistance = true;
        public bool SortByDistance
        {
            get => _sortByDistance;
            set => SetField(ref _sortByDistance, value);
        }

        // Shader programs (compute pipeline stages)
        public XRRenderProgram? _cullingComputeShader;
        //public XRRenderProgram? SortingComputeShader;
        //public XRRenderProgram? RadixSortComputeShader;
        public XRRenderProgram? _buildKeysComputeShader;
        public XRRenderProgram? _buildGpuBatchesComputeShader;
        //public XRRenderProgram? RadixIndexSortComputeShader;
        public XRRenderProgram? _indirectRenderTaskShader;
        public XRRenderProgram? _resetCountersComputeShader;
        public XRRenderProgram? _debugDrawProgram;
        private XRRenderProgram? _copyCommandsProgram; // new: passthrough copy
        private XRRenderProgram? _bvhFrustumCullProgram; // BVH-accelerated frustum culling

        // Phase 3: Hi-Z occlusion (GPU_HiZ)
        private XRRenderProgram? _hiZInitProgram;
        private XRRenderProgram? _hiZGenProgram;
        private XRRenderProgram? _hiZOcclusionProgram;
        private XRRenderProgram? _copyCount3Program;
        private XRTexture2D? _hiZDepthPyramid;
        private XRTexture2D? _hiZDepthPyramidOwned;
        private int _hiZMaxMip;

        // Occlusion stage needs an input count that survives writing the final visible count.
        // We keep cull output counts in a scratch parameter buffer, then write final counts into _culledCountBuffer.
        private XRDataBuffer? _cullCountScratchBuffer;

        // Ping-pong output for occlusion refine (input is the current culled buffer; output becomes the new culled buffer).
        private XRDataBuffer? _occlusionCulledBuffer;
        private XRDataBuffer? _occlusionOverflowFlagBuffer;

        // Renderer used to issue indirect multi-draw calls
        public XRMeshRenderer? _indirectRenderer;

        // Visible count read-back
        private uint _visibleCommandCount = 0;
        public uint VisibleCommandCount
        {
            get => _visibleCommandCount;
            private set => SetField(ref _visibleCommandCount, value);
        }
        private uint _visibleInstanceCount = 0;
        public uint VisibleInstanceCount
        {
            get => _visibleInstanceCount;
            private set => SetField(ref _visibleInstanceCount, value);
        }

        // SoA double buffers & related
        private XRDataBuffer? _soaBoundingSpheresA;
        private XRDataBuffer? _soaMetadataA;
        private XRDataBuffer? _soaBoundingSpheresB;
        private XRDataBuffer? _soaMetadataB;
        private bool _useBufferAForRender = true;
        private XRRenderProgram? _extractSoAComputeShader;
        private XRRenderProgram? _soACullingComputeShader;
        //private XRRenderProgram? HiZSoACullingComputeShader;
        //private XRRenderProgram? _gatherProgram;
        private XRDataBuffer? _soaIndexList;
        //public int HiZMaxMip { get; set; } = 0;
        //private XRTexture? _hiZDepthPyramid;

        // Key-index radix buffers
        //private XRDataBuffer? _keyIndexBufferA;
        //private XRDataBuffer? _keyIndexBufferB;
        //private XRDataBuffer? _histogramIndexBuffer;

        // Stats & flags & counts
        private XRDataBuffer? _statsBuffer;
        private XRDataBuffer? _truncationFlagBuffer;
        private XRDataBuffer? _culledCountBuffer;
        private XRDataBuffer? _drawCountBuffer;
        private XRDataBuffer? _cullingOverflowFlagBuffer;
        private XRDataBuffer? _indirectOverflowFlagBuffer;
        private XRDataBuffer? _gpuBatchRangeBuffer;
        private XRDataBuffer? _gpuBatchCountBuffer;
        private XRDataBuffer? _instanceTransformBuffer;
        private XRDataBuffer? _instanceSourceIndexBuffer;
        private XRDataBuffer? _materialAggregationBuffer;
        private bool _gpuBatchingPreparedThisFrame;
        public bool EnableGpuDrivenBatching { get; set; } = true;
        public bool EnableGpuDrivenInstancing { get; set; } = true;

        /// <summary>
        /// If true, the material ID is included in the sorting key to reduce overdraw.
        /// </summary>
        //public bool UseMaterialBatchKey { get; set; } = false;

        private XRDataBuffer? _materialIDsBuffer;

        private GPUMaterialTable? _materialTable;
        public XRDataBuffer? MaterialTableBuffer => _materialTable?.Buffer;

        // Hybrid rendering manager (meshlets vs traditional indirect)
        private readonly HybridRenderingManager _renderManager = new() { UseMeshletPipeline = false };

        public GPURenderPassCollection(int renderPass)
        {
            RenderPass = renderPass;
            //Dbg($"Ctor renderPass={renderPass}","Lifecycle");
        }

        public void GetVisibleCounts(out uint drawCount, out uint instanceCount, out uint overflowMarker)
        {
            drawCount = VisibleCommandCount;
            instanceCount = VisibleInstanceCount;
            overflowMarker = 0u;

            if (_culledCountBuffer is null)
                return;

            if (IndirectDebug.DisableCpuReadbackCount && !IndirectDebug.ForceCpuFallbackCount)
                return;

            drawCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            instanceCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountInstanceIndex);
            overflowMarker = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountOverflowIndex);
        }

        public void SetMaterialTable(GPUMaterialTable table) => _materialTable = table;
    }

    public sealed partial class GPURenderPassCollection
    {
        // GPU-generated sort keys (uint4 per visible command): packed pass/pipeline/state, material, mesh, source index.
        private XRDataBuffer? _keyIndexBufferA;

        // Expose batches for the current pass for HybridRenderingManager
        public IReadOnlyList<HybridRenderingManager.DrawBatch>? CurrentBatches { get; private set; }
        public XRDataBuffer? InstanceTransformBuffer => _instanceTransformBuffer;
        public XRDataBuffer? InstanceSourceIndexBuffer => _instanceSourceIndexBuffer;
        public bool GpuBatchingPreparedThisFrame => _gpuBatchingPreparedThisFrame;

        // Simple passthrough for count/flag/stat buffer exposure
        public XRDataBuffer? CulledCountBuffer => _culledCountBuffer;
        public XRDataBuffer? DrawCountBuffer => _drawCountBuffer;
        public XRDataBuffer? IndirectDrawBuffer => _indirectDrawBuffer;
        public XRDataBuffer? IndirectOverflowFlagBuffer => _indirectOverflowFlagBuffer;
        public XRDataBuffer? TruncationFlagBuffer => _truncationFlagBuffer;
        public XRDataBuffer? StatsBuffer => _statsBuffer;
        public uint CommandCapacity => _lastMaxCommands == 0u ? GPUScene.MinCommandCount : _lastMaxCommands;

        // Returns the current scene material map (ID -> XRMaterial)
        public IReadOnlyDictionary<uint, XRMaterial> GetMaterialMap(GPUScene scene)
            => scene.MaterialMap;

        private void SetCurrentBatches(IReadOnlyList<HybridRenderingManager.DrawBatch>? batches)
            => CurrentBatches = batches;
    }
}
