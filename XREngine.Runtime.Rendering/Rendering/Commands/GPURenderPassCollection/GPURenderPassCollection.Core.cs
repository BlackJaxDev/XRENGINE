using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Vulkan;
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
        private const uint GpuClearUIntsLocalSizeX = 256u;
        private const uint MaterialScatterLocalSizeX = 64u;
        private const uint MeshletExpansionLocalSizeX = 256u;
        private const uint MaxMeshletTaskCapacityHardLimit = 1_048_576u;
        
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

            /// <summary>
            /// Enables compact hot-command buffers for culling/occlusion/indirect stages.
            /// Falls back to compact 20-lane command reads when disabled.
            /// </summary>
            public bool EnableHotCommandLayout { get; set; } = true;
        }

        private static readonly IndirectDebugSettings _indirectDebug = new();
        public static IndirectDebugSettings IndirectDebug => _indirectDebug;

        private bool _passPolicySnapshotValid;
        private bool _passDebugLoggingEnabled;
        private bool _passValidationLoggingEnabled;
        private bool _passDisableCpuReadbackCount;
        private bool _passEnableCpuBatching;
        private bool _passCpuFallbackRequested;
        private bool _passProbeSourceCommands;
        private bool _passLogCountBufferWrites;
        private bool _passValidateCopyCommandAtomicBounds;
        private bool _passAllowCpuFallback;
        private bool _passDiagnosticReadbacksEnabled;
        private bool _passEnableZeroReadbackMaterialScatter;
        private EZeroReadbackMaterialDrawPath _passZeroReadbackMaterialDrawPath;
        private int _zeroReadbackProgramPendingCountThisFrame;
        private int _forbiddenFallbackLogBudget = 8;

        public static void ConfigureIndirectDebug(Action<IndirectDebugSettings> configure)
        {
            if (configure is null)
                return;

            lock (_indirectDebug)
                configure(_indirectDebug);
        }

        internal static bool ShouldForceCpuIndirectBuild(EMeshSubmissionStrategy strategy)
            => ResolveForceCpuIndirectBuild(
                strategy,
                EffectiveSettingsEnvOverrides.ForceCpuIndirectBuild,
                IndirectDebug.ForceCpuIndirectBuild);

        internal static bool ResolveForceCpuIndirectBuild(
            EMeshSubmissionStrategy strategy,
            string? environmentValue,
            bool configuredDebugValue)
        {
            if (strategy != EMeshSubmissionStrategy.GpuIndirectInstrumented)
                return false;

            if (configuredDebugValue)
                return true;

            ReadOnlySpan<char> value = environmentValue.AsSpan().Trim();
            return value.SequenceEqual("1") || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsShippingHotOnlyProfile()
            => VulkanFeatureProfile.IsActive && VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.ShippingFast;

        private static bool IsHotCommandLayoutEnabled()
            => IndirectDebug.EnableHotCommandLayout || IsShippingHotOnlyProfile();

        private static bool IsHotCommandLayoutRequired()
            => IsShippingHotOnlyProfile();

        private bool IsCpuReadbackCountDisabledForPass()
            => _passPolicySnapshotValid
                ? _passDisableCpuReadbackCount
                : (MeshSubmissionStrategy.IsGpuZeroReadbackStrategy() || IndirectDebug.DisableCpuReadbackCount);

        private bool IsCpuBatchingEnabledForPass()
            => _passPolicySnapshotValid
                ? _passEnableCpuBatching
                : (IsInstrumentedGpuStrategy(MeshSubmissionStrategy) && IndirectDebug.EnableCpuBatching);

        private bool IsSourceCommandProbeEnabledForPass()
            => _passPolicySnapshotValid
                ? _passProbeSourceCommands
                : (IsInstrumentedGpuStrategy(MeshSubmissionStrategy) && IndirectDebug.ProbeSourceCommandsBeforeCopy);

        private bool IsCountBufferWriteLoggingEnabledForPass()
            => _passPolicySnapshotValid
                ? _passLogCountBufferWrites
                : (IsInstrumentedGpuStrategy(MeshSubmissionStrategy) && IndirectDebug.LogCountBufferWrites && RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging);

        private bool IsCopyBoundsValidationEnabledForPass()
            => _passPolicySnapshotValid
                ? _passValidateCopyCommandAtomicBounds
                : IndirectDebug.ValidateCopyCommandAtomicBounds;

        private bool ShouldCaptureDiagnosticReadbacksForPass()
            => _passPolicySnapshotValid
                ? _passDiagnosticReadbacksEnabled
                : (IsInstrumentedGpuStrategy(MeshSubmissionStrategy) &&
                    (RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging ||
                     RuntimeEngine.EffectiveSettings.EnableGpuIndirectValidationLogging ||
                     !IndirectDebug.DisableCpuReadbackCount ||
                     IndirectDebug.EnableCpuBatching));

        private bool IsDebugLoggingEnabledForPass()
            => _passPolicySnapshotValid
                ? _passDebugLoggingEnabled
                : RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

        public bool ZeroReadbackProgramPendingThisFrame
            => _zeroReadbackProgramPendingCountThisFrame > 0;

        public int ZeroReadbackProgramPendingCountThisFrame
            => _zeroReadbackProgramPendingCountThisFrame;

        internal void ResetZeroReadbackProgramPendingState()
            => _zeroReadbackProgramPendingCountThisFrame = 0;

        internal void RecordZeroReadbackProgramPending()
            => _zeroReadbackProgramPendingCountThisFrame++;

        private bool IsValidationLoggingEnabledForPass()
            => _passPolicySnapshotValid
                ? _passValidationLoggingEnabled
                : RuntimeEngine.EffectiveSettings.EnableGpuIndirectValidationLogging;

        private void CapturePassPolicySnapshot()
        {
            EMeshSubmissionStrategy strategy = MeshSubmissionStrategy;
            bool instrumented = IsInstrumentedGpuStrategy(strategy);
            bool zeroReadback = strategy.IsGpuZeroReadbackStrategy();
            bool meshlet = strategy.IsAnyMeshletStrategy();

            _passDebugLoggingEnabled = instrumented && RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;
            _passValidationLoggingEnabled = instrumented && RuntimeEngine.EffectiveSettings.EnableGpuIndirectValidationLogging;
            _passZeroReadbackMaterialDrawPath = RuntimeEngine.EffectiveSettings.ZeroReadbackMaterialDrawPath;
            if (meshlet &&
                _passZeroReadbackMaterialDrawPath is not EZeroReadbackMaterialDrawPath.MaterialTable and
                    not EZeroReadbackMaterialDrawPath.BindlessMaterialTable)
            {
                _passZeroReadbackMaterialDrawPath = EZeroReadbackMaterialDrawPath.MaterialTable;
            }

            // Instrumented GPU indirect needs the same material-tier dispatch as zero-readback
            // so visual diagnostics bind the correct shader/textures per material. CPU readback
            // policy stays controlled by the instrumentation switches below.
            _passEnableZeroReadbackMaterialScatter = zeroReadback || instrumented || meshlet;
            EnableZeroReadbackMaterialScatter = _passEnableZeroReadbackMaterialScatter;

            // Zero-readback and non-instrumented strategies must not map GPU count buffers on the render path.
            // Instrumented exists specifically for bring-up and validation, so keep counts CPU-readable there.
            _passDisableCpuReadbackCount = !instrumented;
            _passEnableCpuBatching = instrumented && IndirectDebug.EnableCpuBatching;
            _passProbeSourceCommands = instrumented && IndirectDebug.ProbeSourceCommandsBeforeCopy;
            _passLogCountBufferWrites = instrumented && IndirectDebug.LogCountBufferWrites && _passDebugLoggingEnabled;
            _passValidateCopyCommandAtomicBounds = IndirectDebug.ValidateCopyCommandAtomicBounds;

            bool fallbackRequested = (RuntimeEngine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
                || (_passDebugLoggingEnabled && RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback);
            _passCpuFallbackRequested = fallbackRequested;
            _passAllowCpuFallback = instrumented
                && !VulkanFeatureProfile.EnforceStrictNoFallbacks
                && fallbackRequested
                && (!VulkanFeatureProfile.IsActive || VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics);

            _passDiagnosticReadbacksEnabled = instrumented
                && (_passDebugLoggingEnabled
                    || _passValidationLoggingEnabled
                    || !_passDisableCpuReadbackCount
                    || _passEnableCpuBatching);

            _passPolicySnapshotValid = true;

            AssertZeroReadbackProductionInvariantsForPass(strategy);
        }

        private static bool IsInstrumentedGpuStrategy(EMeshSubmissionStrategy strategy)
            => strategy == EMeshSubmissionStrategy.GpuIndirectInstrumented ||
               strategy.IsInstrumentedMeshletStrategy();

        // C-GPU-2: per-pass DEBUG assertion that the diagnostic IndirectDebug switches that would
        // defeat the zero-readback contract are all OFF when the pass is configured for
        // GpuIndirectZeroReadback. Compiles out in Release.
        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertZeroReadbackProductionInvariantsForPass(EMeshSubmissionStrategy strategy)
        {
            if (!strategy.IsGpuZeroReadbackStrategy())
                return;

            var d = IndirectDebug;
            System.Diagnostics.Debug.Assert(!d.DisableCountDrawPath,
                $"[C-GPU-2] CapturePassPolicySnapshot: IndirectDebug.DisableCountDrawPath=true under {strategy}. " +
                "Diagnostic switch must be OFF in production.");
            System.Diagnostics.Debug.Assert(!d.ForceCpuFallbackCount,
                $"[C-GPU-2] CapturePassPolicySnapshot: IndirectDebug.ForceCpuFallbackCount=true under {strategy}.");
            System.Diagnostics.Debug.Assert(!d.ForceCpuIndirectBuild,
                $"[C-GPU-2] CapturePassPolicySnapshot: IndirectDebug.ForceCpuIndirectBuild=true under {strategy}.");
            System.Diagnostics.Debug.Assert(d.DisableCpuReadbackCount,
                $"[C-GPU-2] CapturePassPolicySnapshot: IndirectDebug.DisableCpuReadbackCount=false under {strategy}. " +
                "Zero-readback must suppress GPU count-buffer map/unmap.");
            System.Diagnostics.Debug.Assert(!d.EnableCpuBatching,
                $"[C-GPU-2] CapturePassPolicySnapshot: IndirectDebug.EnableCpuBatching=true under {strategy}.");
        }

        private void ClearPassPolicySnapshot()
            => _passPolicySnapshotValid = false;

        private void RecordForbiddenFallback(string reason)
        {
            RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(1);
            if (_forbiddenFallbackLogBudget <= 0)
                return;

            if (Interlocked.Decrement(ref _forbiddenFallbackLogBudget) < 0)
                return;

            Debug.MeshesWarning($"{FormatDebugPrefix("Validation")} Forbidden fallback blocked in profile {VulkanFeatureProfile.ActiveProfile}: {reason}");
        }

        private static uint ComputeBoundedDoublingCapacity(uint currentCapacity, uint minimumRequired)
        {
            ulong current = Math.Max((ulong)currentCapacity, 1UL);
            ulong doubled = current * 2UL;
            ulong required = Math.Max((ulong)minimumRequired, doubled);
            ulong bounded = Math.Min(required, int.MaxValue);
            return (uint)bounded;
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
        
        private IRuntimeRenderPipelineDebugContext? _ownerPipeline;

        [System.Diagnostics.Conditional("DEBUG")]
        private void Dbg(string msg, string cat = "General")
        {
            if (!_verbose)
                return;

            if (!(RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging))
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
            IRuntimeRenderPipelineDebugContext? pipeline = _ownerPipeline ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline as IRuntimeRenderPipelineDebugContext;
            string descriptor = pipeline?.DebugDescriptor ?? "Pipeline=<none>";
            return $"[GPURenderPass/{cat}] {descriptor} Pass={RenderPass}";
        }

        internal void SetDebugContext(IRuntimeRenderPipelineDebugContext? pipeline, int passIndex)
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
        private XRDataBuffer? _sourceHotCommandBuffer;     // Hot source commands (16 uints)
        private XRDataBuffer? _culledHotCommandBuffer;     // Hot compacted visible commands
        private XRDataBuffer? _occlusionCulledHotBuffer;   // Hot ping-pong output for occlusion refine
        private XRDataBuffer? _visibleMeshletTaskBuffer;   // GpuMeshletTaskRecord stream
        private XRDataBuffer? _visibleMeshletTaskCountBuffer;
        private XRDataBuffer? _meshletDispatchIndirectBuffer;
        private XRDataBuffer? _meshletDispatchCountBuffer;
        private XRDataBuffer? _meshletExpansionOverflowFlagBuffer;
        private bool _sourceCommandsUseHotLayout;
        private bool _culledHotCommandsValid;
        private bool _culledCommandsUseHotLayout;
        private bool _meshletExpansionPreparedThisFrame;

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

        private static XRDataBuffer MakeHotCommandBuffer(string name, uint capacity)
        {
            XRDataBuffer buffer = new(
                name,
                EBufferTarget.ShaderStorageBuffer,
                capacity,
                EComponentType.UInt,
                GPUScene.CommandHotUIntCount,
                false,
                false)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                Resizable = false,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read,
                RangeFlags = EBufferMapRangeFlags.Read,
            };
            buffer.Generate();
            return buffer;
        }

        private uint ComputeMeshletTaskCapacity(uint commandCapacity)
        {
            uint visibleCommandCapacity = Math.Max(commandCapacity, 1u);
            ulong taskCapacity = (ulong)visibleCommandCapacity * Math.Max(_meshletTaskCapacityPerVisibleCommand, 1u) * 2ul;
            return (uint)Math.Min(Math.Max(taskCapacity, 1ul), MaxMeshletTaskCapacityHardLimit);
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
        public XRRenderProgram? _materialScatterComputeShader;
        public XRRenderProgram? _buildActiveMaterialBucketsComputeShader;
        //public XRRenderProgram? RadixIndexSortComputeShader;
        private XRRenderProgram? _lodSelectComputeShader;
        public XRRenderProgram? _indirectRenderTaskShader;
        public XRRenderProgram? _buildHotCommandsProgram;
        public XRRenderProgram? _resetCountersComputeShader;
        private XRRenderProgram? _expandMeshletsComputeShader;
        private XRRenderProgram? _clearUIntsComputeShader;
        public XRRenderProgram? _debugDrawProgram;
        private XRRenderProgram? _copyCommandsProgram; // new: passthrough copy
        private XRRenderProgram? _bvhFrustumCullProgram; // BVH-accelerated frustum culling
        private XRRenderProgram[] _gpuPreparationPrograms = [];
        private bool _gpuProgramsReady;

        /// <summary>
        /// True when this pass deliberately deferred GPU work while its fixed compute-program set
        /// was still compiling in the background.
        /// </summary>
        public bool GpuProgramsPendingThisFrame { get; private set; }

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
        private uint _visibleCommandUpperBound = 0;
        private bool _visibleCommandUpperBoundValid;
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

        private uint _maskedVisibleCommandCount = 0;
        public uint MaskedVisibleCommandCount
        {
            get => _maskedVisibleCommandCount;
            private set => SetField(ref _maskedVisibleCommandCount, value);
        }

        private uint _approximateTransparentVisibleCommandCount = 0;
        public uint ApproximateTransparentVisibleCommandCount
        {
            get => _approximateTransparentVisibleCommandCount;
            private set => SetField(ref _approximateTransparentVisibleCommandCount, value);
        }

        private uint _exactTransparentVisibleCommandCount = 0;
        public uint ExactTransparentVisibleCommandCount
        {
            get => _exactTransparentVisibleCommandCount;
            private set => SetField(ref _exactTransparentVisibleCommandCount, value);
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
        private XRDataBuffer? _truncationFlagBuffer = null;
        private XRDataBuffer? _culledCountBuffer;
        private XRDataBuffer? _drawCountBuffer;
        private XRDataBuffer? _cullingOverflowFlagBuffer;
        private XRDataBuffer? _indirectOverflowFlagBuffer;
        private XRDataBuffer? _overflowDebugBuffer;
        private XRDataBuffer? _gpuBatchRangeBuffer;
        private XRDataBuffer? _gpuBatchCountBuffer;
        private XRDataBuffer? _materialSlotLookupBuffer;
        private XRDataBuffer? _materialTierIndirectDrawBuffer;
        private XRDataBuffer? _materialTierDrawCountBuffer;
        private XRDataBuffer? _materialTierActiveBucketBuffer;
        private XRDataBuffer? _materialTierActiveBucketCountBuffer;
        private XRDataBuffer? _instanceTransformBuffer;
        private XRDataBuffer? _instanceSourceIndexBuffer;
        private XRDataBuffer? _materialAggregationBuffer;
        private XRDataBuffer? _maskedVisibleIndexBuffer;
        private XRDataBuffer? _approximateTransparentVisibleIndexBuffer;
        private XRDataBuffer? _exactTransparentVisibleIndexBuffer;
        private XRDataBuffer? _transparencyDomainCountBuffer;
        private bool _gpuBatchingPreparedThisFrame;
        private bool _zeroReadbackMaterialScatterPreparedThisFrame;
        private bool _zeroReadbackActiveBucketListPreparedThisFrame;
        private readonly List<uint> _materialSlotIds = [];
        private readonly List<uint> _materialSlotSortScratch = [];
        private XRDataBuffer? _materialSlotLookupUploadedBuffer;
        private ulong _materialSlotLookupSignature;
        private uint _materialSlotLookupUploadedElementCount;
        private XRDataBuffer? _materialAggregationUploadedBuffer;
        private ulong _materialAggregationSignature;
        private uint _materialAggregationUploadedElementCount;
        private uint _materialTierBucketCount;
        private uint _maxDrawsPerMaterialTier;
        public bool EnableGpuDrivenBatching { get; set; } = true;
        public bool EnableGpuDrivenInstancing { get; set; } = true;
        public bool EnableZeroReadbackMaterialScatter { get; set; } = false;
        public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath => _passPolicySnapshotValid
            ? _passZeroReadbackMaterialDrawPath
            : RuntimeEngine.EffectiveSettings.ZeroReadbackMaterialDrawPath;
        public EMeshSubmissionStrategy MeshSubmissionStrategy { get; set; } = EMeshSubmissionStrategy.GpuIndirectInstrumented;
        public uint LodTransitionFrameCount { get; set; } = 8u;
        private uint _meshletTaskCapacityPerVisibleCommand = 128u;
        public uint MeshletTaskCapacityPerVisibleCommand
        {
            get => _meshletTaskCapacityPerVisibleCommand;
            set => SetField(ref _meshletTaskCapacityPerVisibleCommand, Math.Max(value, 1u));
        }

        /// <summary>
        /// If true, the material ID is included in the sorting key to reduce overdraw.
        /// </summary>
        //public bool UseMaterialBatchKey { get; set; } = false;

        private XRDataBuffer? _materialIDsBuffer;
        private XRRenderProgram? _classifyTransparencyComputeShader;

        private GPUMaterialTable? _materialTable;
        public XRDataBuffer? MaterialTableBuffer => _materialTable?.Buffer;
        public XRDataBuffer? MaterialTextureHandleBuffer => _materialTable?.TextureHandleBuffer;
        private MaterialBindingResolverResult _lastMaterialBindingResolverResult =
            MaterialBindingResolverResult.PerMaterial("Material binding resolver has not run.");
        public MaterialBindingResolverResult LastMaterialBindingResolverResult => _lastMaterialBindingResolverResult;
        public MaterialBindingLayout? MaterialBindingLayout
            => MaterialBindingLayouts.TryGetDefaultForRenderPass(RenderPass, out MaterialBindingLayout layout)
                ? layout
                : null;

        // Hybrid rendering manager (meshlets vs traditional indirect)
        private readonly HybridRenderingManager _renderManager = new() { UseMeshletPipeline = false };
        public bool UseMeshletPipeline
        {
            get => _renderManager.UseMeshletPipeline;
            set => _renderManager.UseMeshletPipeline = value;
        }

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

            if (IsCpuReadbackCountDisabledForPass() && !IndirectDebug.ForceCpuFallbackCount)
                return;

            drawCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            instanceCount = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountInstanceIndex);
            overflowMarker = ReadUIntAt(_culledCountBuffer, GPUScene.VisibleCountOverflowIndex);
        }

        public void SetMaterialTable(GPUMaterialTable table) => _materialTable = table;

        public bool TryGetGeneratedMaterialTableDispatchLayout(int renderPass, out MaterialBindingLayout layout)
            => MaterialBindingLayouts.TryGetGeneratedMaterialTableDispatchLayout(renderPass, out layout);

        public void RecordMaterialBindingResolverResult(MaterialBindingResolverResult result)
            => _lastMaterialBindingResolverResult = result;
    }

    public sealed partial class GPURenderPassCollection : IRuntimeGpuRenderPassHost
    {
        // GPU-generated sort keys (uint4 per visible command): packed pass/pipeline/state, material, mesh, source index.
        private XRDataBuffer? _keyIndexBufferA;
        private XRDataBuffer? _keyIndexScratchBuffer;

        // Expose batches for the current pass for HybridRenderingManager
        public IReadOnlyList<HybridRenderingManager.DrawBatch>? CurrentBatches { get; private set; }
        public XRDataBuffer? InstanceTransformBuffer => _instanceTransformBuffer;
        public XRDataBuffer? InstanceSourceIndexBuffer => _instanceSourceIndexBuffer;
        public bool GpuBatchingPreparedThisFrame => _gpuBatchingPreparedThisFrame;

        // Simple passthrough for count/flag/stat buffer exposure
        public XRDataBuffer? CulledCountBuffer => _culledCountBuffer;
        public XRDataBuffer? CulledHotCommandBuffer => _culledCommandsUseHotLayout ? _culledHotCommandBuffer : null;
        public bool CulledCommandsUseHotLayout => _culledCommandsUseHotLayout;
        public XRDataBuffer? DrawCountBuffer => _drawCountBuffer;
        public XRDataBuffer? IndirectDrawBuffer => _indirectDrawBuffer;
        public XRDataBuffer? MaterialTierIndirectDrawBuffer => _materialTierIndirectDrawBuffer;
        public XRDataBuffer? MaterialTierDrawCountBuffer => _materialTierDrawCountBuffer;
        public XRDataBuffer? MaterialSlotLookupBuffer => _materialSlotLookupBuffer;
        public XRDataBuffer? MaterialTierActiveBucketBuffer => _materialTierActiveBucketBuffer;
        public XRDataBuffer? MaterialTierActiveBucketCountBuffer => _materialTierActiveBucketCountBuffer;
        public XRDataBuffer? IndirectOverflowFlagBuffer => _indirectOverflowFlagBuffer;
        public XRDataBuffer? VisibleMeshletTaskBuffer => _visibleMeshletTaskBuffer;
        public XRDataBuffer? VisibleMeshletTaskCountBuffer => _visibleMeshletTaskCountBuffer;
        public XRDataBuffer? MeshletDispatchIndirectBuffer => _meshletDispatchIndirectBuffer;
        public XRDataBuffer? MeshletDispatchCountBuffer => _meshletDispatchCountBuffer;
        public XRDataBuffer? MeshletExpansionOverflowFlagBuffer => _meshletExpansionOverflowFlagBuffer;
        public XRDataBuffer? TruncationFlagBuffer => _truncationFlagBuffer;
        public XRDataBuffer? StatsBuffer => _statsBuffer;
        public XRDataBuffer? MaskedVisibleIndexBuffer => _maskedVisibleIndexBuffer;
        public XRDataBuffer? ApproximateTransparentVisibleIndexBuffer => _approximateTransparentVisibleIndexBuffer;
        public XRDataBuffer? ExactTransparentVisibleIndexBuffer => _exactTransparentVisibleIndexBuffer;
        public XRDataBuffer? TransparencyDomainCountBuffer => _transparencyDomainCountBuffer;
        public IReadOnlyList<uint> MaterialSlotIds => _materialSlotIds;
        public uint MaterialTierBucketCount => _materialTierBucketCount;
        public uint MaxDrawsPerMaterialTier => _maxDrawsPerMaterialTier;
        public bool ZeroReadbackMaterialScatterPreparedThisFrame => _zeroReadbackMaterialScatterPreparedThisFrame;
        public bool ZeroReadbackActiveBucketListPreparedThisFrame => _zeroReadbackActiveBucketListPreparedThisFrame;
        public bool MeshletExpansionPreparedThisFrame => _meshletExpansionPreparedThisFrame;
        public uint CommandCapacity => _lastMaxCommands == 0u ? GPUScene.MinCommandCount : _lastMaxCommands;
        public uint MaxIndirectDrawCapacity => Math.Max(CommandCapacity * 2u, 1u);
        public uint MaxVisibleMeshletTaskCapacity => ComputeMeshletTaskCapacity(CommandCapacity);

        public bool TryGetMeshletExpansionInputs(GPUScene scene, out GpuMeshletExpansionInputs inputs)
        {
            inputs = default;
            if (scene is null || _culledSceneToRenderBuffer is null || _culledCountBuffer is null)
                return false;

            XRDataBuffer? visibleHotCommandBuffer = _culledCommandsUseHotLayout ? _culledHotCommandBuffer : null;
            uint visibleCommandUpperBound = _visibleCommandUpperBoundValid
                ? Math.Min(_visibleCommandUpperBound, CommandCapacity)
                : Math.Min(VisibleCommandCount, CommandCapacity);

            inputs = new GpuMeshletExpansionInputs(
                _culledSceneToRenderBuffer,
                visibleHotCommandBuffer,
                visibleHotCommandBuffer is not null,
                _culledCountBuffer,
                scene.DrawMetadataBuffer,
                scene.MeshDataBuffer,
                scene.MeshletRangeBuffer,
                scene.MeshletDescriptorBuffer,
                scene.MeshletVertexIndexBuffer,
                scene.MeshletTriangleIndexBuffer,
                scene.LodTransitionBuffer,
                visibleCommandUpperBound);
            return true;
        }

        internal void CaptureMeshletInstrumentationAfterDispatch(TimeSpan dispatchElapsed)
        {
            if (!MeshSubmissionStrategy.IsInstrumentedMeshletStrategy() ||
                !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                return;
            }

            uint visibleMeshlets = VisibleMeshletTaskCountBuffer is not null
                ? ReadUIntAt(VisibleMeshletTaskCountBuffer, 0u)
                : 0u;
            uint dispatchedMeshlets = MeshletDispatchCountBuffer is not null
                ? ReadUIntAt(MeshletDispatchCountBuffer, 0u)
                : 0u;
            uint overflowCount = MeshletExpansionOverflowFlagBuffer is not null
                ? ReadUIntAt(MeshletExpansionOverflowFlagBuffer, 0u)
                : 0u;

            uint readbackBytes = 0u;
            if (VisibleMeshletTaskCountBuffer is not null)
                readbackBytes += sizeof(uint);
            if (MeshletDispatchCountBuffer is not null)
                readbackBytes += sizeof(uint);
            if (MeshletExpansionOverflowFlagBuffer is not null)
                readbackBytes += sizeof(uint);

            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletInstrumentation(
                visibleMeshlets,
                dispatchedMeshlets,
                overflowCount,
                dispatchElapsed,
                readbackBytes);
        }

        // Returns the current scene material map (ID -> XRMaterial)
        public IReadOnlyDictionary<uint, XRMaterial> GetMaterialMap(GPUScene scene)
            => scene.MaterialMap;

        private void SetCurrentBatches(IReadOnlyList<HybridRenderingManager.DrawBatch>? batches)
            => CurrentBatches = batches;
    }
}
