using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// GPU-based render pass orchestration (core fields & properties).
    /// See other partial class files for culling, sorting, indirect build, materials, and initialization logic.
    /// </summary>
    public sealed partial class GPURenderPassCollection : XRBase, IDisposable
    {
        private static readonly uint _indirectCommandStride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();
        private static readonly uint _indirectCommandComponentCount = _indirectCommandStride / sizeof(uint);

        // Verbose debug infra (shared across partials)
        private static volatile bool _verbose = true;

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
            public bool SkipIndirectTailClear { get; set; }

            /// <summary>
            /// Dumps the first few indirect commands and draw count whenever they change.
            /// </summary>
            public bool DumpIndirectArguments { get; set; }

            /// <summary>
            /// Logs writes into the shared draw-count buffer for visibility.
            /// </summary>
            public bool LogCountBufferWrites { get; set; }

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
        
        [System.Diagnostics.Conditional("DEBUG")]
        private static void Dbg(string msg, string cat = "General")
        {
            if (!_verbose)
                return;

            bool enabled;
            lock (_debugCategories)
                enabled = _debugCategories.Contains(cat) || _debugCategories.Contains("All");

            if (enabled)
                Debug.Out($"[GPURenderPass/{cat}] {msg}");
        }

        // Primary working buffers
        private XRDataBuffer? _sortedCommandBuffer;        // Sorted output buffer (some algorithms)
        //private XRDataBuffer? _histogramBuffer;            // Histogram for direct radix sort
        private XRDataBuffer? _indirectDrawBuffer;         // DrawElementsIndirectCommand array
        private XRDataBuffer? _culledSceneToRenderBuffer;  // Compacted visible commands

        // Synchronization & lifecycle
        private readonly Lock _lock = new();
        private bool _disposed = false;
        private bool _initialized = false;
        private uint _lastMaxCommands = 0;

        // Configuration flags
        //public bool UseSoA { get; set; } = false;  // Experimental Structure-of-Arrays path
        //public bool UseHiZ { get; set; } = false;  // HiZ acceleration (requires UseSoA)

        private int _renderPass = 0;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        /// <summary>
        /// Culled command buffer accessor (lazy alloc).
        /// </summary>
        public XRDataBuffer CulledSceneToRenderBuffer
            => _culledSceneToRenderBuffer ??= MakeCulledSceneToRenderBuffer(GPUScene.MinCommandCount);

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
        //public XRRenderProgram? _buildKeysComputeShader;
        //public XRRenderProgram? RadixIndexSortComputeShader;
        public XRRenderProgram? _indirectRenderTaskShader;
        public XRRenderProgram? _resetCountersComputeShader;
        public XRRenderProgram? _debugDrawProgram;
        private XRRenderProgram? _copyCommandsProgram; // new: passthrough copy

        // Renderer used to issue indirect multi-draw calls
        public XRMeshRenderer? _indirectRenderer;

        // Visible count read-back
        private uint _visibleCommandCount = 0;
        public uint VisibleCommandCount
        {
            get => _visibleCommandCount;
            private set => SetField(ref _visibleCommandCount, value);
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
        private XRRenderProgram? _gatherProgram;
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

        /// <summary>
        /// If true, the material ID is included in the sorting key to reduce overdraw.
        /// </summary>
        public bool UseMaterialBatchKey { get; set; } = false;

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

        public void SetMaterialTable(GPUMaterialTable table) => _materialTable = table;
    }

    public sealed partial class GPURenderPassCollection
    {
        // Sorting key/index buffer (optional). When present, pairs [key, index] are used
        // to traverse visible draws in a sort order to build contiguous material batches.
        private XRDataBuffer? _keyIndexBufferA; // [key, index] per visible command

        // Expose batches for the current pass for HybridRenderingManager
        public IReadOnlyList<HybridRenderingManager.DrawBatch>? CurrentBatches { get; private set; }

        // Simple passthrough for DrawCountBuffer exposure
        public XRDataBuffer? DrawCountBuffer => _drawCountBuffer;

        // Returns the current scene material map (ID -> XRMaterial)
        public IReadOnlyDictionary<uint, XRMaterial> GetMaterialMap(GPUScene scene)
            => scene.MaterialMap;

        private void SetCurrentBatches(IReadOnlyList<HybridRenderingManager.DrawBatch>? batches)
            => CurrentBatches = batches;
    }
}
