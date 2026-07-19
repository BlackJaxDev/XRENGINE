using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Vectors;
using XREngine.Data.Rendering;
using XREngine.Data.Lists.Unsafe;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
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
        private static bool VulkanCounterDiagnosticsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanCounterDiagnostics), "1", StringComparison.OrdinalIgnoreCase);
        private static bool VulkanDelayedCounterDiagnosticsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanIndirectTrace), "1", StringComparison.OrdinalIgnoreCase);
        private int _resolveMaterialLogBudget = 16;
        private readonly HashSet<uint> _lastMaterialTableIds = [];
        private int _materialResidencyLogBudget = 12;

        private enum EMaterialTextureReferenceBuildMode
        {
            None = 0,
            OpenGLBindlessHandles,
            VulkanDescriptorIndices,
        }
        
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
            GpuProgramsPendingThisFrame = false;
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render begin (pass={0})", RenderPass);
            Dbg("Render begin", "Lifecycle");

            if (!TryInitializeRender(scene, out XRCamera? camera) || camera is null)
            {
                ClearPassPolicySnapshot();
                return;
            }

            if (!TryPrepareGpuPrograms())
            {
                GpuProgramsPendingThisFrame = true;
                ClearPassPolicySnapshot();
                return;
            }

            // Meshlet debug display force-flip:
            // The post-process MeshletDebugDisplayEnabled toggle requires the production meshlet
            // dispatch path so the generated meshlet fragment shader can write FragMeshletDebugColor.
            // When the camera's default strategy is non-meshlet (e.g. GpuIndirectZeroReadback) we
            // override MeshSubmissionStrategy/UseMeshletPipeline for this pass *before* the meshlet
            // expansion gate runs. Restored in finally so the override never bleeds into other passes
            // or subsequent frames.
            EMeshSubmissionStrategy savedStrategy = MeshSubmissionStrategy;
            bool savedUseMeshletPipeline = UseMeshletPipeline;
            bool meshletDebugForced =
                savedStrategy != EMeshSubmissionStrategy.CpuDirect &&
                !savedStrategy.IsAnyMeshletStrategy() &&
                GpuBvhDebugSettings.ShouldForceMeshletForDebugDisplay(camera, RenderPass);

            if (meshletDebugForced)
            {
                MeshSubmissionStrategy = ResolveMeshletDebugDisplayStrategy();
                UseMeshletPipeline = true;
            }

            try
            {
            using var renderGraphPassScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(RenderPass);
            _gpuBatchingPreparedThisFrame = false;
            _zeroReadbackMaterialScatterPreparedThisFrame = false;
            _zeroReadbackActiveBucketListPreparedThisFrame = false;
            _meshletExpansionPreparedThisFrame = false;
            ResetZeroReadbackProgramPendingState();
            Stopwatch resetStopwatch = Stopwatch.StartNew();
            ResetCounters();
            resetStopwatch.Stop();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Reset,
                resetStopwatch.Elapsed);

            Cull(scene, camera);
            LogVulkanCounterDiagnostics("after-cull");
            LogVulkanCullInputDiagnostics(scene, "after-cull");
            SelectVisibleCommandLods(scene, camera);
            ExpandVisibleMeshlets(scene);
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
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                    RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Indirect,
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
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Draw,
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
            finally
            {
                if (meshletDebugForced)
                {
                    MeshSubmissionStrategy = savedStrategy;
                    UseMeshletPipeline = savedUseMeshletPipeline;
                }
            }
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

            camera = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingCamera
                ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.SceneCamera;
            if (camera is null)
            {
                Dbg("Render abort - no camera", "Lifecycle");
                return false;
            }

            if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingScene is VisualScene3D visualScene)
                visualScene.PrepareGpuCulling();

            return true;
        }

        private static EMeshSubmissionStrategy ResolveMeshletDebugDisplayStrategy()
            => RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging
                ? EMeshSubmissionStrategy.GpuMeshletInstrumented
                : EMeshSubmissionStrategy.GpuMeshletZeroReadback;

        #endregion

        #region Counter Management

        private void ResetCounters()
        {
            ResetVisibleCounters();

            if (_culledCountBuffer is null ||
                _drawCountBuffer is null ||
                _cullCountScratchBuffer is null)
            {
                Dbg($"Reset counters abort - missing base buffers: {DescribeMissingResetCounterBuffers(baseOnly: true)}", "Lifecycle");
                return;
            }

            if (_resetCountersComputeShader is null ||
                _cullingOverflowFlagBuffer is null ||
                _indirectOverflowFlagBuffer is null ||
                _truncationFlagBuffer is null ||
                _statsBuffer is null ||
                _gpuBatchCountBuffer is null ||
                _visibleMeshletTaskCountBuffer is null ||
                _meshletDispatchIndirectBuffer is null ||
                _meshletDispatchCountBuffer is null ||
                _meshletExpansionOverflowFlagBuffer is null)
            {
                Dbg($"Reset counters fallback - full shader contract unavailable: {DescribeMissingResetCounterBuffers(baseOnly: false)}", "Lifecycle");
                ResetBaseCountersOnCpu();
                LogVulkanCounterDiagnostics("after-reset-cpu-fallback");
                return;
            }

            Dbg("Reset counters dispatch", "Lifecycle");

            BindStorageBuffer(_resetCountersComputeShader, _culledCountBuffer, 0);
            BindStorageBuffer(_resetCountersComputeShader, _drawCountBuffer, 1);
            _resetCountersComputeShader.BindBuffer(_cullingOverflowFlagBuffer, 2);
            _resetCountersComputeShader.BindBuffer(_indirectOverflowFlagBuffer, 3);
            _resetCountersComputeShader.BindBuffer(_truncationFlagBuffer, 4);
            BindStorageBuffer(_resetCountersComputeShader, _cullCountScratchBuffer, 6);
            _resetCountersComputeShader.BindBuffer(_statsBuffer, 8);
            _resetCountersComputeShader.BindBuffer(_gpuBatchCountBuffer, 9);
            BindStorageBuffer(_resetCountersComputeShader, _visibleMeshletTaskCountBuffer, 10);
            _resetCountersComputeShader.BindBuffer(_meshletDispatchIndirectBuffer, 11);
            _resetCountersComputeShader.BindBuffer(_meshletExpansionOverflowFlagBuffer, 12);
            _resetCountersComputeShader.BindBuffer(_meshletDispatchCountBuffer, 14);

            _resetCountersComputeShader.DispatchCompute(1, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            ResetCountersHook?.Invoke();
            ResetPerViewDrawCounts(_activeViewCount);

            if (_occlusionOverflowFlagBuffer is not null)
                WriteUInt(_occlusionOverflowFlagBuffer, 0u);

            LogVulkanCounterDiagnostics("after-reset");
        }

        private string DescribeMissingResetCounterBuffers(bool baseOnly)
        {
            StringBuilder builder = new();
            AppendMissing(builder, _culledCountBuffer, nameof(_culledCountBuffer));
            AppendMissing(builder, _drawCountBuffer, nameof(_drawCountBuffer));
            AppendMissing(builder, _cullCountScratchBuffer, nameof(_cullCountScratchBuffer));

            if (!baseOnly)
            {
                AppendMissing(builder, _resetCountersComputeShader, nameof(_resetCountersComputeShader));
                AppendMissing(builder, _cullingOverflowFlagBuffer, nameof(_cullingOverflowFlagBuffer));
                AppendMissing(builder, _indirectOverflowFlagBuffer, nameof(_indirectOverflowFlagBuffer));
                AppendMissing(builder, _truncationFlagBuffer, nameof(_truncationFlagBuffer));
                AppendMissing(builder, _statsBuffer, nameof(_statsBuffer));
                AppendMissing(builder, _gpuBatchCountBuffer, nameof(_gpuBatchCountBuffer));
                AppendMissing(builder, _visibleMeshletTaskCountBuffer, nameof(_visibleMeshletTaskCountBuffer));
                AppendMissing(builder, _meshletDispatchIndirectBuffer, nameof(_meshletDispatchIndirectBuffer));
                AppendMissing(builder, _meshletDispatchCountBuffer, nameof(_meshletDispatchCountBuffer));
                AppendMissing(builder, _meshletExpansionOverflowFlagBuffer, nameof(_meshletExpansionOverflowFlagBuffer));
            }

            return builder.Length == 0 ? "<none>" : builder.ToString();

            static void AppendMissing(StringBuilder builder, object? value, string name)
            {
                if (value is not null)
                    return;

                if (builder.Length > 0)
                    builder.Append(',');
                builder.Append(name);
            }
        }

        private void ResetBaseCountersOnCpu()
        {
            if (_culledCountBuffer is not null)
            {
                for (uint i = 0u; i < GPUScene.VisibleCountComponents; i++)
                    WriteUIntAt(_culledCountBuffer, i, 0u);
            }

            if (_cullCountScratchBuffer is not null)
            {
                for (uint i = 0u; i < GPUScene.VisibleCountComponents; i++)
                    WriteUIntAt(_cullCountScratchBuffer, i, 0u);
            }

            if (_drawCountBuffer is not null)
                WriteUInt(_drawCountBuffer, 0u);
            if (_cullingOverflowFlagBuffer is not null)
                WriteUInt(_cullingOverflowFlagBuffer, 0u);
            if (_indirectOverflowFlagBuffer is not null)
                WriteUInt(_indirectOverflowFlagBuffer, 0u);
            if (_truncationFlagBuffer is not null)
                WriteUInt(_truncationFlagBuffer, 0u);
            if (_occlusionOverflowFlagBuffer is not null)
                WriteUInt(_occlusionOverflowFlagBuffer, 0u);

            ResetPerViewDrawCounts(_activeViewCount);
            ResetCountersHook?.Invoke();
        }

        #endregion

        #region Indirect Command Building

        private void BuildIndirectCommandBuffer(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.BuildIndirectCommandBuffer");

            Dbg("BuildIndirect begin", "Indirect");

            if (_indirectRenderTaskShader is null || _indirectDrawBuffer is null)
            {
                Dbg($"BuildIndirect abort - missing shader/draw resources: {DescribeMissingBuildIndirectBuffers(shaderOnly: true)}", "Indirect");
                return;
            }

            if (_culledCountBuffer is null ||
                _drawCountBuffer is null ||
                _indirectOverflowFlagBuffer is null ||
                _truncationFlagBuffer is null ||
                _statsBuffer is null ||
                CulledSceneToRenderBuffer is null)
            {
                Dbg($"BuildIndirect abort - missing required buffers: {DescribeMissingBuildIndirectBuffers(shaderOnly: false)}", "Indirect");
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
            using (BvhGpuProfiler.Instance.SubmissionScope(BvhGpuProfiler.Stage.CommandEmission))
            using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.CommandEmission, dispatchCommands))
                _indirectRenderTaskShader.DispatchCompute(dispatchGroups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            Dbg($"Indirect dispatch groups={dispatchGroups} visible={VisibleCommandCount}", "Indirect");
            LogVulkanCounterDiagnostics("after-build");
        }

        private void LogVulkanCounterDiagnostics(string point)
        {
            if (!VulkanCounterDiagnosticsEnabled)
                return;

            string culledDraw = DescribeCounter(_culledCountBuffer, GPUScene.VisibleCountDrawIndex);
            string culledInstances = DescribeCounter(_culledCountBuffer, GPUScene.VisibleCountInstanceIndex);
            string culledOverflow = DescribeCounter(_culledCountBuffer, GPUScene.VisibleCountOverflowIndex);
            string drawCount = DescribeCounter(_drawCountBuffer, 0u);
            string materialBuckets = DescribeMaterialTierCountSample();

            Debug.VulkanEvery(
                $"VulkanCounters.{RuntimeHelpers.GetHashCode(this)}.{RenderPass}.{point}",
                TimeSpan.FromMilliseconds(250),
                "[VulkanCounters] pass={0} point={1} cpuVisible={2} cpuInstances={3} upperBoundValid={4} upperBound={5} culledDraw={6} culledInstances={7} culledOverflow={8} drawCount0={9} materialBuckets={10}",
                RenderPass,
                point,
                VisibleCommandCount,
                VisibleInstanceCount,
                _visibleCommandUpperBoundValid,
                _visibleCommandUpperBound,
                culledDraw,
                culledInstances,
                culledOverflow,
                drawCount,
                materialBuckets);
        }

        private void LogVulkanCullInputDiagnostics(GPUScene scene, string point)
        {
            if (!VulkanCounterDiagnosticsEnabled)
                return;

            XRDataBuffer commandBuffer = scene.AllLoadedCommandsBuffer;
            XRDataBuffer metadataBuffer = scene.DrawMetadataBuffer;
            uint inputCount = Math.Min(scene.TotalCommandCount, commandBuffer.ElementCount);
            uint targetPass = unchecked((uint)RenderPass);
            bool matchAll = RenderPass < 0;
            uint commandPassMatches = 0u;
            uint metadataPassMatches = 0u;
            uint commandMetadataPassMismatch = 0u;
            uint materialKnown = 0u;
            uint meshKnown = 0u;
            uint zeroInstances = 0u;
            uint sampled = Math.Min(inputCount, 8u);
            StringBuilder sample = new();

            for (uint i = 0u; i < inputCount; i++)
            {
                GPUIndirectRenderCommand command;
                DrawMetadata metadata;
                try
                {
                    command = commandBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(i);
                    metadata = i < metadataBuffer.ElementCount
                        ? metadataBuffer.GetDataRawAtIndex<DrawMetadata>(i)
                        : default;
                }
                catch (Exception ex)
                {
                    Debug.VulkanWarningEvery(
                        $"VulkanCounters.CullInput.ReadFailed.{RuntimeHelpers.GetHashCode(this)}.{RenderPass}",
                        TimeSpan.FromSeconds(2),
                        "[VulkanCounters] point={0} pass={1} failed to sample cull input at index={2}: {3}: {4}",
                        point,
                        RenderPass,
                        i,
                        ex.GetType().Name,
                        ex.Message);
                    break;
                }

                bool commandPassMatch = matchAll || command.RenderPass == targetPass || command.RenderPass == uint.MaxValue;
                bool metadataPassMatch = matchAll || metadata.RenderPass == targetPass || metadata.RenderPass == uint.MaxValue;
                if (commandPassMatch)
                    commandPassMatches++;
                if (metadataPassMatch)
                    metadataPassMatches++;
                if (command.RenderPass != metadata.RenderPass)
                    commandMetadataPassMismatch++;
                if (scene.MaterialMap.ContainsKey(command.MaterialID))
                    materialKnown++;
                if (scene.TryGetMeshDataEntry(command.MeshID, out GPUScene.MeshDataEntry meshEntry) && meshEntry.IndexCount != 0u)
                    meshKnown++;
                if (command.InstanceCount == 0u)
                    zeroInstances++;

                if (i >= sampled)
                    continue;

                if (sample.Length > 0)
                    sample.Append(" | ");
                sample.Append('#').Append(i)
                    .Append(" cmdPass=").Append(command.RenderPass)
                    .Append(" metaPass=").Append(metadata.RenderPass)
                    .Append(" mat=").Append(command.MaterialID)
                    .Append(scene.MaterialMap.ContainsKey(command.MaterialID) ? ":ok" : ":missing")
                    .Append(" mesh=").Append(command.MeshID)
                    .Append(meshEntry.IndexCount != 0u ? ":ok" : ":missing")
                    .Append(" inst=").Append(command.InstanceCount)
                    .Append(" bounds=").Append(command.BoundsID);
            }

            Debug.VulkanEvery(
                $"VulkanCounters.CullInput.{RuntimeHelpers.GetHashCode(this)}.{RenderPass}.{point}",
                TimeSpan.FromMilliseconds(250),
                "[VulkanCounters] point={0} pass={1} cullInput total={2} commandPassMatches={3} metadataPassMatches={4} commandMetadataPassMismatch={5} materialKnown={6} meshKnown={7} zeroInstances={8} bvhReady={9} bvhNodes={10} bvhProvider={11} sample={12}",
                point,
                RenderPass,
                inputCount,
                commandPassMatches,
                metadataPassMatches,
                commandMetadataPassMismatch,
                materialKnown,
                meshKnown,
                zeroInstances,
                scene.BvhProvider?.IsBvhReady ?? false,
                scene.BvhProvider?.BvhNodeCount ?? 0u,
                scene.BvhProvider?.GetType().Name ?? "<none>",
                sample.Length == 0 ? "<none>" : sample.ToString());
        }

        private string DescribeCounter(XRDataBuffer? buffer, uint index)
        {
            if (TryReadCounter(buffer, index, out uint value, out string reason))
                return value.ToString();

            return reason;
        }

        private bool TryReadCounter(XRDataBuffer? buffer, uint index, out uint value, out string reason)
        {
            value = 0u;
            reason = "<missing>";

            if (buffer is null)
                return false;

            if (index >= buffer.ElementCount)
            {
                reason = $"<out-of-range:{index}/{buffer.ElementCount}>";
                return false;
            }

            try
            {
                if (AbstractRenderer.Current is VulkanRenderer vulkanRenderer)
                {
                    uint byteOffset = checked(index * sizeof(uint));
                    Span<byte> bytes = stackalloc byte[sizeof(uint)];
                    if (!vulkanRenderer.TryReadBufferBytesForDiagnostics(buffer, byteOffset, bytes, out reason))
                        return false;

                    value = BitConverter.ToUInt32(bytes);
                    reason = "gpu";
                    return true;
                }

                value = ReadUIntAt(buffer, index);
                reason = "mapped";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"<{ex.GetType().Name}>";
                Debug.VulkanWarningEvery(
                    $"VulkanCounters.ReadFailed.{RuntimeHelpers.GetHashCode(buffer)}.{index}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanCounters] failed to read counter buffer='{0}' index={1}: {2}: {3}",
                    buffer.AttributeName ?? buffer.Target.ToString(),
                    index,
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
        }

        private string DescribeMaterialTierCountSample()
        {
            if (_materialTierDrawCountBuffer is null)
                return "drawCounts=<missing>";

            uint bucketCount = _materialTierBucketCount == 0u
                ? _materialTierDrawCountBuffer.ElementCount
                : Math.Min(_materialTierBucketCount, _materialTierDrawCountBuffer.ElementCount);
            if (bucketCount == 0u)
                return "drawCounts=<empty>";

            uint scanCount = Math.Min(bucketCount, 128u);
            uint nonZero = 0u;
            uint appended = 0u;
            StringBuilder firstNonZero = new();

            for (uint i = 0u; i < scanCount; ++i)
            {
                if (!TryReadCounter(_materialTierDrawCountBuffer, i, out uint count, out _))
                    continue;

                if (count == 0u)
                    continue;

                nonZero++;
                if (appended >= 8u)
                    continue;

                if (firstNonZero.Length > 0)
                    firstNonZero.Append(',');
                firstNonZero.Append(i).Append(':').Append(count);
                appended++;
            }

            string activeBucketCount = DescribeCounter(_materialTierActiveBucketCountBuffer, 0u);
            string sample = firstNonZero.Length == 0 ? "<none>" : firstNonZero.ToString();
            return $"bucketCount={bucketCount} scan={scanCount} nonZero={nonZero} firstNonZero={sample} activeCount={activeBucketCount}";
        }

        private string DescribeMissingBuildIndirectBuffers(bool shaderOnly)
        {
            StringBuilder builder = new();
            AppendMissing(builder, _indirectRenderTaskShader, nameof(_indirectRenderTaskShader));
            AppendMissing(builder, _indirectDrawBuffer, nameof(_indirectDrawBuffer));

            if (!shaderOnly)
            {
                AppendMissing(builder, _culledCountBuffer, nameof(_culledCountBuffer));
                AppendMissing(builder, _drawCountBuffer, nameof(_drawCountBuffer));
                AppendMissing(builder, _indirectOverflowFlagBuffer, nameof(_indirectOverflowFlagBuffer));
                AppendMissing(builder, _truncationFlagBuffer, nameof(_truncationFlagBuffer));
                AppendMissing(builder, _statsBuffer, nameof(_statsBuffer));
                AppendMissing(builder, CulledSceneToRenderBuffer, nameof(CulledSceneToRenderBuffer));
            }

            return builder.Length == 0 ? "<none>" : builder.ToString();

            static void AppendMissing(StringBuilder builder, object? value, string name)
            {
                if (value is not null)
                    return;

                if (builder.Length > 0)
                    builder.Append(',');
                builder.Append(name);
            }
        }

        private void SelectVisibleCommandLods(GPUScene scene, XRCamera camera)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.SelectVisibleCommandLods");

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
            _lodSelectComputeShader.Uniform("ProjectionScale", ResolveLodProjectionScale(camera));
            _lodSelectComputeShader.Uniform("ViewportSize", ResolveLodViewportSize());
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
            scene.MarkLodTransitionBufferGpuWritten();
            _culledHotCommandsValid = false;

            // Turn GPU-raised LOD residency requests (from earlier frames) into atlas loads.
            // Internally frame-throttled and a no-op unless StreamMeshLodsOnDemand is enabled.
            scene.ServiceLodStreamingRequests();
        }

        private static Vector2 ResolveLodProjectionScale(XRCamera camera)
        {
            bool useUnjitteredProjection = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            Matrix4x4 projection = useUnjitteredProjection ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            return new Vector2(MathF.Abs(projection.M11), MathF.Abs(projection.M22));
        }

        private static Vector2 ResolveLodViewportSize()
        {
            var renderArea = RuntimeEngine.Rendering.State.RenderArea;
            if (renderArea.Width > 0 && renderArea.Height > 0)
                return new Vector2(renderArea.Width, renderArea.Height);

            XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport
                ?? pipeline?.LastWindowViewport;

            if (viewport is not null)
            {
                int width = viewport.InternalWidth > 0 ? viewport.InternalWidth : viewport.Width;
                int height = viewport.InternalHeight > 0 ? viewport.InternalHeight : viewport.Height;
                return new Vector2(Math.Max(width, 1), Math.Max(height, 1));
            }

            return Vector2.One;
        }

        private void ExpandVisibleMeshlets(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuMeshlet.ExpandVisibleMeshlets");

            if (!UseMeshletPipeline && !MeshSubmissionStrategy.IsAnyMeshletStrategy())
                return;

            if (_expandMeshletsComputeShader is null ||
                _visibleMeshletTaskBuffer is null ||
                _visibleMeshletTaskCountBuffer is null ||
                _meshletDispatchIndirectBuffer is null ||
                _meshletDispatchCountBuffer is null ||
                _meshletExpansionOverflowFlagBuffer is null)
            {
                LogMeshletDispatchSkipped("missing shader or output buffers", scene.TotalCommandCount);
                Dbg("Meshlet expansion skipped - missing shader or output buffers.", "Meshlet");
                return;
            }

            UpdateVisibleCountersFromBuffer();
            BuildCulledHotCommandBuffer();

            if (!TryGetMeshletExpansionInputs(scene, out GpuMeshletExpansionInputs inputs))
            {
                LogMeshletDispatchSkipped("input contract unavailable", scene.TotalCommandCount);
                Dbg("Meshlet expansion skipped - input contract unavailable.", "Meshlet");
                return;
            }

            uint dispatchCommands = Math.Min(inputs.VisibleCommandUpperBound, CommandCapacity);
            if (dispatchCommands == 0u || inputs.MeshletRangeBuffer.ElementCount == 0u)
            {
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletDispatchSkipped(1);
                return;
            }

            _expandMeshletsComputeShader.Uniform("InputCommandCount", (int)dispatchCommands);
            _expandMeshletsComputeShader.Uniform("MaxMeshletTaskRecords", (int)_visibleMeshletTaskBuffer.ElementCount);
            _expandMeshletsComputeShader.Uniform("UseHotCommands", inputs.UseHotCommandLayout ? 1 : 0);
            _expandMeshletsComputeShader.Uniform("ExpandPreviousLodTransitions", 1);

            BindStorageBuffer(_expandMeshletsComputeShader, inputs.VisibleCommandBuffer, (uint)GPUMeshletBindings.ExpandVisibleCommands);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.CulledCountBuffer, (uint)GPUMeshletBindings.ExpandCulledCount);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.DrawMetadataBuffer, (uint)GPUMeshletBindings.ExpandDrawMetadata);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.MeshDataBuffer, (uint)GPUMeshletBindings.ExpandMeshData);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.MeshletRangeBuffer, (uint)GPUMeshletBindings.ExpandMeshletRanges);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.MeshletDescriptorBuffer, (uint)GPUMeshletBindings.ExpandMeshletDescriptors);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.MeshletVertexIndexBuffer, (uint)GPUMeshletBindings.ExpandMeshletVertexIndices);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.MeshletTriangleIndexBuffer, (uint)GPUMeshletBindings.ExpandMeshletTriangleIndices);
            BindStorageBuffer(_expandMeshletsComputeShader, inputs.LodTransitionBuffer, (uint)GPUMeshletBindings.ExpandLodTransitions);
            BindStorageBuffer(_expandMeshletsComputeShader, _visibleMeshletTaskBuffer, (uint)GPUMeshletBindings.ExpandVisibleMeshletTasks);
            BindStorageBuffer(_expandMeshletsComputeShader, _visibleMeshletTaskCountBuffer, (uint)GPUMeshletBindings.ExpandMeshletTaskCount);
            BindStorageBuffer(_expandMeshletsComputeShader, _meshletDispatchIndirectBuffer, (uint)GPUMeshletBindings.ExpandDispatchIndirect);
            BindStorageBuffer(_expandMeshletsComputeShader, _meshletExpansionOverflowFlagBuffer, (uint)GPUMeshletBindings.ExpandOverflow);
            BindStorageBuffer(_expandMeshletsComputeShader, _meshletDispatchCountBuffer, (uint)GPUMeshletBindings.ExpandDispatchCount);

            if (inputs.UseHotCommandLayout && inputs.VisibleHotCommandBuffer is not null)
                BindStorageBuffer(_expandMeshletsComputeShader, inputs.VisibleHotCommandBuffer, (uint)GPUMeshletBindings.ExpandVisibleHotCommands);
            else
                BindStorageBuffer(_expandMeshletsComputeShader, inputs.VisibleCommandBuffer, (uint)GPUMeshletBindings.ExpandVisibleHotCommands);

            uint dispatchGroups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(dispatchCommands, MeshletExpansionLocalSizeX).Item1);
            const EMemoryBarrierMask postExpandBarrier = EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command;
            _expandMeshletsComputeShader.DispatchCompute(dispatchGroups, 1, 1, postExpandBarrier);
            AbstractRenderer.Current?.MemoryBarrier(postExpandBarrier);
            _meshletExpansionPreparedThisFrame = true;
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletBufferBytesResident(scene.MeshletBufferBytesResident);
            Dbg($"Meshlet expansion dispatch groups={dispatchGroups} commands={dispatchCommands} taskCapacity={_visibleMeshletTaskBuffer.ElementCount}", "Meshlet");
        }

        private void LogMeshletDispatchSkipped(string reason, uint commandCount)
        {
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletDispatchSkipped(1);
            XREngine.Debug.RenderingWarningEvery(
                $"Meshlet.DispatchSkipped.{RenderPass}.{reason.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "Meshlet.DispatchSkipped pass={0} requested={1} selected={2} reason='{3}' commandCount={4} capacity={5}",
                RenderPass,
                MeshSubmissionStrategy,
                MeshSubmissionStrategy,
                reason,
                commandCount,
                MaxVisibleMeshletTaskCapacity);
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
            _culledCountBuffer!.BindTo(_indirectRenderTaskShader!, 3);
            _drawCountBuffer!.BindTo(_indirectRenderTaskShader!, 4);
            _indirectOverflowFlagBuffer!.BindTo(_indirectRenderTaskShader!, 5);
            scene.LodTransitionBuffer.BindTo(_indirectRenderTaskShader!, 10);

            _truncationFlagBuffer!.SetDataRawAtIndex(0, 0u);
            _truncationFlagBuffer.PushSubData();
            _truncationFlagBuffer.BindTo(_indirectRenderTaskShader!, 7);

            _statsBuffer!.BindTo(_indirectRenderTaskShader!, 8);
            (_culledCommandsUseHotLayout && _culledHotCommandBuffer is not null
                ? _culledHotCommandBuffer
                : CulledSceneToRenderBuffer).BindTo(_indirectRenderTaskShader!, 9);
            BindViewSetBuffers(_indirectRenderTaskShader!);
        }

        private void BuildCulledHotCommandBuffer()
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.BuildCulledHotCommandBuffer");

            _culledCommandsUseHotLayout = false;
            _culledHotCommandsValid = false;

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
            _culledHotCommandsValid = true;
        }

        private List<HybridRenderingManager.DrawBatch>? BuildGpuBatchesAndInstancing(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.BuildGpuBatchesAndInstancing");

            if (_buildKeysComputeShader is null ||
                _keyIndexBufferA is null ||
                _drawCountBuffer is null ||
                _indirectDrawBuffer is null ||
                _culledCountBuffer is null)
            {
                Dbg("GPU indirect batching unavailable - missing shader/buffer dependencies.", "Materials");
                return null;
            }

            UpdateVisibleCountersFromBuffer();
            if (EnableZeroReadbackMaterialScatter)
            {
                DispatchBuildKeys();
                bool materialScatterDispatched = DispatchMaterialScatter(scene);
                _zeroReadbackMaterialScatterPreparedThisFrame = materialScatterDispatched &&
                    _materialTierIndirectDrawBuffer is not null &&
                    _materialTierDrawCountBuffer is not null &&
                    _materialSlotLookupBuffer is not null &&
                    _materialSlotIds.Count > 0;

                if (_zeroReadbackMaterialScatterPreparedThisFrame &&
                    RequiresActiveMaterialBucketList(ZeroReadbackMaterialDrawPath))
                {
                    DispatchBuildActiveMaterialBuckets();
                }

                UpdateVisibleCountersFromBuffer();
                return null;
            }

#if XRE_DEBUG_BATCH_RANGE_READBACK
            DispatchBuildKeys();
            PopulateMaterialAggregationFlags(scene);
            DispatchBuildGpuBatches(scene);
            UpdateVisibleCountersFromBuffer();

            // When readback is disabled (shipping / zero-readback mode), skip batch readback entirely.
            // The draw submission consumes GPU count buffers rather than CPU material batch ranges.
            if (IsCpuReadbackCountDisabledForPass())
                return null;

            return ReadGpuBatchRanges();
#else
            // Shipping/default builds do not include the legacy GPURenderBuildBatches +
            // batch-range readback path. Use the count-buffer indirect build instead.
            BuildIndirectCommandBuffer(scene);
            UpdateVisibleCountersFromBuffer();
            return null;
#endif
        }

        private bool DispatchMaterialScatter(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchMaterialScatter");

            if (_materialScatterComputeShader is null ||
                _keyIndexBufferA is null ||
                _culledCountBuffer is null)
            {
                return false;
            }

            PopulateMaterialSlotLookup(scene);
            if (_materialSlotLookupBuffer is null ||
                _materialTierIndirectDrawBuffer is null ||
                _materialTierDrawCountBuffer is null ||
                _materialTierBucketCount == 0u ||
                _maxDrawsPerMaterialTier == 0u)
            {
                return false;
            }

            if (!ResetMaterialScatterBuffersOnGpu())
                return false;

            _materialScatterComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _materialScatterComputeShader.Uniform("MaxMaterialSlotLookup", (int)_materialSlotLookupBuffer.ElementCount);
            _materialScatterComputeShader.Uniform("MaxBucketCount", (int)_materialTierBucketCount);
            _materialScatterComputeShader.Uniform("MaxIndirectDrawsPerBucket", (int)_maxDrawsPerMaterialTier);
            _materialScatterComputeShader.Uniform("AtlasIndexCounts", new UVector3(
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Static), 0),
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Dynamic), 0),
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Streaming), 0)));
            _materialScatterComputeShader.Uniform("AtlasVertexCounts", new UVector3(
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Static), 0),
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Dynamic), 0),
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Streaming), 0)));
            _materialScatterComputeShader.Uniform("StatsEnabled", _statsBuffer is null ? 0u : 1u);

            scene.DrawMetadataBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterInputCommands);
            scene.MeshDataBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterMeshData);
            _culledCountBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterCulledCount);
            _keyIndexBufferA.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterSortKeys);
            _materialSlotLookupBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterMaterialSlotLookup);
            _materialTierIndirectDrawBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterIndirectDraws);
            _materialTierDrawCountBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterDrawCounts);
            _indirectOverflowFlagBuffer?.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterOverflow);
            scene.LodTransitionBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterLodTransitions);
            _statsBuffer?.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterStats);

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), _keyIndexBufferA.ElementCount)
                : Math.Max(VisibleCommandCount, 1u);
            uint groups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u), MaterialScatterLocalSizeX).Item1);
            using (BvhGpuProfiler.Instance.SubmissionScope(BvhGpuProfiler.Stage.CommandEmission))
            using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.CommandEmission, dispatchCommands))
                _materialScatterComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
            LogVulkanCounterDiagnostics("after-material-scatter");
            return true;
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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchBuildActiveMaterialBuckets");

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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.PopulateMaterialSlotLookup");

            IReadOnlyDictionary<uint, XRMaterial> materialMap = scene.MaterialMap;
            ulong signature = ComputeMaterialSlotLookupSignature(materialMap, out uint maxMaterialId);

            uint materialSlotLookupCount = maxMaterialId == uint.MaxValue
                ? uint.MaxValue
                : maxMaterialId + 1u;
            EnsureMaterialScatterBuffers(materialSlotLookupCount, (uint)materialMap.Count, CommandCapacity);
            if (_materialSlotLookupBuffer is null)
                return;

            if (ReferenceEquals(_materialSlotLookupUploadedBuffer, _materialSlotLookupBuffer) &&
                _materialSlotLookupSignature == signature &&
                _materialSlotLookupUploadedElementCount == _materialSlotLookupBuffer.ElementCount &&
                _materialSlotIds.Count == materialMap.Count)
            {
                return;
            }

            _materialSlotIds.Clear();
            _materialSlotSortScratch.Clear();

            for (uint i = 0; i < _materialSlotLookupBuffer.ElementCount; ++i)
                _materialSlotLookupBuffer.SetDataRawAtIndex(i, GPUBatchingBindings.InvalidMaterialSlot);

            foreach (uint materialId in materialMap.Keys)
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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ResetMaterialScatterBuffersOnGpu");

            if (_materialTierDrawCountBuffer is null || _materialTierIndirectDrawBuffer is null)
                return false;

            bool countsCleared = ClearUIntBufferOnGpu(
                _materialTierDrawCountBuffer,
                _materialTierDrawCountBuffer.ElementCount,
                EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);

            bool commandsCleared = true;
            if (ShouldClearMaterialScatterIndirectCommands(_materialTierDrawCountBuffer))
            {
                ulong indirectUIntCount = (ulong)_materialTierIndirectDrawBuffer.ElementCount * _materialTierIndirectDrawBuffer.ComponentCount;
                commandsCleared = ClearUIntBufferOnGpu(
                    _materialTierIndirectDrawBuffer,
                    indirectUIntCount,
                    EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command);
                P3Diagnostics.RecordMaterialScatterIndirectCommandClear(cleared: true);
            }
            else
            {
                P3Diagnostics.RecordMaterialScatterIndirectCommandClear(cleared: false);
            }

            return countsCleared && commandsCleared;
        }

        private static bool ShouldClearMaterialScatterIndirectCommands(XRDataBuffer drawCountBuffer)
        {
            if (IndirectDebug.DisableCountDrawPath)
                return true;

            var renderer = AbstractRenderer.Current;
            if (renderer is null || !renderer.SupportsIndirectCountDraw())
                return true;

            return IndirectDebug.ValidateLiveHandles && drawCountBuffer.APIWrappers.Count == 0;
        }

        private bool ClearUIntBufferOnGpu(XRDataBuffer buffer, ulong uintCount, EMemoryBarrierMask barrierMask)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.ClearUIntBufferOnGpu");

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
                RuntimeEngine.Rendering.Stats.GpuTransparency.RecordGpuTransparencyDomainCounts(0, 0, 0, 0);
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

                RuntimeEngine.Rendering.Stats.GpuTransparency.RecordGpuTransparencyDomainCounts(
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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchBuildKeys");

            if (_buildKeysComputeShader is null || _keyIndexBufferA is null || _culledCountBuffer is null)
                return;

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), _keyIndexBufferA.ElementCount)
                : Math.Max(VisibleCommandCount, 1u);

            _buildKeysComputeShader.Uniform("MaxSortKeys", (int)_keyIndexBufferA.ElementCount);
            _buildKeysComputeShader.Uniform("StateBitMask", 0x0FFFu);

            var sortDomain = GpuSortPolicy.ResolveSortDomain(RenderPass, RuntimeEngine.Rendering.Settings.GpuSortDomainPolicy);
            var sortDirection = GpuSortPolicy.ResolveSortDirection(sortDomain);
            _buildKeysComputeShader.Uniform("SortDomain", (int)sortDomain);
            _buildKeysComputeShader.Uniform("SortDirection", (int)sortDirection);

            CulledSceneToRenderBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysInputCommands);
            _culledCountBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysCulledCount);
            _keyIndexBufferA.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysSortKeys);

            uint groups = Math.Max(1, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _buildKeysComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
        }

#if XRE_DEBUG_BATCH_RANGE_READBACK
        private void DispatchBuildGpuBatches(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchBuildGpuBatches");

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
#endif

        private void PopulateMaterialAggregationFlags(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.PopulateMaterialAggregationFlags");

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

#if XRE_DEBUG_BATCH_RANGE_READBACK
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
                    RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                }

                VoidPtr mapped = _gpuBatchRangeBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
                if (!mapped.IsValid)
                    return null;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((int)(batchCount * GPUBatchingLayout.BatchRangeStride));

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
#endif

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

            if (MeshSubmissionStrategy.IsGpuZeroReadbackStrategy())
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
                    RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                }

                VoidPtr mapped = _materialTierActiveBucketBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
                if (!mapped.IsValid)
                    return null;

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((int)(activeCount * sizeof(uint)));

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
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.PrepareMaterialTableAndValidateResidency");

            bool materialTableRequired = EnableZeroReadbackMaterialScatter &&
                ZeroReadbackMaterialDrawPath is EZeroReadbackMaterialDrawPath.MaterialTable
                    or EZeroReadbackMaterialDrawPath.BindlessMaterialTable;

            if (!VulkanFeatureProfile.EnableBindlessMaterialTable && !materialTableRequired)
                return true;

            _materialTable ??= new GPUMaterialTable(128);
            bool allResident = true;
            var renderState = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            XRMaterial? overrideMaterial = renderState?.OverrideMaterial;
            bool useDepthNormalMaterialVariants = renderState?.UseDepthNormalMaterialVariants ?? false;

            HashSet<uint> currentIds = [.. scene.MaterialMap.Keys];
            foreach (uint removedId in _lastMaterialTableIds)
            {
                if (currentIds.Contains(removedId))
                    continue;

                _materialTable.Remove(removedId);
            }

            bool bindlessMaterialTableRequested =
                ZeroReadbackMaterialDrawPath == EZeroReadbackMaterialDrawPath.BindlessMaterialTable;
            VulkanRenderer? vulkanRenderer = AbstractRenderer.Current as VulkanRenderer;
            EMaterialTextureReferenceBuildMode textureReferenceMode = EMaterialTextureReferenceBuildMode.None;
            if (bindlessMaterialTableRequested &&
                AbstractRenderer.Current is OpenGLRenderer glRenderer &&
                glRenderer.SupportsBindlessTextureHandles)
            {
                textureReferenceMode = EMaterialTextureReferenceBuildMode.OpenGLBindlessHandles;
            }
            else if (bindlessMaterialTableRequested && vulkanRenderer is not null)
            {
                if (vulkanRenderer.TryEnsureGlobalMaterialTextureDescriptorTable(out string reason))
                {
                    textureReferenceMode = EMaterialTextureReferenceBuildMode.VulkanDescriptorIndices;
                }
                else
                {
                    string message = $"{FormatDebugPrefix("Materials")} Vulkan bindless material-table requested but unavailable: {reason}";
                    if (VulkanFeatureProfile.RequireBindlessMaterialTable)
                    {
                        _skipGpuSubmissionThisPass = true;
                        _skipGpuSubmissionReason = message;
                        Debug.MeshesWarning(message);
                        return false;
                    }

                    if (_materialResidencyLogBudget > 0)
                    {
                        Debug.MeshesWarning(message + " Falling back to non-bindless material-table rows.");
                        _materialResidencyLogBudget--;
                    }
                }
            }

            foreach (var (materialId, material) in scene.MaterialMap)
            {
                XRMaterial? effectiveMaterial = ResolveEffectiveGpuMaterial(material, overrideMaterial, useDepthNormalMaterialVariants);
                GPUMaterialEntry entry = BuildMaterialEntry(
                    effectiveMaterial,
                    textureReferenceMode,
                    vulkanRenderer,
                    out GPUMaterialTextureReferences textureReferences,
                    out bool resident);
                _materialTable.AddOrUpdate(materialId, entry, textureReferences);
                allResident &= resident;
            }

            _materialTable.TrimTrailingUnused(128u);
            _materialTable.PushDirtyRanges();
            if (textureReferenceMode == EMaterialTextureReferenceBuildMode.VulkanDescriptorIndices)
                vulkanRenderer?.FlushGlobalMaterialTextureDescriptorUpdates();

            if (AbstractRenderer.Current is OpenGLRenderer openGlRenderer)
            {
                while (_materialTable.TryConsumeRetiredHandle(out GPUMaterialRetiredHandle retired))
                    openGlRenderer.ReleaseResidentBindlessTextureHandle(retired.Handle);
            }

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

        private static GPUMaterialEntry BuildMaterialEntry(
            XRMaterial? material,
            EMaterialTextureReferenceBuildMode textureReferenceMode,
            VulkanRenderer? vulkanRenderer,
            out GPUMaterialTextureReferences textureReferences,
            out bool resident)
        {
            textureReferences = GPUMaterialTextureReferences.Empty;
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
                resident &= TryResolveMaterialTexture(material, albedo, "Albedo", textureReferenceMode, vulkanRenderer, out GPUMaterialTextureReference reference);
                textureReferences = textureReferences with { Albedo = reference };
            }

            if (normal is not null)
            {
                flags |= 1u << 1;
                resident &= TryResolveMaterialTexture(material, normal, "Normal", textureReferenceMode, vulkanRenderer, out GPUMaterialTextureReference reference);
                textureReferences = textureReferences with { Normal = reference };
            }

            if (rm is not null)
            {
                flags |= 1u << 2;
                resident &= TryResolveMaterialTexture(material, rm, "RM", textureReferenceMode, vulkanRenderer, out GPUMaterialTextureReference reference);
                textureReferences = textureReferences with { RM = reference };
            }

            if (resident)
                flags |= 1u << 31;

            return new GPUMaterialEntry
            {
                Flags = flags,
                BaseColorOpacity = ResolveMaterialBaseColorOpacity(material),
                RMSE = ResolveMaterialRmse(material),
            };
        }

        private static Vector4 ResolveMaterialBaseColorOpacity(XRMaterial material)
        {
            Vector3 baseColor = material.Parameter<ShaderVector3>("BaseColor")?.Value ?? Vector3.One;
            float opacity = material.Parameter<ShaderFloat>("Opacity")?.Value ?? 1.0f;

            if (material.Parameter<ShaderVector4>("BaseColor") is { } baseColor4)
            {
                Vector4 v = baseColor4.Value;
                baseColor = new Vector3(v.X, v.Y, v.Z);
                opacity = v.W;
            }
            else if (material.Parameter<ShaderVector4>("MatColor") is { } matColor)
            {
                Vector4 v = matColor.Value;
                baseColor = new Vector3(v.X, v.Y, v.Z);
                opacity = v.W;
            }

            return new Vector4(baseColor, opacity);
        }

        private static Vector4 ResolveMaterialRmse(XRMaterial material)
        {
            float roughness = material.Parameter<ShaderFloat>("Roughness")?.Value ?? 1.0f;
            float metallic = material.Parameter<ShaderFloat>("Metallic")?.Value ?? 0.0f;
            float specular = material.Parameter<ShaderFloat>("Specular")?.Value ?? 1.0f;
            float emission = material.Parameter<ShaderFloat>("Emission")?.Value ?? 0.0f;

            return new Vector4(roughness, metallic, specular, emission);
        }

        private static bool TryResolveMaterialTexture(
            XRMaterial material,
            XRTexture texture,
            string semantic,
            EMaterialTextureReferenceBuildMode textureReferenceMode,
            VulkanRenderer? vulkanRenderer,
            out GPUMaterialTextureReference reference)
        {
            reference = GPUMaterialTextureReference.None;
            if (!IsTextureArrayAllowedForMaterialTable(material, texture))
                return false;

            if (textureReferenceMode == EMaterialTextureReferenceBuildMode.OpenGLBindlessHandles)
            {
                if (!TryResolveOpenGLBindlessTextureHandle(texture, out ulong handle))
                    return false;

                reference = GPUMaterialTextureReference.FromOpenGLBindlessHandle(handle);
                return true;
            }

            if (textureReferenceMode == EMaterialTextureReferenceBuildMode.VulkanDescriptorIndices)
            {
                if (vulkanRenderer is null)
                    return false;

                if (!vulkanRenderer.TryGetOrCreateMaterialTextureDescriptorIndex(texture, semantic, out uint descriptorIndex, out _))
                    return false;

                reference = GPUMaterialTextureReference.FromVulkanDescriptorIndex(descriptorIndex);
                return true;
            }

            return IsTextureResident(texture);
        }

        private static bool IsTextureArrayAllowedForMaterialTable(XRMaterial material, XRTexture texture)
        {
            bool isTextureArray =
                texture is XRTexture1DArray ||
                texture is XRTexture2DArray ||
                texture is XRTextureCubeArray;

            if (!isTextureArray)
                return true;

            return material.RenderOptions?.TextureArrayPolicy == EMaterialTextureArrayPolicy.HomogeneousClassOnly;
        }

        private static bool TryResolveOpenGLBindlessTextureHandle(XRTexture texture, out ulong handle)
        {
            handle = 0ul;
            if (AbstractRenderer.Current is not OpenGLRenderer renderer)
                return false;

            return renderer.TryGetResidentBindlessTextureHandle(texture, out handle);
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
            bool captureDiagnostics = ShouldCaptureDiagnosticReadbacksForPass() ||
                (AbstractRenderer.Current is VulkanRenderer && VulkanDelayedCounterDiagnosticsEnabled);
            if (!captureDiagnostics)
                return;

            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (_statsBuffer is not null)
            {
                renderer?.QueueGpuRenderStatsBufferReadback(
                    _statsBuffer,
                    publishDraws: false,
                    publishTriangles: true);
            }

            if (renderer is not VulkanRenderer || !VulkanDelayedCounterDiagnosticsEnabled)
                return;

            if (_culledCountBuffer is not null)
            {
                renderer.QueueGpuRenderDrawCountReadback(
                    _culledCountBuffer,
                    countElementCount: Math.Min(3u, _culledCountBuffer.ElementCount));
            }

            if (_materialTierDrawCountBuffer is not null)
            {
                uint bucketCount = Math.Min(_materialTierBucketCount, _materialTierDrawCountBuffer.ElementCount);
                if (bucketCount > 0u)
                    renderer.QueueGpuRenderDrawCountReadback(_materialTierDrawCountBuffer, countElementCount: bucketCount);
            }

            if (_materialTierActiveBucketCountBuffer is not null)
                renderer.QueueGpuRenderDrawCountReadback(_materialTierActiveBucketCountBuffer);

            if (_keyIndexBufferA is not null)
            {
                ulong keyUIntCount = (ulong)_keyIndexBufferA.ElementCount * _keyIndexBufferA.ComponentCount;
                renderer.QueueGpuRenderDrawCountReadback(
                    _keyIndexBufferA,
                    countElementCount: (uint)Math.Min(keyUIntCount, 64ul));
            }
        }

        private void PostRenderDiagnostics(GPUScene scene)
        {
            if (!ShouldCaptureDiagnosticReadbacksForPass())
            {
                uint requestedDraws = scene.TotalCommandCount;
                uint emittedDraws = VisibleCommandCount;
                uint culledDraws = requestedDraws > emittedDraws ? requestedDraws - emittedDraws : 0u;
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectEffectiveness(
                    requestedDraws,
                    culledDraws,
                    emittedDraws,
                    emittedDraws,
                    overflowCount: 0u);
                return;
            }

            _ = BvhGpuProfiler.Instance.ResolveAndPublish(RuntimeEngine.Time.Timer.Render.LastTimestampTicks, _statsBuffer);
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
            uint meshletExpandOv = _meshletExpansionOverflowFlagBuffer is not null ? ReadUInt(_meshletExpansionOverflowFlagBuffer) : 0u;
            uint overflowTotal = cullOv + indOv + trunc + meshletExpandOv;

            if (cullOv != 0 || indOv != 0 || trunc != 0 || meshletExpandOv != 0)
            {
                Debug.MeshesWarning($"{FormatDebugPrefix("Stats")} GPU Render Overflow: Culling={cullOv} Indirect={indOv} Trunc={trunc} MeshletExpand={meshletExpandOv}");
                Dbg($"Overflow flags cull={cullOv} indirect={indOv} trunc={trunc} meshletExpand={meshletExpandOv}", "Stats");
                if (meshletExpandOv != 0u)
                {
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletExpansionOverflow(meshletExpandOv);
                    Debug.MeshesWarning($"{FormatDebugPrefix("Stats")} Meshlet.ExpandOverflow pass={RenderPass} count={meshletExpandOv} capacity={MaxVisibleMeshletTaskCapacity}");
                }

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
            if (stats.Input > 0u)
                _gpuBvhEstimatedVisibleRatio = Math.Clamp((float)stats.Culled / stats.Input, 0.0f, 1.0f);
            int cpuFallbackEvents = RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents;
            int cpuFallbackRecovered = RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands;
            uint consumedDrawCount = 0u;
            if (!IsCpuReadbackCountDisabledForPass() && _drawCountBuffer is not null)
                consumedDrawCount = ReadUIntAt(_drawCountBuffer, 0u);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectEffectiveness(
                requestedDraws: stats.Input,
                culledDraws: stats.Culled,
                emittedIndirectDraws: stats.Drawn,
                consumedDraws: consumedDrawCount,
                overflowCount: overflowCount);
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletTaskStats(
                stats.MeshletTaskRecordsEmitted,
                stats.MeshletTaskRecordsFrustumCulled,
                stats.MeshletTaskRecordsConeCulled,
                stats.MeshletTaskRecordsHiZCulled);

            if (IsDebugLoggingEnabledForPass())
            {
                Debug.Meshes($"{FormatDebugPrefix("Stats")} [GPU Stats] In={stats.Input} CulledOut={stats.Culled} " +
                         $"Draws={stats.Drawn} Tris={stats.Triangles} RejFrustum={stats.FrustumRejected} RejDist={stats.DistanceRejected} " +
                         $"CpuFallbackEvents={cpuFallbackEvents} CpuRecovered={cpuFallbackRecovered}");
                Debug.Meshes($"{FormatDebugPrefix("Stats")} [Meshlets] Tasks={stats.MeshletTaskRecordsEmitted} " +
                         $"Frustum={stats.MeshletTaskRecordsFrustumCulled} Cone={stats.MeshletTaskRecordsConeCulled} HiZ={stats.MeshletTaskRecordsHiZCulled}");

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
                             $"Ray={stats.BvhRayCount} ({stats.BvhRayMs:F3} ms) " +
                             $"Visited=({stats.BvhVisitedInternalNodes} internal, {stats.BvhVisitedLeaves} leaves, {stats.BvhVisitedCommands} commands) " +
                             $"Rejected=({stats.BvhInternalRejections} internal, {stats.BvhLeafRejections} leaves) " +
                             $"Planes={stats.BvhFrustumPlaneTests} MaskReductions={stats.BvhPlaneMaskReductions} " +
                             $"QueueMax={stats.BvhMaxQueueOccupancy} QueueOverflow={stats.BvhQueueOverflows}");
                }
            }

            LogTransparencyDomainStats(
                (uint)RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
                (uint)RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
                (uint)RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
                (uint)RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible);

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
            _overflowDebugBuffer?.Dispose();
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
            _visibleMeshletTaskBuffer?.Dispose();
            _visibleMeshletTaskCountBuffer?.Dispose();
            _meshletDispatchIndirectBuffer?.Dispose();
            _meshletDispatchCountBuffer?.Dispose();
            _meshletExpansionOverflowFlagBuffer?.Dispose();
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
            _expandMeshletsComputeShader?.Destroy();
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
            public uint MeshletTaskRecordsEmitted { get; }
            public uint MeshletTaskRecordsFrustumCulled { get; }
            public uint MeshletTaskRecordsConeCulled { get; }
            public uint MeshletTaskRecordsHiZCulled { get; }
            public double BvhBuildMs { get; }
            public double BvhRefitMs { get; }
            public double BvhCullMs { get; }
            public double BvhRayMs { get; }
            public uint BvhVisitedInternalNodes { get; }
            public uint BvhVisitedLeaves { get; }
            public uint BvhVisitedCommands { get; }
            public uint BvhFrustumPlaneTests { get; }
            public uint BvhPlaneMaskReductions { get; }
            public uint BvhInternalRejections { get; }
            public uint BvhLeafRejections { get; }
            public uint BvhEmittedCommands { get; }
            public uint BvhMaxQueueOccupancy { get; }
            public uint BvhQueueOverflows { get; }

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
                MeshletTaskRecordsEmitted = values[(int)GpuStatsLayout.MeshletTaskRecordsEmitted];
                MeshletTaskRecordsFrustumCulled = values[(int)GpuStatsLayout.MeshletTaskRecordsFrustumCulled];
                MeshletTaskRecordsConeCulled = values[(int)GpuStatsLayout.MeshletTaskRecordsConeCulled];
                MeshletTaskRecordsHiZCulled = values[(int)GpuStatsLayout.MeshletTaskRecordsHiZCulled];
                BvhVisitedInternalNodes = values[(int)GpuStatsLayout.BvhVisitedInternalNodes];
                BvhVisitedLeaves = values[(int)GpuStatsLayout.BvhVisitedLeaves];
                BvhVisitedCommands = values[(int)GpuStatsLayout.BvhVisitedCommands];
                BvhFrustumPlaneTests = values[(int)GpuStatsLayout.BvhFrustumPlaneTests];
                BvhPlaneMaskReductions = values[(int)GpuStatsLayout.BvhPlaneMaskReductions];
                BvhInternalRejections = values[(int)GpuStatsLayout.BvhInternalRejections];
                BvhLeafRejections = values[(int)GpuStatsLayout.BvhLeafRejections];
                BvhEmittedCommands = values[(int)GpuStatsLayout.BvhEmittedCommands];
                BvhMaxQueueOccupancy = values[(int)GpuStatsLayout.BvhMaxQueueOccupancy];
                BvhQueueOverflows = values[(int)GpuStatsLayout.BvhQueueOverflows];

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
                        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
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

                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(Unsafe.SizeOf<GPUIndirectRenderCommand>());

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
