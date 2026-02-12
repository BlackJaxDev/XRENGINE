using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Lists.Unsafe;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
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
        
        /// <summary>
        /// When true, sorts commands by material ID on CPU to create contiguous batches.
        /// This reduces batch count at the cost of CPU overhead for sorting.
        /// </summary>
        public bool EnableCpuMaterialSort { get; set; } = false;

        #endregion

        #region Main Render Pipeline

        /// <summary>
        /// Renders this pass using indirect rendering fully on-GPU.
        /// </summary>
        public void Render(GPUScene scene)
        {
            using var renderTiming = BeginTiming("GPURenderPassCollection.Render");
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render begin (pass={0})", RenderPass);
            Dbg("Render begin", "Lifecycle");

            if (!TryInitializeRender(scene, out XRCamera? camera))
                return;

            _gpuBatchingPreparedThisFrame = false;
            ResetCounters();
            Cull(scene, camera);

            // Phase 2: do not early-out based on CPU-visible counters.
            // The default submission path uses GPU-written count buffers; a 0 count naturally results in no draws.

            bool useCpuBatchFallback = !EnableGpuDrivenBatching || IndirectDebug.EnableCpuBatching;
            List<HybridRenderingManager.DrawBatch>? batches;

            if (useCpuBatchFallback)
            {
                using (BeginTiming("PopulateMaterialIDs"))
                    PopulateMaterialIDs(scene);

                using (BeginTiming("BuildIndirectCommandBuffer"))
                    BuildIndirectCommandBuffer(scene);

                using var batchTiming = BeginTiming("BuildMaterialBatchesCpuFallback");
                batches = BuildMaterialBatches(scene);
                CurrentBatches = batches;
                _gpuBatchingPreparedThisFrame = false;
            }
            else
            {
                using var batchTiming = BeginTiming("BuildGpuBatchesAndInstancing");
                batches = BuildGpuBatchesAndInstancing(scene);
                CurrentBatches = batches;
                _gpuBatchingPreparedThisFrame = batches is not null;

                if (batches is null || batches.Count == 0)
                {
                    if (scene.TotalCommandCount > 0)
                    {
                        Debug.LogWarning($"{FormatDebugPrefix("Materials")} GPU batching produced no batch ranges. " +
                                         "Enable IndirectDebug.EnableCpuBatching for emergency fallback diagnostics.");
                    }
                    return;
                }
            }

            Log(LogCategory.Indirect, LogLevel.Info, "Indirect build complete - visible={0}", VisibleCommandCount);
            Dbg("Indirect build complete", "Indirect");

            if (batches is not null)
                Log(LogCategory.Materials, LogLevel.Info, "Material batches={0}, visible commands={1}", batches.Count, VisibleCommandCount);

            _renderManager.Render(this, camera, scene, _indirectDrawBuffer!, _indirectRenderer, RenderPass, _drawCountBuffer, batches);
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render submission done");
            Dbg("Render submission done", "Lifecycle");

            _useBufferAForRender = !_useBufferAForRender;

            PostRenderDiagnostics();
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render end");
            Dbg("Render end", "Lifecycle");
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

            camera = Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.SceneCamera;
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

            _resetCountersComputeShader.BindBuffer(_culledCountBuffer, 0);
            _resetCountersComputeShader.BindBuffer(_drawCountBuffer, 1);
            if (_cullingOverflowFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 2);
            if (_indirectOverflowFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_indirectOverflowFlagBuffer, 3);
            if (_truncationFlagBuffer is not null)
                _resetCountersComputeShader.BindBuffer(_truncationFlagBuffer, 4);
            _resetCountersComputeShader.BindBuffer(_cullCountScratchBuffer, 6);
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
            Dbg("BuildIndirect begin", "Indirect");

            if (_indirectRenderTaskShader is null || _indirectDrawBuffer is null)
            {
                Dbg("BuildIndirect abort - shaders or draw buffer null", "Indirect");
                return;
            }

            // Phase 2: avoid CPU readback of visible counters in the hot path.
            // Indirect compute shaders consume the GPU-written count buffer directly.
            UpdateVisibleCountersFromBuffer();
            BindIndirectShaderUniforms();
            BindIndirectShaderBuffers(scene);

            uint dispatchCommands = VisibleCommandCount;
            if (IndirectDebug.DisableCpuReadbackCount)
            {
                // When we don't read counts back, conservatively dispatch for the max indirect capacity.
                dispatchCommands = _indirectDrawBuffer!.ElementCount;
            }

            uint dispatchGroups = Math.Max(1, ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _indirectRenderTaskShader.DispatchCompute(dispatchGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"Indirect dispatch groups={dispatchGroups} visible={VisibleCommandCount}", "Indirect");
        }

        private void BindIndirectShaderUniforms()
        {
            _indirectRenderTaskShader!.Uniform("CurrentRenderPass", RenderPass);
            _indirectRenderTaskShader.Uniform("MaxIndirectDraws", (int)_indirectDrawBuffer!.ElementCount);
            _indirectRenderTaskShader.Uniform("AtlasAll16Bit", 0);
            _indirectRenderTaskShader.Uniform("StatsEnabled", _statsBuffer is not null ? 1u : 0u);
            _indirectRenderTaskShader.Uniform("ActiveViewCount", (int)(_activeViewCount == 0u ? 1u : _activeViewCount));
            _indirectRenderTaskShader.Uniform("SourceViewId", (int)_indirectSourceViewId);
        }

        private void BindIndirectShaderBuffers(GPUScene scene)
        {
            CulledSceneToRenderBuffer.BindTo(_indirectRenderTaskShader!, 0);
            _indirectDrawBuffer!.BindTo(_indirectRenderTaskShader!, 1);
            scene.MeshDataBuffer.BindTo(_indirectRenderTaskShader!, 2);
            _culledCountBuffer?.BindTo(_indirectRenderTaskShader!, 3);
            _drawCountBuffer?.BindTo(_indirectRenderTaskShader!, 4);
            _indirectOverflowFlagBuffer?.BindTo(_indirectRenderTaskShader!, 5);

            if (_truncationFlagBuffer is not null)
            {
                _truncationFlagBuffer.SetDataRawAtIndex(0, 0u);
                _truncationFlagBuffer.PushSubData();
                _truncationFlagBuffer.BindTo(_indirectRenderTaskShader!, 7);
            }

            _statsBuffer?.BindTo(_indirectRenderTaskShader!, 8);
            BindViewSetBuffers(_indirectRenderTaskShader!);
        }

        private List<HybridRenderingManager.DrawBatch>? BuildGpuBatchesAndInstancing(GPUScene scene)
        {
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
            UpdateVisibleCountersFromBuffer();

            return ReadGpuBatchRanges();
        }

        private void DispatchBuildKeys()
        {
            if (_buildKeysComputeShader is null || _keyIndexBufferA is null || _culledCountBuffer is null)
                return;

            uint dispatchCommands = IndirectDebug.DisableCpuReadbackCount
                ? _keyIndexBufferA.ElementCount
                : Math.Max(VisibleCommandCount, 1u);

            _buildKeysComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _buildKeysComputeShader.Uniform("MaxSortKeys", (int)_keyIndexBufferA.ElementCount);
            _buildKeysComputeShader.Uniform("StateBitMask", 0x0FFFu);

            CulledSceneToRenderBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysInputCommands);
            _culledCountBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysCulledCount);
            _keyIndexBufferA.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysSortKeys);

            uint groups = Math.Max(1, ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _buildKeysComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
        }

        private void DispatchBuildGpuBatches(GPUScene scene)
        {
            if (_buildGpuBatchesComputeShader is null ||
                _keyIndexBufferA is null ||
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

            CulledSceneToRenderBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesInputCommands);
            scene.MeshDataBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesMeshData);
            _culledCountBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesCulledCount);
            _keyIndexBufferA.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesSortKeys);
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

            _buildGpuBatchesComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }

        private void PopulateMaterialAggregationFlags(GPUScene scene)
        {
            if (_materialAggregationBuffer is null)
                return;

            uint maxMaterialId = 0u;
            foreach (uint id in scene.MaterialMap.Keys)
            {
                if (id > maxMaterialId)
                    maxMaterialId = id;
            }

            EnsureMaterialAggregationBuffer(maxMaterialId + 1u);
            if (_materialAggregationBuffer is null)
                return;

            for (uint i = 0; i < _materialAggregationBuffer.ElementCount; ++i)
                _materialAggregationBuffer.SetDataRawAtIndex(i, 1u);

            foreach (var (materialID, material) in scene.MaterialMap)
            {
                uint allow = MaterialSupportsGpuInstanceAggregation(material) ? 1u : 0u;
                if (materialID < _materialAggregationBuffer.ElementCount)
                    _materialAggregationBuffer.SetDataRawAtIndex(materialID, allow);
            }

            _materialAggregationBuffer.PushSubData();
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

        #endregion

        #region Material ID Management

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
            if (!IndirectDebug.EnableCpuBatching)
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

        private void PostRenderDiagnostics()
        {
            _ = BvhGpuProfiler.Instance.ResolveAndPublish(Engine.Time.Timer.Render.LastTimestamp, _statsBuffer);
            CheckOverflowFlags();
            LogGpuStats();
        }

        private void CheckOverflowFlags()
        {
            if (_cullingOverflowFlagBuffer is null || _indirectOverflowFlagBuffer is null || _truncationFlagBuffer is null)
                return;

            uint cullOv = ReadUInt(_cullingOverflowFlagBuffer);
            uint indOv = ReadUInt(_indirectOverflowFlagBuffer);
            uint trunc = ReadUInt(_truncationFlagBuffer);

            if (cullOv != 0 || indOv != 0 || trunc != 0)
            {
                Debug.LogWarning($"{FormatDebugPrefix("Stats")} GPU Render Overflow: Culling={cullOv} Indirect={indOv} Trunc={trunc}");
                Dbg($"Overflow flags cull={cullOv} indirect={indOv} trunc={trunc}", "Stats");
            }

            LogValidationDetails(cullOv);
        }

        private void LogValidationDetails(uint cullOv)
        {
            if (!Engine.EffectiveSettings.EnableGpuIndirectValidationLogging || _culledSceneToRenderBuffer is null)
                return;

            if (cullOv > 0 && Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                Debug.Out($"{FormatDebugPrefix("Validation")} Culling overflow count={cullOv} " +
                         $"(capacity={_culledSceneToRenderBuffer.ElementCount}, visible={VisibleCommandCount})");
            }

            if (_culledSceneToRenderBuffer.ElementCount > 0)
            {
                var tail = _culledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(
                    _culledSceneToRenderBuffer.ElementCount - 1);
                    
                if (tail.MeshID == uint.MaxValue || tail.MaterialID == uint.MaxValue)
                {
                    Debug.Out($"{FormatDebugPrefix("Validation")} Overflow sentinel at tail (mesh={tail.MeshID} material={tail.MaterialID})");
                }
            }
        }

        private void LogGpuStats()
        {
            if (_statsBuffer is null)
                return;

            Span<uint> values = stackalloc uint[(int)GpuStatsLayout.FieldCount];
            ReadUints(_statsBuffer, values);

            var stats = new GpuRenderStats(values);
            int cpuFallbackEvents = Engine.Rendering.Stats.GpuCpuFallbackEvents;
            int cpuFallbackRecovered = Engine.Rendering.Stats.GpuCpuFallbackRecoveredCommands;

            if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                Debug.Out($"{FormatDebugPrefix("Stats")} [GPU Stats] In={stats.Input} CulledOut={stats.Culled} " +
                         $"Draws={stats.Drawn} RejFrustum={stats.FrustumRejected} RejDist={stats.DistanceRejected} " +
                         $"CpuFallbackEvents={cpuFallbackEvents} CpuRecovered={cpuFallbackRecovered}");

                EOcclusionCullingMode occlusionMode = ActiveOcclusionMode;
                if (occlusionMode != EOcclusionCullingMode.Disabled)
                {
                    Debug.Out($"{FormatDebugPrefix("Stats")} [Occlusion] Mode={occlusionMode} " +
                             $"Candidates={OcclusionCandidatesTested} Occluded={OcclusionAccepted} " +
                             $"Recoveries={OcclusionFalsePositiveRecoveries} TemporalOverrides={OcclusionTemporalOverrides}");
                }

                if (stats.HasBvhActivity)
                {
                    Debug.Out($"{FormatDebugPrefix("Stats")} [BVH] Build={stats.BvhBuildCount} ({stats.BvhBuildMs:F3} ms) " +
                             $"Refit={stats.BvhRefitCount} ({stats.BvhRefitMs:F3} ms) " +
                             $"Cull={stats.BvhCullCount} ({stats.BvhCullMs:F3} ms) " +
                             $"Ray={stats.BvhRayCount} ({stats.BvhRayMs:F3} ms)");
                }
            }

            Dbg($"Stats in={stats.Input} culled={stats.Culled} draws={stats.Drawn} " +
                $"frustumRej={stats.FrustumRejected} distRej={stats.DistanceRejected} " +
                $"cpuFallbackEvents={cpuFallbackEvents} cpuRecovered={cpuFallbackRecovered}", "Stats");
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
            if (!Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                return;

            uint sampleCount = Math.Min(drawReported == 0 ? VisibleCommandCount : drawReported, 8u);
            string prefix = FormatDebugPrefix("Indirect");

            Debug.Out($"{prefix} [Indirect/Dump] drawReported={drawReported} visible={VisibleCommandCount} batches={CurrentBatches?.Count ?? 0}\n" +
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
            _instanceTransformBuffer?.Dispose();
            _instanceSourceIndexBuffer?.Dispose();
            _materialAggregationBuffer?.Dispose();
            _culledSceneToRenderBuffer?.Dispose();
            _occlusionCulledBuffer?.Dispose();
            _passFilterDebugBuffer?.Dispose();
            _materialIDsBuffer?.Dispose();
            DisposeViewSetBuffers();

            _hiZDepthPyramidOwned?.Destroy();
            _hiZDepthPyramidOwned = null;
            _hiZDepthPyramid = null;
        }

        private void DisposeShaders()
        {
            _cullingComputeShader?.Destroy();
            _buildKeysComputeShader?.Destroy();
            _buildGpuBatchesComputeShader?.Destroy();
            _indirectRenderTaskShader?.Destroy();
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
