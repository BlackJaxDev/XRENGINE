using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Data.Lists.Unsafe;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using static XREngine.Rendering.GpuDispatchLogger;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Partial class handling indirect rendering and material batching for GPU render passes.
    /// </summary>
    public sealed partial class GPURenderPassCollection
    {
        #region Fields & Properties

        internal static Action? ResetCountersHook { get; set; }
        private int _resolveMaterialLogBudget = 16;
        private readonly HashSet<uint> _lastMaterialTableIds = [];
        private int _materialResidencyLogBudget = 12;
        
        /// <summary>
        /// When true, sorts commands by material ID on CPU to create contiguous batches.
        /// This reduces batch count at the cost of CPU overhead for sorting.
        /// </summary>
        public bool EnableCpuMaterialSort { get; set; } = false;

        private static XRMaterial? ResolveEffectiveGpuMaterial(XRMaterial? sourceMaterial, XRMaterial? overrideMaterial, bool useDepthNormalMaterialVariants)
        {
            if (!useDepthNormalMaterialVariants)
                return overrideMaterial ?? sourceMaterial;

            XRMaterial? variant = sourceMaterial?.DepthNormalPrePassVariant;
            if (variant is not null)
                return variant;

            return overrideMaterial ?? sourceMaterial;
        }

        #endregion

        #region Main Render Pipeline

        /// <summary>
        /// Renders this pass using indirect rendering fully on-GPU.
        /// </summary>
        public void Render(GPUScene scene)
        {
            using var renderTiming = BeginTiming("GPURenderPassCollection.Render");
            CapturePassPolicySnapshot();
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render begin (pass={0})", RenderPass);
            Dbg("Render begin", "Lifecycle");

            if (!TryInitializeRender(scene, out XRCamera? camera) || camera is null)
            {
                ClearPassPolicySnapshot();
                return;
            }

            _gpuBatchingPreparedThisFrame = false;
            _zeroReadbackMaterialScatterPreparedThisFrame = false;
            _zeroReadbackActiveBucketListPreparedThisFrame = false;
            ResetZeroReadbackProgramPendingState();
            Stopwatch resetStopwatch = Stopwatch.StartNew();
            ResetCounters();
            resetStopwatch.Stop();
            Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(
                Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Reset,
                resetStopwatch.Elapsed);

            Cull(scene, camera);
            SelectVisibleCommandLods(scene, camera);
            ClassifyTransparencyDomains(scene);

            // Phase 2: do not early-out based on CPU-visible counters.
            // The default submission path uses GPU-written count buffers; a 0 count naturally results in no draws.

            bool strictNoFallbacks = VulkanFeatureProfile.EnforceStrictNoFallbacks;
            bool cpuBatchingEnabled = IsCpuBatchingEnabledForPass();
            bool useCpuBatchFallback = !strictNoFallbacks && (!EnableGpuDrivenBatching || cpuBatchingEnabled);
            List<HybridRenderingManager.DrawBatch>? batches;
            TimeSpan indirectStageElapsed = TimeSpan.Zero;

            if (useCpuBatchFallback)
            {
                using (BeginTiming("PopulateMaterialIDs"))
                    PopulateMaterialIDs(scene);

                using (BeginTiming("BuildIndirectCommandBuffer"))
                {
                    Stopwatch indirectStopwatch = Stopwatch.StartNew();
                    BuildIndirectCommandBuffer(scene);
                    indirectStopwatch.Stop();
                    indirectStageElapsed += indirectStopwatch.Elapsed;
                }

                using var batchTiming = BeginTiming("BuildMaterialBatchesCpuFallback");
                batches = BuildMaterialBatches(scene);
                CurrentBatches = batches;
                _gpuBatchingPreparedThisFrame = false;
            }
            else
            {
                if (!EnableGpuDrivenBatching && strictNoFallbacks)
                    RecordForbiddenFallback("CPU material batch fallback requested while strict no-fallbacks is active.");

                using var batchTiming = BeginTiming("BuildGpuBatchesAndInstancing");
                Stopwatch indirectStopwatch = Stopwatch.StartNew();
                batches = BuildGpuBatchesAndInstancing(scene);
                indirectStopwatch.Stop();
                indirectStageElapsed += indirectStopwatch.Elapsed;
                CurrentBatches = batches;
                _gpuBatchingPreparedThisFrame = batches is not null;

                bool canSubmitGpuCountOnly =
                    IsCpuReadbackCountDisabledForPass() &&
                    _drawCountBuffer is not null &&
                    _indirectDrawBuffer is not null;

                if (!_zeroReadbackMaterialScatterPreparedThisFrame &&
                    (batches is null || batches.Count == 0) &&
                    !canSubmitGpuCountOnly)
                {
                    if (scene.TotalCommandCount > 0)
                    {
                        Debug.MeshesWarning($"{FormatDebugPrefix("Materials")} GPU batching produced no batch ranges. " +
                                         "Enable IndirectDebug.EnableCpuBatching for emergency fallback diagnostics.");
                    }
                    ClearPassPolicySnapshot();
                    return;
                }
            }

            if (indirectStageElapsed > TimeSpan.Zero)
            {
                Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(
                    Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Indirect,
                    indirectStageElapsed);
            }

            Log(LogCategory.Indirect, LogLevel.Info, "Indirect build complete - visible={0}", VisibleCommandCount);
            Dbg("Indirect build complete", "Indirect");

            if (batches is not null)
                Log(LogCategory.Materials, LogLevel.Info, "Material batches={0}, visible commands={1}", batches.Count, VisibleCommandCount);

            if (!PrepareMaterialTableAndValidateResidency(scene, batches))
            {
                Dbg("Render abort - material table residency validation failed", "Materials");
                return;
            }

            Stopwatch drawStopwatch = Stopwatch.StartNew();
            _renderManager.Render(this, camera, scene, _indirectDrawBuffer!, _indirectRenderer, RenderPass, _drawCountBuffer, batches);
            drawStopwatch.Stop();
            Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming(
                Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming.Draw,
                drawStopwatch.Elapsed);
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render submission done");
            Dbg("Render submission done", "Lifecycle");

            _useBufferAForRender = !_useBufferAForRender;

            QueueAsyncGpuTriangleStatsReadback();
            PostRenderDiagnostics(scene);
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render end");
            Dbg("Render end", "Lifecycle");
            ClearPassPolicySnapshot();
        }

        /// <summary>
        /// Validates prerequisites and retrieves the camera for rendering.
        /// </summary>
        private bool TryInitializeRender(GPUScene scene, out XRCamera? camera)
        {
            PreRenderInitialize(scene);
            camera = null;

            if (_indirectDrawBuffer is null)
            {
                Dbg("Render abort - draw buffer null", "Lifecycle");
                return false;
            }

            if (_indirectRenderTaskShader is null && _buildGpuBatchesComputeShader is null)
            {
                Dbg("Render abort - indirect/batching shaders unavailable", "Lifecycle");
                return false;
            }

            camera = Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingCamera
                ?? Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.SceneCamera;
            if (camera is null)
            {
                Dbg("Render abort - no camera", "Lifecycle");
                return false;
            }

            if (Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingScene is VisualScene3D visualScene)
                visualScene.PrepareGpuCulling();

            return true;
        }

        #endregion

        #region Counter Management

        private void ResetCounters()
        {
            ResetVisibleCounters();

            if (_resetCountersComputeShader is null || _culledCountBuffer is null || _drawCountBuffer is null || _cullCountScratchBuffer is null)
                return;

            Dbg("Reset counters dispatch", "Lifecycle");

            BindStorageBuffer(_resetCountersComputeShader, _culledCountBuffer, 0);
            BindStorageBuffer(_resetCountersComputeShader, _drawCountBuffer, 1);
            if (_cullingOverflowFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 2);
            if (_indirectOverflowFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_indirectOverflowFlagBuffer, 3);
            if (_truncationFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_truncationFlagBuffer, 4);
            BindStorageBuffer(_resetCountersComputeShader, _cullCountScratchBuffer, 6);
            if (_statsBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_statsBuffer, 8);
            if (_gpuBatchCountBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_gpuBatchCountBuffer, 9);

            _resetCountersComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            ResetCountersHook?.Invoke();
            ResetPerViewDrawCounts(_activeViewCount);

            if (_occlusionOverflowFlagBuffer is not null)
                WriteUInt(_occlusionOverflowFlagBuffer, 0u);
        }

        #endregion

        #region Indirect Command Building

        private void BuildIndirectCommandBuffer(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.BuildIndirectCommandBuffer");

            Dbg("BuildIndirect begin", "Indirect");

            if (_indirectRenderTaskShader is null || _indirectDrawBuffer is null)
            {
                Dbg("BuildIndirect abort - shaders or draw buffer null", "Indirect");
                return;
            }

            // Phase 2: avoid CPU readback of visible counters in the hot path.
            // Indirect compute shaders consume the GPU-written count buffer directly.
            UpdateVisibleCountersFromBuffer();
            BuildCulledHotCommandBuffer();

            if (IsHotCommandLayoutRequired() && !_culledCommandsUseHotLayout)
            {
                Dbg("BuildIndirect abort - ShippingFast requires hot command layout but hot culled buffer is unavailable", "Indirect");
                return;
            }

            BindIndirectShaderUniforms();
            BindIndirectShaderBuffers(scene);

            uint dispatchCommands = VisibleCommandCount;
            if (IsCpuReadbackCountDisabledForPass())
            {
                dispatchCommands = Math.Min(dispatchCommands, _indirectDrawBuffer!.ElementCount);
            }

            uint dispatchGroups = Math.Max(1, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _indirectRenderTaskShader.DispatchCompute(dispatchGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"Indirect dispatch groups={dispatchGroups} visible={VisibleCommandCount}", "Indirect");
        }

        private void SelectVisibleCommandLods(GPUScene scene, XRCamera camera)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.SelectVisibleCommandLods");

            if (_lodSelectComputeShader is null ||
                _culledSceneToRenderBuffer is null ||
                _culledCountBuffer is null ||
                !scene.HasLogicalMeshEntries)
            {
                return;
            }

            uint dispatchCommands = VisibleCommandCount;
            if (IsCpuReadbackCountDisabledForPass())
                dispatchCommands = Math.Min(dispatchCommands, _culledSceneToRenderBuffer.ElementCount);

            if (dispatchCommands == 0)
                return;

            _lodSelectComputeShader.Uniform("CameraPosition", camera.Transform?.RenderTranslation ?? Vector3.Zero);
            _lodSelectComputeShader.Uniform("InputCommandCount", (int)dispatchCommands);
            _lodSelectComputeShader.Uniform("TransitionFrameStep", 1.0f / Math.Max(LodTransitionFrameCount, 1u));

            _culledSceneToRenderBuffer.BindTo(_lodSelectComputeShader, 0);
            _culledCountBuffer.BindTo(_lodSelectComputeShader, 1);
            scene.LODTableBuffer.BindTo(_lodSelectComputeShader, 2);
            scene.LODRequestBuffer.BindTo(_lodSelectComputeShader, 3);
            scene.LodTransitionBuffer.BindTo(_lodSelectComputeShader, 4);

            uint dispatchGroups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(dispatchCommands).Item1);
            const EMemoryBarrierMask postLodBarrier = EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command;
            _lodSelectComputeShader.DispatchCompute(dispatchGroups, 1, 1, postLodBarrier);
            AbstractRenderer.Current?.MemoryBarrier(postLodBarrier);
            _culledHotCommandsValid = false;
        }

        private void BindIndirectShaderUniforms()
        {
            _indirectRenderTaskShader!.Uniform("CurrentRenderPass", RenderPass);
            _indirectRenderTaskShader.Uniform("MaxIndirectDraws", (int)_indirectDrawBuffer!.ElementCount);
            _indirectRenderTaskShader.Uniform("AtlasAll16Bit", 0);
            _indirectRenderTaskShader.Uniform("StatsEnabled", _statsBuffer is not null ? 1u : 0u);
            _indirectRenderTaskShader.Uniform("ActiveViewCount", (int)(_activeViewCount == 0u ? 1u : _activeViewCount));
            _indirectRenderTaskShader.Uniform("SourceViewId", (int)_indirectSourceViewId);
            _indirectRenderTaskShader.Uniform("UseHotCommands", _culledCommandsUseHotLayout ? 1 : 0);
        }

        private void BindIndirectShaderBuffers(GPUScene scene)
        {
            CulledSceneToRenderBuffer.BindTo(_indirectRenderTaskShader!, 0);
            _indirectDrawBuffer!.BindTo(_indirectRenderTaskShader!, 1);
            scene.MeshDataBuffer.BindTo(_indirectRenderTaskShader!, 2);
            _culledCountBuffer?.BindTo(_indirectRenderTaskShader!, 3);
            _drawCountBuffer?.BindTo(_indirectRenderTaskShader!, 4);
            _indirectOverflowFlagBuffer?.BindTo(_indirectRenderTaskShader!, 5);
            scene.LodTransitionBuffer.BindTo(_indirectRenderTaskShader!, 10);

            if (_truncationFlagBuffer is not null)
            {
                _truncationFlagBuffer.SetDataRawAtIndex(0, 0u);
                _truncationFlagBuffer.PushSubData();
                _truncationFlagBuffer.BindTo(_indirectRenderTaskShader!, 7);
            }

            _statsBuffer?.BindTo(_indirectRenderTaskShader!, 8);
            if (_culledCommandsUseHotLayout)
                _culledHotCommandBuffer?.BindTo(_indirectRenderTaskShader!, 9);
            BindViewSetBuffers(_indirectRenderTaskShader!);
        }

        private void BuildCulledHotCommandBuffer()
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.BuildCulledHotCommandBuffer");

            _culledCommandsUseHotLayout = false;

            if (!IsHotCommandLayoutEnabled() ||
                _buildHotCommandsProgram is null ||
                _culledHotCommandBuffer is null ||
                _culledSceneToRenderBuffer is null ||
                _culledCountBuffer is null)
                return;

            uint maxInput = _culledSceneToRenderBuffer.ElementCount;
            if (maxInput == 0u)
                return;

            uint inputCount = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), maxInput)
                : Math.Max(VisibleCommandCount, 1u);

            _buildHotCommandsProgram.Uniform("InputCount", (int)inputCount);
            _buildHotCommandsProgram.BindBuffer(_culledSceneToRenderBuffer, 0);
            _buildHotCommandsProgram.BindBuffer(_culledHotCommandBuffer, 1);

            uint groups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(inputCount).Item1);
            _buildHotCommandsProgram.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            _culledCommandsUseHotLayout = true;
        }

        private List<HybridRenderingManager.DrawBatch>? BuildGpuBatchesAndInstancing(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.BuildGpuBatchesAndInstancing");

            if (_buildKeysComputeShader is null ||
                _buildGpuBatchesComputeShader is null ||
                _keyIndexBufferA is null ||
                _gpuBatchRangeBuffer is null ||
                _gpuBatchCountBuffer is null ||
                _instanceTransformBuffer is null ||
                _instanceSourceIndexBuffer is null ||
                _materialAggregationBuffer is null ||
                _drawCountBuffer is null ||
                _indirectDrawBuffer is null ||
                _culledCountBuffer is null)
            {
                Dbg("GPU batching unavailable - missing shader/buffer dependencies.", "Materials");
                return null;
            }

            UpdateVisibleCountersFromBuffer();
            PopulateMaterialAggregationFlags(scene);
            DispatchBuildKeys();
            DispatchBuildGpuBatches(scene);
            if (EnableZeroReadbackMaterialScatter)
            {
                DispatchMaterialScatter(scene);
                _zeroReadbackMaterialScatterPreparedThisFrame = _materialTierIndirectDrawBuffer is not null &&
                    _materialTierDrawCountBuffer is not null &&
                    _materialSlotLookupBuffer is not null &&
                    _materialSlotIds.Count > 0;

                if (_zeroReadbackMaterialScatterPreparedThisFrame &&
                    RequiresActiveMaterialBucketList(ZeroReadbackMaterialDrawPath))
                {
                    DispatchBuildActiveMaterialBuckets();
                }
            }
            UpdateVisibleCountersFromBuffer();

            if (_zeroReadbackMaterialScatterPreparedThisFrame)
                return null;

            // When readback is disabled (shipping / zero-readback mode), skip batch readback entirely.
            // The draw submission consumes GPU count buffers rather than CPU material batch ranges.
            if (IsCpuReadbackCountDisabledForPass())
                return null;

            return ReadGpuBatchRanges();
        }

        private void DispatchMaterialScatter(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.DispatchMaterialScatter");

            if (_materialScatterComputeShader is null ||
                _keyIndexBufferA is null ||
                _culledCountBuffer is null)
            {
                return;
            }

            PopulateMaterialSlotLookup(scene);
            if (_materialSlotLookupBuffer is null ||
                _materialTierIndirectDrawBuffer is null ||
                _materialTierDrawCountBuffer is null ||
                _materialTierBucketCount == 0u ||
                _maxDrawsPerMaterialTier == 0u)
            {
                return;
            }

            if (!ResetMaterialScatterBuffersOnGpu())
                return;

            _materialScatterComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _materialScatterComputeShader.Uniform("MaxMaterialSlotLookup", (int)_materialSlotLookupBuffer.ElementCount);
            _materialScatterComputeShader.Uniform("MaxBucketCount", (int)_materialTierBucketCount);
            _materialScatterComputeShader.Uniform("MaxIndirectDrawsPerBucket", (int)_maxDrawsPerMaterialTier);

            CulledSceneToRenderBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterInputCommands);
            scene.MeshDataBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterMeshData);
            _culledCountBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterCulledCount);
            _keyIndexBufferA.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterSortKeys);
            _materialSlotLookupBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterMaterialSlotLookup);
            _materialTierIndirectDrawBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterIndirectDraws);
            _materialTierDrawCountBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterDrawCounts);
            _indirectOverflowFlagBuffer?.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterOverflow);
            scene.LodTransitionBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterLodTransitions);

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), _keyIndexBufferA.ElementCount)
                : Math.Max(VisibleCommandCount, 1u);
            uint groups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u), MaterialScatterLocalSizeX).Item1);
            _materialScatterComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }

        private static bool RequiresActiveMaterialBucketList(EZeroReadbackMaterialDrawPath path)
            => path is EZeroReadbackMaterialDrawPath.ActiveBucketList
                or EZeroReadbackMaterialDrawPath.MaterialTable
                or EZeroReadbackMaterialDrawPath.BindlessMaterialTable;

        private static ulong MixMaterialMapEntry(ulong materialId, ulong value)
        {
            unchecked
            {
                ulong mixed = materialId + 0x9E3779B97F4A7C15ul;
                mixed ^= value + 0xBF58476D1CE4E5B9ul + (mixed << 6) + (mixed >> 2);
                mixed ^= mixed >> 30;
                mixed *= 0xBF58476D1CE4E5B9ul;
                mixed ^= mixed >> 27;
                mixed *= 0x94D049BB133111EBul;
                mixed ^= mixed >> 31;
                return mixed;
            }
        }

        private static ulong CombineMaterialMapSignature(int materialCount, uint maxMaterialId, ulong entryXor, ulong entrySum)
        {
            unchecked
            {
                ulong signature = MixMaterialMapEntry((uint)materialCount, maxMaterialId);
                signature ^= entryXor;
                signature ^= entrySum * 1099511628211ul;
                return signature == 0ul ? 1ul : signature;
            }
        }

        private static ulong ComputeMaterialSlotLookupSignature(IReadOnlyDictionary<uint, XRMaterial> materialMap, out uint maxMaterialId)
        {
            unchecked
            {
                ulong entryXor = 0ul;
                ulong entrySum = 0ul;
                maxMaterialId = 0u;

                foreach (uint materialId in materialMap.Keys)
                {
                    if (materialId > maxMaterialId)
                        maxMaterialId = materialId;

                    ulong entry = MixMaterialMapEntry(materialId, 0ul);
                    entryXor ^= entry;
                    entrySum += entry;
                }

                return CombineMaterialMapSignature(materialMap.Count, maxMaterialId, entryXor, entrySum);
            }
        }

        private static ulong ComputeMaterialAggregationSignature(IReadOnlyDictionary<uint, XRMaterial> materialMap, out uint maxMaterialId)
        {
            unchecked
            {
                ulong entryXor = 0ul;
                ulong entrySum = 0ul;
                maxMaterialId = 0u;

                foreach (KeyValuePair<uint, XRMaterial> pair in materialMap)
                {
                    uint materialId = pair.Key;
                    if (materialId > maxMaterialId)
                        maxMaterialId = materialId;

                    ulong allow = MaterialSupportsGpuInstanceAggregation(pair.Value) ? 1ul : 0ul;
                    ulong entry = MixMaterialMapEntry(materialId, allow);
                    entryXor ^= entry;
                    entrySum += entry;
                }

                return CombineMaterialMapSignature(materialMap.Count, maxMaterialId, entryXor, entrySum);
            }
        }

        private void DispatchBuildActiveMaterialBuckets()
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.DispatchBuildActiveMaterialBuckets");

            _zeroReadbackActiveBucketListPreparedThisFrame = false;

            if (_buildActiveMaterialBucketsComputeShader is null ||
                _materialTierDrawCountBuffer is null ||
                _materialTierActiveBucketBuffer is null ||
                _materialTierActiveBucketCountBuffer is null ||
                _materialTierBucketCount == 0u)
            {
                return;
            }

            if (!ClearUIntBufferOnGpu(_materialTierActiveBucketCountBuffer, 1u, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command))
                return;

            _buildActiveMaterialBucketsComputeShader.Uniform("MaxBucketCount", (int)_materialTierBucketCount);
            _materialTierDrawCountBuffer.BindTo(_buildActiveMaterialBucketsComputeShader, GPUBatchingBindings.ActiveMaterialBucketDrawCounts);
            _materialTierActiveBucketBuffer.BindTo(_buildActiveMaterialBucketsComputeShader, GPUBatchingBindings.ActiveMaterialBucketIndices);
            _materialTierActiveBucketCountBuffer.BindTo(_buildActiveMaterialBucketsComputeShader, GPUBatchingBindings.ActiveMaterialBucketCount);

            uint groups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(_materialTierBucketCount, MaterialScatterLocalSizeX).Item1);
            _buildActiveMaterialBucketsComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            _zeroReadbackActiveBucketListPreparedThisFrame = true;
        }

        private void PopulateMaterialSlotLookup(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.PopulateMaterialSlotLookup");

            ulong signature = ComputeMaterialSlotLookupSignature(scene.MaterialMap, out uint maxMaterialId);

            EnsureMaterialScatterBuffers(maxMaterialId + 1u, CommandCapacity);
            if (_materialSlotLookupBuffer is null)
                return;

            if (ReferenceEquals(_materialSlotLookupUploadedBuffer, _materialSlotLookupBuffer) &&
                _materialSlotLookupSignature == signature &&
                _materialSlotLookupUploadedElementCount == _materialSlotLookupBuffer.ElementCount &&
                _materialSlotIds.Count == scene.MaterialMap.Count)
            {
                return;
            }

            _materialSlotIds.Clear();
            _materialSlotSortScratch.Clear();

            for (uint i = 0; i < _materialSlotLookupBuffer.ElementCount; ++i)
                _materialSlotLookupBuffer.SetDataRawAtIndex(i, GPUBatchingBindings.InvalidMaterialSlot);

            foreach (uint materialId in scene.MaterialMap.Keys)
                _materialSlotSortScratch.Add(materialId);
            _materialSlotSortScratch.Sort();

            for (int slotIndex = 0; slotIndex < _materialSlotSortScratch.Count; ++slotIndex)
            {
                uint materialId = _materialSlotSortScratch[slotIndex];
                _materialSlotLookupBuffer.SetDataRawAtIndex(materialId, (uint)slotIndex);
                _materialSlotIds.Add(materialId);
            }

            _materialSlotLookupBuffer.PushSubData();
            _materialSlotLookupUploadedBuffer = _materialSlotLookupBuffer;
            _materialSlotLookupSignature = signature;
            _materialSlotLookupUploadedElementCount = _materialSlotLookupBuffer.ElementCount;
        }

        private bool ResetMaterialScatterBuffersOnGpu()
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.ResetMaterialScatterBuffersOnGpu");

            if (_materialTierDrawCountBuffer is null || _materialTierIndirectDrawBuffer is null)
                return false;

            bool countsCleared = ClearUIntBufferOnGpu(
                _materialTierDrawCountBuffer,
                _materialTierDrawCountBuffer.ElementCount,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            ulong indirectUIntCount = (ulong)_materialTierIndirectDrawBuffer.ElementCount * _materialTierIndirectDrawBuffer.ComponentCount;
            bool commandsCleared = ClearUIntBufferOnGpu(
                _materialTierIndirectDrawBuffer,
                indirectUIntCount,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            return countsCleared && commandsCleared;
        }

        private bool ClearUIntBufferOnGpu(XRDataBuffer buffer, ulong uintCount, EMemoryBarrierMask barrierMask)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.ClearUIntBufferOnGpu");

            if (_clearUIntsComputeShader is null || uintCount == 0ul)
                return false;

            if (!_clearUIntsComputeShader.IsLinked)
            {
                _clearUIntsComputeShader.Link();
                if (!_clearUIntsComputeShader.IsLinked)
                    return false;
            }

            uint boundedCount = uintCount > int.MaxValue ? (uint)int.MaxValue : (uint)uintCount;
            _clearUIntsComputeShader.Uniform("ElementCount", (int)boundedCount);
            buffer.BindTo(_clearUIntsComputeShader, 0);

            (uint x, uint y, uint z) = XRRenderProgram.ComputeDispatch.ForCommands(boundedCount, GpuClearUIntsLocalSizeX);
            _clearUIntsComputeShader.DispatchCompute(x, y, z, barrierMask);
            AbstractRenderer.Current?.MemoryBarrier(barrierMask);
            return true;
        }

        private void ClassifyTransparencyDomains(GPUScene scene)
        {
            if (_classifyTransparencyComputeShader is null ||
                _transparencyDomainCountBuffer is null ||
                _maskedVisibleIndexBuffer is null ||
                _approximateTransparentVisibleIndexBuffer is null ||
                _exactTransparentVisibleIndexBuffer is null ||
                _culledCountBuffer is null ||
                scene.AllLoadedTransparencyMetadataBuffer is null)
            {
                MaskedVisibleCommandCount = 0u;
                ApproximateTransparentVisibleCommandCount = 0u;
                ExactTransparentVisibleCommandCount = 0u;
                Engine.Rendering.Stats.RecordGpuTransparencyDomainCounts(0, 0, 0, 0);
                return;
            }

            WriteUints(_transparencyDomainCountBuffer, 0u, 0u, 0u, 0u);

            _classifyTransparencyComputeShader.Uniform("MaxVisibleCommands", (int)CommandCapacity);
            CulledSceneToRenderBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyInputCommands);
            _culledCountBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyCulledCount);
            scene.AllLoadedTransparencyMetadataBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyMetadata);
            _maskedVisibleIndexBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyMaskedVisibleIndices);
            _approximateTransparentVisibleIndexBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyApproximateVisibleIndices);
            _exactTransparentVisibleIndexBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyExactVisibleIndices);
            _transparencyDomainCountBuffer.BindTo(_classifyTransparencyComputeShader, GPUTransparencyBindings.ClassifyDomainCounts);

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), CommandCapacity)
                : Math.Max(VisibleCommandCount, 1u);
            uint groups = Math.Max(1, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _classifyTransparencyComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            if (ShouldCaptureDiagnosticReadbacksForPass())
            {
                // Diagnostic path: read domain counts back to CPU for stats/logging.
                uint opaqueOrOtherCount = ReadUIntAt(_transparencyDomainCountBuffer, (uint)EGpuTransparencyDomain.OpaqueOrOther);
                MaskedVisibleCommandCount = ReadUIntAt(_transparencyDomainCountBuffer, (uint)EGpuTransparencyDomain.Masked);
                ApproximateTransparentVisibleCommandCount = ReadUIntAt(_transparencyDomainCountBuffer, (uint)EGpuTransparencyDomain.TransparentApproximate);
                ExactTransparentVisibleCommandCount = ReadUIntAt(_transparencyDomainCountBuffer, (uint)EGpuTransparencyDomain.TransparentExact);

                Engine.Rendering.Stats.RecordGpuTransparencyDomainCounts(
                    opaqueOrOtherCount,
                    MaskedVisibleCommandCount,
                    ApproximateTransparentVisibleCommandCount,
                    ExactTransparentVisibleCommandCount);
            }
            else
            {
                // Zero-readback path: GPU wrote counts into _transparencyDomainCountBuffer
                // but we don't read them back. CPU stats remain at 0 (unavailable).
                MaskedVisibleCommandCount = 0u;
                ApproximateTransparentVisibleCommandCount = 0u;
                ExactTransparentVisibleCommandCount = 0u;
            }
        }

        private void DispatchBuildKeys()
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.DispatchBuildKeys");

            if (_buildKeysComputeShader is null || _keyIndexBufferA is null || _culledCountBuffer is null)
                return;

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), _keyIndexBufferA.ElementCount)
                : Math.Max(VisibleCommandCount, 1u);

            _buildKeysComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _buildKeysComputeShader.Uniform("MaxSortKeys", (int)_keyIndexBufferA.ElementCount);
            _buildKeysComputeShader.Uniform("StateBitMask", 0x0FFFu);

            var sortDomain = GpuSortPolicy.ResolveSortDomain(RenderPass, Engine.Rendering.Settings.GpuSortDomainPolicy);
            var sortDirection = GpuSortPolicy.ResolveSortDirection(sortDomain);
            _buildKeysComputeShader.Uniform("SortDomain", (int)sortDomain);
            _buildKeysComputeShader.Uniform("SortDirection", (int)sortDirection);

            CulledSceneToRenderBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysInputCommands);
            _culledCountBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysCulledCount);
            _keyIndexBufferA.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysSortKeys);

            uint groups = Math.Max(1, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _buildKeysComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchBuildGpuBatches(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.DispatchBuildGpuBatches");

            if (_buildGpuBatchesComputeShader is null ||
                _keyIndexBufferA is null ||
                _keyIndexScratchBuffer is null ||
                _gpuBatchRangeBuffer is null ||
                _gpuBatchCountBuffer is null ||
                _instanceTransformBuffer is null ||
                _instanceSourceIndexBuffer is null ||
                _materialAggregationBuffer is null ||
                _indirectDrawBuffer is null ||
                _drawCountBuffer is null ||
                _culledCountBuffer is null)
            {
                return;
            }

            _buildGpuBatchesComputeShader.Uniform("MaxIndirectDraws", (int)_indirectDrawBuffer.ElementCount);
            _buildGpuBatchesComputeShader.Uniform("MaxBatchRanges", (int)_gpuBatchRangeBuffer.ElementCount);
            _buildGpuBatchesComputeShader.Uniform("MaxInstanceTransforms", (int)_instanceTransformBuffer.ElementCount);
            _buildGpuBatchesComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _buildGpuBatchesComputeShader.Uniform("EnableInstancingAggregation", EnableGpuDrivenInstancing ? 1u : 0u);
            _buildGpuBatchesComputeShader.Uniform("StatsEnabled", _statsBuffer is not null ? 1u : 0u);
            _buildGpuBatchesComputeShader.Uniform("RadixSortThreshold", 1024);

            CulledSceneToRenderBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesInputCommands);
            scene.MeshDataBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesMeshData);
            _culledCountBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesCulledCount);
            _keyIndexBufferA.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesSortKeys);
            _keyIndexScratchBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesSortScratch);
            _indirectDrawBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesIndirectDraws);
            _drawCountBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesDrawCount);
            _gpuBatchRangeBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesBatchRanges);
            _gpuBatchCountBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesBatchCount);
            _instanceTransformBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesInstanceTransforms);
            _instanceSourceIndexBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesInstanceSources);
            _materialAggregationBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesMaterialAggregation);
            _indirectOverflowFlagBuffer?.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesIndirectOverflow);
            _truncationFlagBuffer?.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesTruncation);
            _statsBuffer?.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesStats);
            scene.LodTransitionBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesLodTransitions);

            _buildGpuBatchesComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }

        private void PopulateMaterialAggregationFlags(GPUScene scene)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.PopulateMaterialAggregationFlags");

            ulong signature = ComputeMaterialAggregationSignature(scene.MaterialMap, out uint maxMaterialId);

            EnsureMaterialAggregationBuffer(maxMaterialId + 1u);
            if (_materialAggregationBuffer is null)
                return;

            if (ReferenceEquals(_materialAggregationUploadedBuffer, _materialAggregationBuffer) &&
                _materialAggregationSignature == signature &&
                _materialAggregationUploadedElementCount == _materialAggregationBuffer.ElementCount)
            {
                return;
            }

            for (uint i = 0; i < _materialAggregationBuffer.ElementCount; ++i)
                _materialAggregationBuffer.SetDataRawAtIndex(i, 1u);

            foreach (KeyValuePair<uint, XRMaterial> pair in scene.MaterialMap)
            {
                uint materialID = pair.Key;
                uint allow = MaterialSupportsGpuInstanceAggregation(pair.Value) ? 1u : 0u;
                if (materialID < _materialAggregationBuffer.ElementCount)
                    _materialAggregationBuffer.SetDataRawAtIndex(materialID, allow);
            }

            _materialAggregationBuffer.PushSubData();
            _materialAggregationUploadedBuffer = _materialAggregationBuffer;
            _materialAggregationSignature = signature;
            _materialAggregationUploadedElementCount = _materialAggregationBuffer.ElementCount;
        }

        private static bool MaterialSupportsGpuInstanceAggregation(XRMaterial? material)
        {
            if (material is null)
                return false;

            foreach (XRShader? shader in material.Shaders)
            {
                if (shader?.Type != EShaderType.Vertex)
                    continue;

                string? source = shader.Source?.Text;
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                bool isTextShader =
                    source.Contains("GlyphTransformsBuffer", StringComparison.Ordinal) &&
                    source.Contains("GlyphTexCoordsBuffer", StringComparison.Ordinal);

                if (isTextShader)
                    return false;
            }

            return true;
        }

        private List<HybridRenderingManager.DrawBatch>? ReadGpuBatchRanges()
        {
            if (_gpuBatchCountBuffer is null || _gpuBatchRangeBuffer is null)
                return null;

            uint batchCount = ReadUIntAt(_gpuBatchCountBuffer, 0u);
            if (batchCount == 0u)
                return null;

            batchCount = Math.Min(batchCount, _gpuBatchRangeBuffer.ElementCount);
            if (batchCount == 0u)
                return null;

            bool mappedHere = false;

            try
            {
                if (_gpuBatchRangeBuffer.ActivelyMapping.Count == 0)
                {
                    _gpuBatchRangeBuffer.StorageFlags |= EBufferMapStorageFlags.Read;
                    _gpuBatchRangeBuffer.RangeFlags |= EBufferMapRangeFlags.Read;
                    _gpuBatchRangeBuffer.MapBufferData();
                    mappedHere = true;
                    Engine.Rendering.Stats.RecordGpuBufferMapped();
                }

                VoidPtr mapped = _gpuBatchRangeBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
                if (!mapped.IsValid)
                    return null;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                Engine.Rendering.Stats.RecordGpuReadbackBytes((int)(batchCount * GPUBatchingLayout.BatchRangeStride));

                uint stride = _gpuBatchRangeBuffer.ElementSize;
                if (stride == 0)
                    stride = GPUBatchingLayout.BatchRangeStride;

                var batches = new List<HybridRenderingManager.DrawBatch>((int)batchCount);

                unsafe
                {
                    byte* basePtr = (byte*)mapped.Pointer;
                    for (uint i = 0; i < batchCount; ++i)
                    {
                        GPUBatchRangeEntry range = Unsafe.ReadUnaligned<GPUBatchRangeEntry>(basePtr + (int)(i * stride));
                        if (range.DrawCount == 0u)
                            continue;

                        batches.Add(new HybridRenderingManager.DrawBatch(range.DrawOffset, range.DrawCount, range.MaterialID));
                    }
                }

                return batches.Count == 0 ? null : batches;
            }
            finally
            {
                if (mappedHere)
                    _gpuBatchRangeBuffer.UnmapBufferData();
            }
        }

        public List<uint>? ReadActiveMaterialTierBuckets()
        {
            if (_materialTierActiveBucketBuffer is null ||
                _materialTierActiveBucketCountBuffer is null ||
                _materialTierBucketCount == 0u)
            {
                return null;
            }

            uint activeCount = ReadUIntAt(_materialTierActiveBucketCountBuffer, 0u);
            activeCount = Math.Min(activeCount, _materialTierActiveBucketBuffer.ElementCount);
            activeCount = Math.Min(activeCount, _materialTierBucketCount);
            if (activeCount == 0u)
                return null;

            if (MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
            {
                XREngine.Debug.RenderingWarningEvery(
                    $"RenderDispatch.ZeroReadbackActiveBucketReadback.{RenderPass}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDispatch] Zero-readback draw path {0} is reading back {1} active material buckets for pass {2}. Use FullBucketScan for strict no-readback diagnostics, or treat ActiveBucketList/MaterialTable as readback-assisted modes.",
                    ZeroReadbackMaterialDrawPath,
                    activeCount,
                    RenderPass);
            }

            bool mappedHere = false;

            try
            {
                if (_materialTierActiveBucketBuffer.ActivelyMapping.Count == 0)
                {
                    _materialTierActiveBucketBuffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read;
                    _materialTierActiveBucketBuffer.RangeFlags |= EBufferMapRangeFlags.Read;
                    _materialTierActiveBucketBuffer.MapBufferData();
                    mappedHere = true;
                    Engine.Rendering.Stats.RecordGpuBufferMapped();
                }

                VoidPtr mapped = _materialTierActiveBucketBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
                if (!mapped.IsValid)
                    return null;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                Engine.Rendering.Stats.RecordGpuReadbackBytes((int)(activeCount * sizeof(uint)));

                var buckets = new List<uint>((int)activeCount);
                unsafe
                {
                    uint* basePtr = (uint*)mapped.Pointer;
                    for (uint i = 0; i < activeCount; ++i)
                    {
                        uint bucketIndex = basePtr[i];
                        if (bucketIndex < _materialTierBucketCount)
                            buckets.Add(bucketIndex);
                    }
                }

                return buckets.Count == 0 ? null : buckets;
            }
            finally
            {
                if (mappedHere)
                    _materialTierActiveBucketBuffer.UnmapBufferData();
            }
        }

        #endregion

        #region Material ID Management

        private bool PrepareMaterialTableAndValidateResidency(GPUScene scene, IReadOnlyList<HybridRenderingManager.DrawBatch>? batches)
        {
            using var profilerScope = Engine.Profiler.Start("GpuIndirect.PrepareMaterialTableAndValidateResidency");

            bool materialTableRequired = MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback &&
                ZeroReadbackMaterialDrawPath is EZeroReadbackMaterialDrawPath.MaterialTable
                    or EZeroReadbackMaterialDrawPath.BindlessMaterialTable;

            if (!VulkanFeatureProfile.EnableBindlessMaterialTable && !materialTableRequired)
                return true;

            _materialTable ??= new GPUMaterialTable(128);
            bool allResident = true;
            var renderState = Engine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            XRMaterial? overrideMaterial = renderState?.OverrideMaterial;
            bool useDepthNormalMaterialVariants = renderState?.UseDepthNormalMaterialVariants ?? false;

            HashSet<uint> currentIds = [.. scene.MaterialMap.Keys];
            foreach (uint removedId in _lastMaterialTableIds)
            {
                if (currentIds.Contains(removedId))
                    continue;

                _materialTable.Remove(removedId);
            }

            foreach (var (materialId, material) in scene.MaterialMap)
            {
                XRMaterial? effectiveMaterial = ResolveEffectiveGpuMaterial(material, overrideMaterial, useDepthNormalMaterialVariants);
                GPUMaterialEntry entry = BuildMaterialEntry(effectiveMaterial, out bool resident);
                _materialTable.AddOrUpdate(materialId, entry);
                allResident &= resident;
            }

            _materialTable.TrimTrailingUnused(128u);
            _materialTable.Buffer.PushSubData();

            _lastMaterialTableIds.Clear();
            foreach (uint materialId in currentIds)
                _lastMaterialTableIds.Add(materialId);

            SetMaterialTable(_materialTable);

            if (!allResident)
            {
                _skipGpuSubmissionThisPass = true;
                _skipGpuSubmissionReason = "Material residency guarantee failed before indirect draw submission.";
                if (_materialResidencyLogBudget > 0)
                {
                    Debug.MeshesWarning($"{FormatDebugPrefix("Materials")} {_skipGpuSubmissionReason}");
                    _materialResidencyLogBudget--;
                }

                return false;
            }

            if (VulkanFeatureProfile.ActiveGeometryFetchMode == EVulkanGeometryFetchMode.BufferDeviceAddressPrototype && _materialResidencyLogBudget > 0)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Materials")} Vulkan geometry fetch prototype is selected but atlas path remains active pending benchmark sign-off.");
                _materialResidencyLogBudget--;
            }

            return true;
        }

        private static GPUMaterialEntry BuildMaterialEntry(XRMaterial? material, out bool resident)
        {
            resident = true;
            uint flags = 0u;

            if (material is null)
            {
                resident = false;
                return new GPUMaterialEntry { Flags = flags };
            }

            XRTexture? albedo = material.Textures.Count > 0 ? material.Textures[0] : null;
            XRTexture? normal = material.Textures.Count > 1 ? material.Textures[1] : null;
            XRTexture? rm = material.Textures.Count > 2 ? material.Textures[2] : null;

            if (albedo is not null)
            {
                flags |= 1u << 0;
                resident &= IsTextureResident(albedo);
            }

            if (normal is not null)
            {
                flags |= 1u << 1;
                resident &= IsTextureResident(normal);
            }

            if (rm is not null)
            {
                flags |= 1u << 2;
                resident &= IsTextureResident(rm);
            }

            if (resident)
                flags |= 1u << 31;

            return new GPUMaterialEntry
            {
                Flags = flags,
                AlbedoHandle = 0ul,
                NormalHandle = 0ul,
                RMHandle = 0ul,
                Padding0 = 0u,
                Padding1 = 0u,
                Padding2 = 0u,
            };
        }

        private static bool IsTextureResident(XRTexture texture)
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is null)
                return false;

            AbstractRenderAPIObject? apiObject = renderer.GetOrCreateAPIRenderObject(texture, generateNow: true);
            return apiObject is not null && apiObject.IsGenerated;
        }

        /// <summary>
        /// Collects all material IDs from the scene's commands into a dedicated buffer.
        /// </summary>
        private void PopulateMaterialIDs(GPUScene scene)
        {
            if (_materialIDsBuffer is null || scene.TotalCommandCount == 0)
                return;

            uint count = scene.TotalCommandCount;
            Dbg($"PopulateMaterialIDs count={count}", "Materials");

            bool loggedSentinel = false;
            for (uint i = 0; i < count; i++)
            {
                var cmd = scene.AllLoadedCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                
                if (!loggedSentinel && cmd.MaterialID == uint.MaxValue)
                {
                    Dbg($"PopulateMaterialIDs sentinel detected @sceneIndex={i} mesh={cmd.MeshID}", "Materials");
                    loggedSentinel = true;
                }
                
                _materialIDsBuffer.SetDataRawAtIndex(i, cmd.MaterialID);
            }
        }

        #endregion

        #region Material Batching

        /// <summary>
        /// Creates draw batches grouped by material ID for efficient rendering.
        /// </summary>
        private List<HybridRenderingManager.DrawBatch>? BuildMaterialBatches(GPUScene scene)
        {
            // Phase 2: CPU-side mapping of the culled command buffer is debug-only.
            // Default path is a single submit using GPU-generated counts.
            if (!IsCpuBatchingEnabledForPass())
                return null;

            uint count = VisibleCommandCount;
            if (count == 0)
                return null;

            Dbg($"BuildMaterialBatches count={count}", "Materials");

            var batches = new List<HybridRenderingManager.DrawBatch>((int)Math.Min(count, 64));
            using var mappedBuffer = TryMapCulledBuffer();
            
            BuildBatchesFromCommands(scene, count, mappedBuffer, batches);

            if (batches.Count == 0)
                return null;

            LogMaterialBatches(scene, batches);
            return batches;
        }

        private MappedBufferScope TryMapCulledBuffer()
            => _culledSceneToRenderBuffer is not null ? new MappedBufferScope(_culledSceneToRenderBuffer) : default;

        /// <summary>
        /// Entry for command sorting by material ID. 
        /// </summary>
        private readonly struct MaterialSortEntry : IComparable<MaterialSortEntry>
        {
            public readonly uint OriginalIndex;
            public readonly uint MaterialId;

            public MaterialSortEntry(uint originalIndex, uint materialId)
            {
                OriginalIndex = originalIndex;
                MaterialId = materialId;
            }

            public int CompareTo(MaterialSortEntry other)
            {
                int materialCompare = MaterialId.CompareTo(other.MaterialId);
                return materialCompare != 0 ? materialCompare : OriginalIndex.CompareTo(other.OriginalIndex);
            }
        }

        private void BuildBatchesFromCommands(
            GPUScene scene,
            uint count,
            MappedBufferScope mappedBuffer,
            List<HybridRenderingManager.DrawBatch> batches)
        {
            if (EnableCpuMaterialSort && count > 1 &&
                BuildBatchesFromCommandsSorted(scene, count, mappedBuffer, batches))
                return;

            BuildBatchesFromCommandsUnsorted(scene, count, mappedBuffer, batches);
        }

        /// <summary>
        /// Builds batches with CPU-side sorting by material ID for contiguous batches.
        /// Reduces batch count significantly when materials aren't spatially coherent.
        /// </summary>
        private bool BuildBatchesFromCommandsSorted(
            GPUScene scene,
            uint count,
            MappedBufferScope mappedBuffer,
            List<HybridRenderingManager.DrawBatch> batches)
        {
            // Use ArrayPool to avoid allocation pressure
            MaterialSortEntry[] sortEntries = ArrayPool<MaterialSortEntry>.Shared.Rent((int)count);
            try
            {
                int unsortedBatchCount = 0;
                uint previousMaterial = uint.MaxValue;
                bool hasPrevious = false;

                // Collect material IDs with original indices
                for (uint i = 0; i < count; ++i)
                {
                    uint materialId = GetMaterialIdForCommand(scene, i, mappedBuffer);
                    sortEntries[i] = new MaterialSortEntry(i, materialId);

                    if (!hasPrevious || materialId != previousMaterial)
                    {
                        unsortedBatchCount++;
                        previousMaterial = materialId;
                        hasPrevious = true;
                    }
                }

                // Sort by material ID
                Array.Sort(sortEntries, 0, (int)count);

                if (!TryReorderIndirectCommandsByMaterial(sortEntries, count))
                {
                    Dbg("MaterialSort reorder failed; using unsorted batches.", "Materials");
                    return false;
                }

                // Build contiguous batches from sorted entries
                uint currentMaterial = sortEntries[0].MaterialId;
                uint batchStartIndex = 0;
                uint batchCount = 1;

                for (uint i = 1; i < count; ++i)
                {
                    uint materialId = sortEntries[i].MaterialId;

                    if (materialId == currentMaterial)
                    {
                        batchCount++;
                        continue;
                    }

                    // Emit batch for previous material in sorted draw order.
                    batches.Add(new HybridRenderingManager.DrawBatch(batchStartIndex, batchCount, currentMaterial));

                    currentMaterial = materialId;
                    batchStartIndex = i;
                    batchCount = 1;
                }

                // Emit final batch
                if (batchCount > 0)
                    batches.Add(new HybridRenderingManager.DrawBatch(batchStartIndex, batchCount, currentMaterial));

                Dbg($"MaterialSort: {count} commands, sorted batches={batches.Count}, unsorted batches={unsortedBatchCount}", "Materials");
                return true;
            }
            finally
            {
                ArrayPool<MaterialSortEntry>.Shared.Return(sortEntries);
            }
        }

        /// <summary>
        /// Reorders indirect draw commands to match CPU sorted material order.
        /// </summary>
        private bool TryReorderIndirectCommandsByMaterial(MaterialSortEntry[] sortedEntries, uint count)
        {
            if (_indirectDrawBuffer is null)
            {
                Dbg("MaterialSort reorder skipped - indirect draw buffer missing.", "Materials");
                return false;
            }

            if (count == 0)
                return true;

            if (count > _indirectDrawBuffer.ElementCount)
            {
                Dbg($"MaterialSort reorder skipped - visible count {count} exceeds indirect capacity {_indirectDrawBuffer.ElementCount}.", "Materials");
                return false;
            }

            DrawElementsIndirectCommand[] sortedCommands = ArrayPool<DrawElementsIndirectCommand>.Shared.Rent((int)count);
            try
            {
                for (uint i = 0; i < count; ++i)
                {
                    uint originalIndex = sortedEntries[i].OriginalIndex;
                    if (originalIndex >= _indirectDrawBuffer.ElementCount)
                    {
                        Dbg($"MaterialSort reorder aborted - original index {originalIndex} out of bounds.", "Materials");
                        return false;
                    }

                    sortedCommands[i] = _indirectDrawBuffer.GetDataRawAtIndex<DrawElementsIndirectCommand>(originalIndex);
                }

                for (uint i = 0; i < count; ++i)
                    _indirectDrawBuffer.SetDataRawAtIndex(i, sortedCommands[i]);

                uint byteLength = checked(count * (uint)Unsafe.SizeOf<DrawElementsIndirectCommand>());
                _indirectDrawBuffer.PushSubData(0, byteLength);
                return true;
            }
            catch (Exception ex)
            {
                Dbg($"MaterialSort reorder failed ex={ex.Message}", "Materials");
                return false;
            }
            finally
            {
                ArrayPool<DrawElementsIndirectCommand>.Shared.Return(sortedCommands);
            }
        }

        /// <summary>
        /// Original unsorted batch building - groups contiguous commands with same material.
        /// </summary>
        private void BuildBatchesFromCommandsUnsorted(
            GPUScene scene,
            uint count,
            MappedBufferScope mappedBuffer,
            List<HybridRenderingManager.DrawBatch> batches)
        {
            uint currentMaterial = uint.MaxValue;
            uint batchStart = 0;
            uint batchCount = 0;

            for (uint i = 0; i < count; ++i)
            {
                uint materialId = GetMaterialIdForCommand(scene, i, mappedBuffer);

                if (batchCount > 0 && materialId == currentMaterial)
                {
                    batchCount++;
                    continue;
                }

                if (batchCount > 0)
                    batches.Add(new HybridRenderingManager.DrawBatch(batchStart, batchCount, currentMaterial));

                currentMaterial = materialId;
                batchStart = i;
                batchCount = 1;
            }

            if (batchCount > 0)
                batches.Add(new HybridRenderingManager.DrawBatch(batchStart, batchCount, currentMaterial));
        }

        private uint GetMaterialIdForCommand(GPUScene scene, uint index, MappedBufferScope mappedBuffer)
            => mappedBuffer.TryReadCommand(index, out var culledCmd) &&
                TryValidateMaterialId(scene, culledCmd.MaterialID, "culled buffer")
                ? culledCmd.MaterialID
                : ResolveMaterialId(scene, index);

        #endregion

        #region Material ID Resolution

        private bool TryValidateMaterialId(GPUScene scene, uint sourceId, string sourceName)
        {
            if (sourceId == 0 || !scene.MaterialMap.ContainsKey(sourceId))
            {
                LogMaterialValidationFailure(sourceId, sourceName);
                return false;
            }
            return true;
        }

        private void LogMaterialValidationFailure(uint sourceId, string sourceName)
        {
            if (sourceId == 0)
                return;

            int remaining = Interlocked.Decrement(ref _resolveMaterialLogBudget);
            if (remaining >= 0)
            {
                Dbg($"ResolveMaterialId rejected id={sourceId} from {sourceName} (not in MaterialMap). Remaining logs: {remaining}", "Materials");
                if (remaining == 0)
                    Dbg("ResolveMaterialId rejection log budget exhausted; suppressing further logs.", "Materials");
            }
        }

        private uint ResolveMaterialId(GPUScene scene, uint visibleIndex)
        {
            // Try culled buffer first
            if (TryGetMaterialFromBuffer(_culledSceneToRenderBuffer, visibleIndex, scene, "culled buffer", out uint id))
                return id;

            // Try material IDs buffer
            if (TryGetMaterialIdFromMaterialBuffer(visibleIndex, scene, out id))
                return id;

            // Fallback to scene commands
            if (TryGetMaterialFromBuffer(scene.AllLoadedCommandsBuffer, visibleIndex, scene, "scene command buffer", out id))
                return id;

            return 0;
        }

        private bool TryGetMaterialFromBuffer(XRDataBuffer? buffer, uint index, GPUScene scene, string sourceName, out uint materialId)
        {
            materialId = 0;
            if (buffer is null || index >= buffer.ElementCount)
                return false;

            try
            {
                var cmd = buffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(index);
                if (TryValidateMaterialId(scene, cmd.MaterialID, sourceName))
                {
                    LogSentinelIfDetected(cmd.MaterialID, index, sourceName);
                    materialId = cmd.MaterialID;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Dbg($"ResolveMaterialId {sourceName} read failed idx={index} ex={ex.Message}", "Materials");
            }
            return false;
        }

        private bool TryGetMaterialIdFromMaterialBuffer(uint index, GPUScene scene, out uint materialId)
        {
            materialId = 0;
            if (_materialIDsBuffer is null || index >= _materialIDsBuffer.ElementCount)
                return false;

            try
            {
                uint id = _materialIDsBuffer.GetDataRawAtIndex<uint>(index);
                if (TryValidateMaterialId(scene, id, "material buffer"))
                {
                    LogSentinelIfDetected(id, index, "material buffer");
                    materialId = id;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Dbg($"ResolveMaterialId material buffer read failed idx={index} ex={ex.Message}", "Materials");
            }
            return false;
        }

        private void LogSentinelIfDetected(uint materialId, uint index, string sourceName)
        {
            if (materialId == uint.MaxValue)
                Dbg($"ResolveMaterialId detected sentinel materialID=uint.MaxValue from {sourceName} @idx={index}", "Materials");
        }

        #endregion

        #region Diagnostics & Logging

        private void QueueAsyncGpuTriangleStatsReadback()
        {
            if (_statsBuffer is null)
                return;

            AbstractRenderer.Current?.QueueGpuRenderStatsBufferReadback(
                _statsBuffer,
                publishDraws: false,
                publishTriangles: true);
        }

        private void PostRenderDiagnostics(GPUScene scene)
        {
            if (!ShouldCaptureDiagnosticReadbacksForPass())
            {
                uint requestedDraws = scene.TotalCommandCount;
                uint emittedDraws = VisibleCommandCount;
                uint culledDraws = requestedDraws > emittedDraws ? requestedDraws - emittedDraws : 0u;
                Engine.Rendering.Stats.RecordVulkanIndirectEffectiveness(
                    requestedDraws,
                    culledDraws,
                    emittedDraws,
                    emittedDraws,
                    overflowCount: 0u);
                return;
            }

            _ = BvhGpuProfiler.Instance.ResolveAndPublish(Engine.Time.Timer.Render.LastTimestampTicks, _statsBuffer);
            uint overflowCount = CheckOverflowFlags(scene);
            LogGpuStats(overflowCount);
        }

        private uint CheckOverflowFlags(GPUScene scene)
        {
            if (!ShouldCaptureDiagnosticReadbacksForPass())
                return 0u;

            if (_cullingOverflowFlagBuffer is null || _indirectOverflowFlagBuffer is null || _truncationFlagBuffer is null)
                return 0u;

            uint cullOv = ReadUInt(_cullingOverflowFlagBuffer);
            uint indOv = ReadUInt(_indirectOverflowFlagBuffer);
            uint trunc = ReadUInt(_truncationFlagBuffer);
            uint overflowTotal = cullOv + indOv + trunc;

            if (cullOv != 0 || indOv != 0 || trunc != 0)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Stats")} GPU Render Overflow: Culling={cullOv} Indirect={indOv} Trunc={trunc}");
                Dbg($"Overflow flags cull={cullOv} indirect={indOv} trunc={trunc}", "Stats");

                uint currentCapacity = scene.AllocatedMaxCommandCount;
                uint minimumRequired = Math.Max(Math.Max(scene.TotalCommandCount, VisibleCommandCount), 1u);
                uint requestedCapacity = ComputeBoundedDoublingCapacity(currentCapacity, minimumRequired);

                if (requestedCapacity > currentCapacity)
                {
                    uint finalCapacity = scene.EnsureCommandCapacity(requestedCapacity);
                    Debug.MeshesWarning($"{FormatDebugPrefix("Stats")} Overflow growth policy requested capacity increase {currentCapacity} -> {finalCapacity} (required={minimumRequired}).");
                }
            }

            LogValidationDetails(cullOv);
            return overflowTotal;
        }

        private void LogValidationDetails(uint cullOv)
        {
            if (!IsValidationLoggingEnabledForPass() || _culledSceneToRenderBuffer is null)
                return;

            if (cullOv > 0 && IsDebugLoggingEnabledForPass())
            {
                Debug.Meshes($"{FormatDebugPrefix("Validation")} Culling overflow count={cullOv} " +
                         $"(capacity={_culledSceneToRenderBuffer.ElementCount}, visible={VisibleCommandCount})");
            }

            if (_culledSceneToRenderBuffer.ElementCount > 0)
            {
                var tail = _culledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(
                    _culledSceneToRenderBuffer.ElementCount - 1);
                    
                if (tail.MeshID == uint.MaxValue || tail.MaterialID == uint.MaxValue)
                {
                    Debug.Meshes($"{FormatDebugPrefix("Validation")} Overflow sentinel at tail (mesh={tail.MeshID} material={tail.MaterialID})");
                }
            }
        }

        private void LogGpuStats(uint overflowCount)
        {
            if (_statsBuffer is null || !ShouldCaptureDiagnosticReadbacksForPass())
                return;

            Span<uint> values = stackalloc uint[(int)GpuStatsLayout.FieldCount];
            ReadUints(_statsBuffer, values);

            var stats = new GpuRenderStats(values);
            int cpuFallbackEvents = Engine.Rendering.Stats.GpuCpuFallbackEvents;
            int cpuFallbackRecovered = Engine.Rendering.Stats.GpuCpuFallbackRecoveredCommands;
            uint consumedDrawCount = 0u;
            if (!IsCpuReadbackCountDisabledForPass() && _drawCountBuffer is not null)
                consumedDrawCount = ReadUIntAt(_drawCountBuffer, 0u);

            Engine.Rendering.Stats.RecordVulkanIndirectEffectiveness(
                requestedDraws: stats.Input,
                culledDraws: stats.Culled,
                emittedIndirectDraws: stats.Drawn,
                consumedDraws: consumedDrawCount,
                overflowCount: overflowCount);

            if (IsDebugLoggingEnabledForPass())
            {
                Debug.Meshes($"{FormatDebugPrefix("Stats")} [GPU Stats] In={stats.Input} CulledOut={stats.Culled} " +
                         $"Draws={stats.Drawn} Tris={stats.Triangles} RejFrustum={stats.FrustumRejected} RejDist={stats.DistanceRejected} " +
                         $"CpuFallbackEvents={cpuFallbackEvents} CpuRecovered={cpuFallbackRecovered}");

                Debug.Meshes($"{FormatDebugPrefix("Stats")} [Transparency] Masked={MaskedVisibleCommandCount} " +
                         $"Approximate={ApproximateTransparentVisibleCommandCount} Exact={ExactTransparentVisibleCommandCount}");

                EOcclusionCullingMode occlusionMode = ActiveOcclusionMode;
                if (occlusionMode != EOcclusionCullingMode.Disabled)
                {
                    Debug.Meshes($"{FormatDebugPrefix("Stats")} [Occlusion] Mode={occlusionMode} " +
                             $"Candidates={OcclusionCandidatesTested} Occluded={OcclusionAccepted} " +
                             $"Recoveries={OcclusionFalsePositiveRecoveries} TemporalOverrides={OcclusionTemporalOverrides}");
                }

                if (stats.HasBvhActivity)
                {
                    Debug.Meshes($"{FormatDebugPrefix("Stats")} [BVH] Build={stats.BvhBuildCount} ({stats.BvhBuildMs:F3} ms) " +
                             $"Refit={stats.BvhRefitCount} ({stats.BvhRefitMs:F3} ms) " +
                             $"Cull={stats.BvhCullCount} ({stats.BvhCullMs:F3} ms) " +
                             $"Ray={stats.BvhRayCount} ({stats.BvhRayMs:F3} ms)");
                }
            }

            LogTransparencyDomainStats(
                (uint)Engine.Rendering.Stats.GpuTransparencyOpaqueOrOtherVisible,
                (uint)Engine.Rendering.Stats.GpuTransparencyMaskedVisible,
                (uint)Engine.Rendering.Stats.GpuTransparencyApproximateVisible,
                (uint)Engine.Rendering.Stats.GpuTransparencyExactVisible);

            Dbg($"Stats in={stats.Input} culled={stats.Culled} draws={stats.Drawn} tris={stats.Triangles} " +
                $"frustumRej={stats.FrustumRejected} distRej={stats.DistanceRejected} " +
                $"cpuFallbackEvents={cpuFallbackEvents} cpuRecovered={cpuFallbackRecovered} " +
                $"masked={MaskedVisibleCommandCount} approximate={ApproximateTransparentVisibleCommandCount} exact={ExactTransparentVisibleCommandCount}", "Stats");
        }

        private void LogMaterialBatches(GPUScene scene, List<HybridRenderingManager.DrawBatch> batches)
        {
            var sb = new StringBuilder($"BuildMaterialBatches produced {batches.Count} batches:");

            foreach (var (batch, index) in batches.Select((b, i) => (b, i)))
            {
                string materialName = scene.MaterialMap.TryGetValue(batch.MaterialID, out XRMaterial? mat) && mat is not null
                    ? (mat.Name ?? $"Material#{batch.MaterialID}")
                    : (batch.MaterialID == 0 ? "<Invalid>" : $"Material#{batch.MaterialID}");

                sb.Append($" [#{index}] {materialName} -> {batch.Count} draws");
            }

            Dbg(sb.ToString(), "Materials");
        }

        private void DumpIndirectSummary(uint drawReported)
        {
            if (!IsDebugLoggingEnabledForPass())
                return;

            uint sampleCount = Math.Min(drawReported == 0 ? VisibleCommandCount : drawReported, 8u);
            string prefix = FormatDebugPrefix("Indirect");

            Debug.Meshes($"{prefix} [Indirect/Dump] drawReported={drawReported} visible={VisibleCommandCount} batches={CurrentBatches?.Count ?? 0}\n" +
                     $"  CountBufferMapped={_drawCountBuffer?.ActivelyMapping.Count > 0} CulledBufferMapped={_culledCountBuffer?.ActivelyMapping.Count > 0}\n" +
                     $"  SampleCount={sampleCount}");
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            Dbg("Dispose invoked", "Lifecycle");

            if (_disposed)
                return;

            using (_lock.EnterScope())
            {
                DisposeBuffers();
                DisposeShaders();
                UnsubscribeFromAtlasEvents();
                
                _buffersMapped = false;
                _initialized = false;
                _disposed = true;
            }

            Dbg("Dispose complete", "Lifecycle");
        }

        private void UnsubscribeFromAtlasEvents()
        {
            if (_subscribedScene is not null)
            {
                _subscribedScene.AtlasRebuilt -= OnAtlasRebuilt;
                _subscribedScene = null;
            }
        }

        private void DisposeBuffers()
        {
            _indirectDrawBuffer?.Dispose();
            _culledCountBuffer?.Dispose();
            _cullCountScratchBuffer?.Dispose();
            _drawCountBuffer?.Dispose();
            _cullingOverflowFlagBuffer?.Dispose();
            _indirectOverflowFlagBuffer?.Dispose();
            _occlusionOverflowFlagBuffer?.Dispose();
            _sortedCommandBuffer?.Dispose();
            _keyIndexBufferA?.Dispose();
            _gpuBatchRangeBuffer?.Dispose();
            _gpuBatchCountBuffer?.Dispose();
            _materialSlotLookupBuffer?.Dispose();
            _materialTierIndirectDrawBuffer?.Dispose();
            _materialTierDrawCountBuffer?.Dispose();
            _materialTierActiveBucketBuffer?.Dispose();
            _materialTierActiveBucketCountBuffer?.Dispose();
            _instanceTransformBuffer?.Dispose();
            _instanceSourceIndexBuffer?.Dispose();
            _materialAggregationBuffer?.Dispose();
            _maskedVisibleIndexBuffer?.Dispose();
            _approximateTransparentVisibleIndexBuffer?.Dispose();
            _exactTransparentVisibleIndexBuffer?.Dispose();
            _transparencyDomainCountBuffer?.Dispose();
            _culledSceneToRenderBuffer?.Dispose();
            _occlusionCulledBuffer?.Dispose();
            _sourceHotCommandBuffer?.Dispose();
            _culledHotCommandBuffer?.Dispose();
            _occlusionCulledHotBuffer?.Dispose();
            _passFilterDebugBuffer?.Dispose();
            _materialIDsBuffer?.Dispose();
            _materialTable?.Dispose();
            _keyIndexScratchBuffer?.Dispose();
            DisposeViewSetBuffers();

            _hiZDepthPyramidOwned?.Destroy();
            _hiZDepthPyramidOwned = null;
            _hiZDepthPyramid = null;
            _materialSlotLookupUploadedBuffer = null;
            _materialSlotLookupSignature = 0ul;
            _materialSlotLookupUploadedElementCount = 0u;
            _materialAggregationUploadedBuffer = null;
            _materialAggregationSignature = 0ul;
            _materialAggregationUploadedElementCount = 0u;
            _materialSlotIds.Clear();
            _materialSlotSortScratch.Clear();
        }

        private void DisposeShaders()
        {
            _cullingComputeShader?.Destroy();
            _buildKeysComputeShader?.Destroy();
            _buildGpuBatchesComputeShader?.Destroy();
            _materialScatterComputeShader?.Destroy();
            _buildActiveMaterialBucketsComputeShader?.Destroy();
            _classifyTransparencyComputeShader?.Destroy();
            _lodSelectComputeShader?.Destroy();
            _indirectRenderTaskShader?.Destroy();
            _buildHotCommandsProgram?.Destroy();
            _clearUIntsComputeShader?.Destroy();
            _indirectRenderer?.Destroy();

            _hiZInitProgram?.Destroy();
            _hiZGenProgram?.Destroy();
            _hiZOcclusionProgram?.Destroy();
            _copyCount3Program?.Destroy();
        }

        #endregion

        #region Helper Types

        /// <summary>
        /// Parsed GPU render statistics for convenient access.
        /// </summary>
        private readonly struct GpuRenderStats
        {
            public uint Input { get; }
            public uint Culled { get; }
            public uint Drawn { get; }
            public uint Triangles { get; }
            public uint FrustumRejected { get; }
            public uint DistanceRejected { get; }
            public uint BvhBuildCount { get; }
            public uint BvhRefitCount { get; }
            public uint BvhCullCount { get; }
            public uint BvhRayCount { get; }
            public double BvhBuildMs { get; }
            public double BvhRefitMs { get; }
            public double BvhCullMs { get; }
            public double BvhRayMs { get; }

            public bool HasBvhActivity => BvhBuildCount + BvhRefitCount + BvhCullCount + BvhRayCount > 0;

            public GpuRenderStats(Span<uint> values)
            {
                Input = values[(int)GpuStatsLayout.StatsInputCount];
                Culled = values[(int)GpuStatsLayout.StatsCulledCount];
                Drawn = values[(int)GpuStatsLayout.StatsDrawCount];
                Triangles = values[(int)GpuStatsLayout.StatsTriangleCount];
                FrustumRejected = values[(int)GpuStatsLayout.StatsRejectedFrustum];
                DistanceRejected = values[(int)GpuStatsLayout.StatsRejectedDistance];
                BvhBuildCount = values[(int)GpuStatsLayout.BvhBuildCount];
                BvhRefitCount = values[(int)GpuStatsLayout.BvhRefitCount];
                BvhCullCount = values[(int)GpuStatsLayout.BvhCullCount];
                BvhRayCount = values[(int)GpuStatsLayout.BvhRayCount];

                BvhBuildMs = ToMs(values[(int)GpuStatsLayout.BvhBuildTimeLo], values[(int)GpuStatsLayout.BvhBuildTimeHi]);
                BvhRefitMs = ToMs(values[(int)GpuStatsLayout.BvhRefitTimeLo], values[(int)GpuStatsLayout.BvhRefitTimeHi]);
                BvhCullMs = ToMs(values[(int)GpuStatsLayout.BvhCullTimeLo], values[(int)GpuStatsLayout.BvhCullTimeHi]);
                BvhRayMs = ToMs(values[(int)GpuStatsLayout.BvhRayTimeLo], values[(int)GpuStatsLayout.BvhRayTimeHi]);
            }

            private static double ToMs(uint lo, uint hi) => ((double)((ulong)hi << 32 | lo)) / 1_000_000.0;
        }

        /// <summary>
        /// RAII wrapper for safely mapping and unmapping culled buffer data.
        /// </summary>
        private readonly struct MappedBufferScope : IDisposable
        {
            private readonly XRDataBuffer? _buffer;
            private readonly bool _mappedHere;
            private readonly IntPtr _basePtr;
            private readonly uint _stride;

            public bool IsValid => _basePtr != IntPtr.Zero;

            public MappedBufferScope(XRDataBuffer buffer)
            {
                _buffer = buffer;
                _mappedHere = false;
                _basePtr = IntPtr.Zero;
                _stride = 0;

                try
                {
                    if (buffer.ActivelyMapping.Count == 0)
                    {
                        buffer.StorageFlags |= EBufferMapStorageFlags.Read;
                        buffer.RangeFlags |= EBufferMapRangeFlags.Read;
                        buffer.MapBufferData();
                        _mappedHere = true;
                        Engine.Rendering.Stats.RecordGpuBufferMapped();
                    }

                    var culledPtr = buffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
                    if (culledPtr.IsValid)
                    {
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                        _stride = buffer.ElementSize != 0 ? buffer.ElementSize : GPUScene.CommandFloatCount * sizeof(float);
                        unsafe { _basePtr = (IntPtr)culledPtr.Pointer; }
                    }
                    else if (_mappedHere)
                    {
                        buffer.UnmapBufferData();
                        _mappedHere = false;
                    }
                }
                catch
                {
                    if (_mappedHere)
                        buffer.UnmapBufferData();
                    _mappedHere = false;
                }
            }

            public bool TryReadCommand(uint index, out GPUIndirectRenderCommand command)
            {
                command = default;
                if (_basePtr == IntPtr.Zero)
                    return false;

                Engine.Rendering.Stats.RecordGpuReadbackBytes(Unsafe.SizeOf<GPUIndirectRenderCommand>());

                unsafe
                {
                    byte* ptr = (byte*)_basePtr + (index * _stride);
                    command = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(ptr);
                }
                return true;
            }

            public void Dispose()
            {
                if (_mappedHere && _buffer is not null)
                    _buffer.UnmapBufferData();
            }
        }

        #endregion
    }
}
