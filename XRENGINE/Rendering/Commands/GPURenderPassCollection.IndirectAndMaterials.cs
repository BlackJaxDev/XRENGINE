using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Lists.Unsafe;
using XREngine.Scene;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        /// <summary>
        /// Renders this pass using indirect rendering fully on-GPU.
        /// </summary>
        /// <param name="scene"></param>
        public void Render(GPUScene scene)
        {
            Dbg("Render begin", "Lifecycle");

            PreRenderInitialize(scene);

            if (_indirectRenderTaskShader is null || _indirectDrawBuffer is null)
            {
                Dbg("Render abort - shaders or draw buffer null", "Lifecycle");
                return;
            }

            XRCamera? camera = Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.SceneCamera;
            if (camera is null)
            {
                Dbg("Render abort - no camera", "Lifecycle");
                return;
            }

            if (Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingScene is VisualScene3D visualScene)
                visualScene.PrepareGpuCulling();

            ResetCounters();

            // Cull visible commands for this pass
            Cull(scene, camera);

            if (VisibleCommandCount == 0)
            {
                Dbg("Render early-out - visible=0", "Lifecycle");
                return;
            }

            PopulateMaterialIDs(scene);

            BuildIndirectCommandBuffer(scene);
            Dbg("Indirect build complete", "Indirect");

            List<HybridRenderingManager.DrawBatch> batches = BuildMaterialBatches(scene) ?? [new HybridRenderingManager.DrawBatch(0, VisibleCommandCount, 0)];
            CurrentBatches = batches;

            _renderManager.Render(this, camera, scene, _indirectDrawBuffer, _indirectRenderer, RenderPass, _drawCountBuffer, batches);
            Dbg("Render submission done", "Lifecycle");

            _useBufferAForRender = !_useBufferAForRender; // swap

            if (_cullingOverflowFlagBuffer != null && _indirectOverflowFlagBuffer != null && _truncationFlagBuffer != null)
            {
                uint cullOv = ReadUInt(_cullingOverflowFlagBuffer);
                uint indOv = ReadUInt(_indirectOverflowFlagBuffer);
                uint trunc = ReadUInt(_truncationFlagBuffer);

                if (cullOv != 0 || indOv != 0 || trunc != 0)
                {
                    Debug.LogWarning($"{FormatDebugPrefix("Stats")} GPU Render Overflow: Culling={cullOv} Indirect={indOv} Trunc={trunc}");
                    Dbg($"Overflow flags cull={cullOv} indirect={indOv} trunc={trunc}", "Stats");
                }

                if (Engine.EffectiveSettings.EnableGpuIndirectValidationLogging && _culledSceneToRenderBuffer is not null)
                {
                    if (cullOv > 0 && Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                        Debug.Out($"{FormatDebugPrefix("Validation")} Culling overflow count={cullOv} (capacity={_culledSceneToRenderBuffer.ElementCount}, visible={VisibleCommandCount})");

                    if (_culledSceneToRenderBuffer.ElementCount > 0)
                    {
                        GPUIndirectRenderCommand tail = _culledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(_culledSceneToRenderBuffer.ElementCount - 1);
                        if (tail.MeshID == uint.MaxValue || tail.MaterialID == uint.MaxValue)
                        {
                            Debug.Out($"{FormatDebugPrefix("Validation")} Overflow sentinel detected at tail of culled buffer (mesh={tail.MeshID} material={tail.MaterialID})");
                        }
                    }
                }
            }

            if (_statsBuffer != null)
            {
                Span<uint> values = stackalloc uint[5];
                ReadUints(_statsBuffer, values);

                uint input = values[0];
                uint culled = values[1];
                uint drawn = values[2];
                uint frustumRej = values[3];
                uint distRej = values[4];

                if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                    Debug.Out($"{FormatDebugPrefix("Stats")} [GPU Stats] In={input} CulledOut={culled} Draws={drawn} RejFrustum={frustumRej} RejDist={distRej}");
                Dbg($"Stats in={input} culled={culled} draws={drawn} frustumRej={frustumRej} distRej={distRej}", "Stats");
            }
            Dbg("Render end", "Lifecycle");
        }

        private void ResetCounters()
        {
            if (_resetCountersComputeShader is null ||
                _culledCountBuffer is null ||
                _drawCountBuffer is null)
                return;

            Dbg("Reset counters dispatch", "Lifecycle");

            _resetCountersComputeShader.BindBuffer(_culledCountBuffer, 0);
            _resetCountersComputeShader.BindBuffer(_drawCountBuffer, 1);

            if (_cullingOverflowFlagBuffer != null)
                _resetCountersComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 2);
            if (_indirectOverflowFlagBuffer != null)
                _resetCountersComputeShader.BindBuffer(_indirectOverflowFlagBuffer, 3);
            if (_truncationFlagBuffer != null)
                _resetCountersComputeShader.BindBuffer(_truncationFlagBuffer, 4);
            if (_statsBuffer != null)
                _resetCountersComputeShader.BindBuffer(_statsBuffer, 8);

            _resetCountersComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
        }

        private void BuildIndirectCommandBuffer(GPUScene scene)
        {
            Dbg("BuildIndirect begin", "Indirect");

            if (_indirectRenderTaskShader is null || _indirectDrawBuffer is null)
            {
                Dbg("BuildIndirect abort - shaders or draw buffer null", "Indirect");
                return;
            }

            _indirectRenderTaskShader.Uniform("CurrentRenderPass", RenderPass);
            _indirectRenderTaskShader.Uniform("MaxIndirectDraws", (int)_indirectDrawBuffer.ElementCount);
            _indirectRenderTaskShader.Uniform("AtlasAll16Bit", 0);

            CulledSceneToRenderBuffer.BindTo(_indirectRenderTaskShader, 0);
            _indirectDrawBuffer.BindTo(_indirectRenderTaskShader, 1);
            scene.MeshDataBuffer.BindTo(_indirectRenderTaskShader, 2);
            _culledCountBuffer?.BindTo(_indirectRenderTaskShader, 3);
            _drawCountBuffer?.BindTo(_indirectRenderTaskShader, 4);
            _indirectOverflowFlagBuffer?.BindTo(_indirectRenderTaskShader, 5);

            if (_truncationFlagBuffer is not null)
            {
                _truncationFlagBuffer.SetDataRawAtIndex(0, 0u);
                _truncationFlagBuffer.PushSubData();
                _truncationFlagBuffer.BindTo(_indirectRenderTaskShader, 7);
            }

            bool statsEnabled = _statsBuffer is not null;
            if (statsEnabled)
                _statsBuffer!.BindTo(_indirectRenderTaskShader, 8);

            _indirectRenderTaskShader.Uniform("StatsEnabled", statsEnabled ? 1u : 0u);

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(VisibleCommandCount);
            if (x <= 0)
                x = 1;

            _indirectRenderTaskShader.DispatchCompute(x, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"Indirect dispatch groups={x} visible={VisibleCommandCount}", "Indirect");
            //ReadDrawCount();
        }

        //private void ReadDrawCount()
        //{
        //    if (_drawCountBuffer is null || _culledCountBuffer is null)
        //    {
        //        Dbg("ReadDrawCount abort - count buffers null", "Indirect");
        //        return;
        //    }

        //    bool allowCpuReadback = !IndirectDebug.DisableCpuReadbackCount || IndirectDebug.ForceCpuFallbackCount;
        //    if (!allowCpuReadback)
        //        return;

        //    if (IndirectDebug.ForceCpuFallbackCount)
        //    {
        //        WriteUInt(_drawCountBuffer, VisibleCommandCount);
        //        Dbg("Indirect count forced to CPU fallback", "Indirect");
        //        return;
        //    }

        //    uint drawReported = ReadUInt(_drawCountBuffer);

        //    if (IndirectDebug.LogCountBufferWrites)
        //        Debug.Out($"{FormatDebugPrefix("Indirect")} [Indirect/Count] GPU reported {drawReported} visible={VisibleCommandCount}");

        //    if (IndirectDebug.DumpIndirectArguments)
        //        DumpIndirectSummary(drawReported);

        //    if (drawReported == 0 && VisibleCommandCount > 0 && !IndirectDebug.DisableCpuReadbackCount)
        //    {
        //        WriteUInt(_drawCountBuffer, VisibleCommandCount);
        //        Dbg("Indirect CPU fallback set draw count", "Indirect");
        //    }
        //}

        private void DumpIndirectSummary(uint drawReported)
        {
            uint sampleCount = drawReported == 0 ? VisibleCommandCount : drawReported;
            sampleCount = Math.Min(sampleCount, 8u);

            string prefix = FormatDebugPrefix("Indirect");
            var message = new StringBuilder()
                .AppendLine($"{prefix} [Indirect/Dump] drawReported={drawReported} visible={VisibleCommandCount} batches={CurrentBatches?.Count ?? 0}")
                .AppendLine($"  CountBufferMapped={(_drawCountBuffer?.ActivelyMapping.Count > 0)} CulledBufferMapped={(_culledCountBuffer?.ActivelyMapping.Count > 0)}")
                .AppendLine($"  SampleCount={sampleCount}");

            if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                Debug.Out(message.ToString());
        }

        /// <summary>
        /// Collects all material IDs from the scene's commands into a dedicated buffer.
        /// </summary>
        /// <param name="scene"></param>
        private void PopulateMaterialIDs(GPUScene scene)
        {
            if (_materialIDsBuffer is null)
                return;

            uint count = scene.TotalCommandCount;
            if (count == 0)
                return;

            Dbg($"PopulateMaterialIDs count={count}", "Materials");

            bool loggedSentinel = false;

            for (uint i = 0; i < count; i++)
            {
                var cmd = scene.AllLoadedCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                if (!loggedSentinel && cmd.MaterialID == uint.MaxValue)
                {
                    Dbg($"PopulateMaterialIDs source materialID=uint.MaxValue @sceneIndex={i} mesh={cmd.MeshID}", "Materials");
                    loggedSentinel = true;
                }
                _materialIDsBuffer.SetDataRawAtIndex(i, cmd.MaterialID);
            }
        }

        /// <summary>
        /// Creates draw batches based on material IDs.
        /// </summary>
        /// <param name="scene"></param>
        /// <returns></returns>
        private List<HybridRenderingManager.DrawBatch>? BuildMaterialBatches(GPUScene scene)
        {
            uint count = VisibleCommandCount;
            if (count == 0)
                return null;

            Dbg($"BuildMaterialBatches count={count}", "Materials");

            List<HybridRenderingManager.DrawBatch> batches = new((int)Math.Min(count, 64));

            bool mappedCulledHere = false;
            VoidPtr culledPtr = default;
            uint culledStride = 0;

            if (_culledSceneToRenderBuffer is not null)
            {
                try
                {
                    if (_culledSceneToRenderBuffer.ActivelyMapping.Count == 0)
                    {
                        _culledSceneToRenderBuffer.StorageFlags |= EBufferMapStorageFlags.Read;
                        _culledSceneToRenderBuffer.RangeFlags |= EBufferMapRangeFlags.Read;
                        _culledSceneToRenderBuffer.MapBufferData();
                        mappedCulledHere = true;
                    }

                    culledPtr = _culledSceneToRenderBuffer
                        .GetMappedAddresses()
                        .FirstOrDefault(ptr => ptr.IsValid);

                    if (culledPtr.IsValid)
                    {
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                        culledStride = _culledSceneToRenderBuffer.ElementSize;
                        if (culledStride == 0)
                            culledStride = GPUScene.CommandFloatCount * sizeof(float);
                    }
                    else if (mappedCulledHere)
                    {
                        _culledSceneToRenderBuffer.UnmapBufferData();
                        mappedCulledHere = false;
                    }
                }
                catch (Exception ex)
                {
                    Dbg($"BuildMaterialBatches map failed: {ex.Message}", "Materials");
                    if (mappedCulledHere)
                    {
                        _culledSceneToRenderBuffer.UnmapBufferData();
                        mappedCulledHere = false;
                    }
                    culledPtr = default;
                    culledStride = 0;
                }
            }

            IntPtr culledBasePtr = IntPtr.Zero;
            if (culledPtr.IsValid)
            {
                unsafe
                {
                    culledBasePtr = (IntPtr)culledPtr.Pointer;
                }
            }

            uint currentMaterial = uint.MaxValue;
            uint batchStart = 0;
            uint batchCount = 0;

            for (uint i = 0; i < count; ++i)
            {
                uint materialId;

                if (culledBasePtr != IntPtr.Zero)
                {
                    GPUIndirectRenderCommand culledCmd;
                    unsafe
                    {
                        byte* basePtr = (byte*)culledBasePtr + (i * culledStride);
                        culledCmd = Unsafe.ReadUnaligned<GPUIndirectRenderCommand>(basePtr);
                    }
                    if (TryValidateMaterialId(scene, culledCmd.MaterialID, "culled buffer"))
                    {
                        materialId = culledCmd.MaterialID;
                    }
                    else
                    {
                        materialId = ResolveMaterialId(scene, i);
                    }
                }
                else
                {
                    materialId = ResolveMaterialId(scene, i);
                }

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

            if (mappedCulledHere)
                _culledSceneToRenderBuffer!.UnmapBufferData();

            if (batches.Count == 0)
                return null;

            LogMaterialBatches(scene, batches);
            return batches;
        }

        private int _resolveMaterialLogBudget = 16;

        private bool TryValidateMaterialId(GPUScene scene, uint sourceId, string sourceName)
        {
            if (sourceId == 0)
                return false;

            if (scene.MaterialMap.ContainsKey(sourceId))
                return true;

            int remaining = Interlocked.Decrement(ref _resolveMaterialLogBudget);
            if (remaining >= 0)
            {
                Dbg($"ResolveMaterialId rejected id={sourceId} from {sourceName} (not present in MaterialMap). Remaining logs: {remaining}", "Materials");
                if (remaining == 0)
                    Dbg("ResolveMaterialId rejection log budget exhausted; suppressing further logs.", "Materials");
            }
            return false;
        }

        private uint ResolveMaterialId(GPUScene scene, uint visibleIndex)
        {
            try
            {
                if (_culledSceneToRenderBuffer is not null && visibleIndex < _culledSceneToRenderBuffer.ElementCount)
                {
                    GPUIndirectRenderCommand culledCmd = _culledSceneToRenderBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(visibleIndex);
                    if (TryValidateMaterialId(scene, culledCmd.MaterialID, "culled buffer"))
                    {
                        if (culledCmd.MaterialID == uint.MaxValue)
                            Dbg($"ResolveMaterialId detected sentinel materialID=uint.MaxValue from culled buffer @idx={visibleIndex} mesh={culledCmd.MeshID}", "Materials");
                        return culledCmd.MaterialID;
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg($"ResolveMaterialId culled buffer read failed idx={visibleIndex} ex={ex.Message}", "Materials");
            }

            try
            {
                if (_materialIDsBuffer is not null && visibleIndex < _materialIDsBuffer.ElementCount)
                {
                    uint id = _materialIDsBuffer.GetDataRawAtIndex<uint>(visibleIndex);
                    if (TryValidateMaterialId(scene, id, "material buffer"))
                    {
                        if (id == uint.MaxValue)
                            Dbg($"ResolveMaterialId detected sentinel materialID=uint.MaxValue from material buffer @idx={visibleIndex}", "Materials");
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Dbg($"ResolveMaterialId material buffer read failed idx={visibleIndex} ex={ex.Message}", "Materials");
            }

            try
            {
                XRDataBuffer commands = scene.AllLoadedCommandsBuffer;
                if (visibleIndex < commands.ElementCount)
                {
                    GPUIndirectRenderCommand cmd = commands.GetDataRawAtIndex<GPUIndirectRenderCommand>(visibleIndex);
                    if (TryValidateMaterialId(scene, cmd.MaterialID, "scene command buffer"))
                    {
                        if (cmd.MaterialID == uint.MaxValue)
                            Dbg($"ResolveMaterialId detected sentinel materialID=uint.MaxValue from scene commands @idx={visibleIndex}", "Materials");
                        return cmd.MaterialID;
                    }
                    if (cmd.MaterialID == uint.MaxValue)
                        Dbg($"ResolveMaterialId detected sentinel materialID=uint.MaxValue from scene commands @idx={visibleIndex}", "Materials");
                }
            }
            catch (Exception ex)
            {
                Dbg($"ResolveMaterialId fallback read failed idx={visibleIndex} ex={ex.Message}", "Materials");
            }

            return 0;
        }

        private void LogMaterialBatches(GPUScene scene, List<HybridRenderingManager.DrawBatch> batches)
        {
            IReadOnlyDictionary<uint, XRMaterial> materialMap = scene.MaterialMap;

            var sb = new StringBuilder();
            sb.Append($"BuildMaterialBatches produced {batches.Count} batches:");

            for (int i = 0; i < batches.Count; i++)
            {
                HybridRenderingManager.DrawBatch batch = batches[i];
                string materialName = materialMap.TryGetValue(batch.MaterialID, out XRMaterial? mat) && mat is not null
                    ? (mat.Name ?? $"Material#{batch.MaterialID}")
                    : (batch.MaterialID == 0 ? "<Invalid>" : $"Material#{batch.MaterialID}");

                sb.Append($" [#{i}] {materialName} -> {batch.Count} draws");
            }

            Dbg(sb.ToString(), "Materials");
        }

        public void Dispose()
        {
            Dbg("Dispose invoked","Lifecycle");

            if (_disposed)
                return;

            using (_lock.EnterScope())
            {
                _indirectDrawBuffer?.Dispose();
                _culledCountBuffer?.Dispose();
                _drawCountBuffer?.Dispose();
                _cullingOverflowFlagBuffer?.Dispose();
                _indirectOverflowFlagBuffer?.Dispose();
                _sortedCommandBuffer?.Dispose();
                _culledSceneToRenderBuffer?.Dispose();
                _passFilterDebugBuffer?.Dispose();
                _materialIDsBuffer?.Dispose();
                _cullingComputeShader?.Destroy();
                _indirectRenderTaskShader?.Destroy();
                _indirectRenderer?.Destroy();
                _buffersMapped = false;
                _initialized = false; _disposed = true;
            }

            Dbg("Dispose complete","Lifecycle");
        }
    }
}
