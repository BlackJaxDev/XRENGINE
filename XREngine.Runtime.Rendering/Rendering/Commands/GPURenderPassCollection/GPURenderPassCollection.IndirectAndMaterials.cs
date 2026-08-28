using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        private readonly HashSet<uint> _currentMaterialTableIdsScratch = [];
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
            GpuProgramsPendingThisFrame = false;
            _viewBatchClassificationFrameId = 0u;
            _viewBatchClassificationPublished = false;
            
            Log(LogCategory.Lifecycle, LogLevel.Info, "Render begin (pass={0})", RenderPass);
            Dbg("Render begin", "Lifecycle");

            if (!TryInitializeRender(scene, out XRCamera? camera) || camera is null)
            {
                ClearExactTransparentMultiviewRejection();
                ClearPassPolicySnapshot();
                return;
            }

            if (!TryPrepareGpuPrograms())
            {
                GpuProgramsPendingThisFrame = true;
                ClearExactTransparentMultiviewRejection();
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
            EMeshPrimitivePathPreference savedPrimitivePathPreference = MeshPrimitivePathPreference;
            bool savedUseMeshletPipeline = UseMeshletPipeline;
            bool meshletDebugForced =
                savedStrategy != EMeshSubmissionStrategy.CpuDirect &&
                savedPrimitivePathPreference == EMeshPrimitivePathPreference.TraditionalOnly &&
                GpuBvhDebugSettings.ShouldForceMeshletForDebugDisplay(camera, RenderPass);

            if (meshletDebugForced)
            {
                MeshSubmissionStrategy = ResolveMeshletDebugDisplayStrategy().ToSubmissionMode();
                MeshPrimitivePathPreference = EMeshPrimitivePathPreference.MeshShaderPreferred;
                UseMeshletPipeline = true;
            }

            try
            {
            if (MeshPrimitivePathPreference != EMeshPrimitivePathPreference.TraditionalOnly)
            {
                bool meshletPipelineReady = _renderManager.TrySealMeshletMaterialTablePipeline(
                    this,
                    camera,
                    scene,
                    RenderPass,
                    out string? readinessFailure);
                SealMeshletDirectPipelineReadiness(meshletPipelineReady, readinessFailure);
            }

            int renderGraphPassIndex = RenderGraphPassIndexOverride != int.MinValue
                ? RenderGraphPassIndexOverride
                : RenderPass;
            using var renderGraphPassScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(renderGraphPassIndex);
            ResetZeroReadbackProgramPendingState();
            bool useTwoPassGpuHiZ = TryPrepareGpuHiZTwoPass(scene, camera, out GpuHiZDepthInput twoPassDepthInput);
            Stopwatch resetStopwatch = Stopwatch.StartNew();
            ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_meshletEvidenceSnapshotFrameId != renderFrameId)
            {
                _meshletEvidenceSnapshotFrameId = renderFrameId;
                _meshletEvidenceSnapshotQueuedThisFrame = false;
                _meshletEvidenceRefreshSnapshotQueuedThisFrame = false;
            }
            ResetCounters();
            resetStopwatch.Stop();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Reset,
                resetStopwatch.Elapsed);

            Cull(scene, camera, deferGpuHiZ: useTwoPassGpuHiZ);
            LogVulkanCounterDiagnostics("after-cull");
            LogVulkanCullInputDiagnostics(scene, "after-cull");
            bool submitted = useTwoPassGpuHiZ
                ? ExecuteGpuHiZTwoPass(scene, camera, twoPassDepthInput)
                : PrepareAndSubmitVisibleSet(scene, camera, "single");
            if (!submitted)
            {
                ClearPassPolicySnapshot();
                return;
            }

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
                    MeshPrimitivePathPreference = savedPrimitivePathPreference;
                    UseMeshletPipeline = savedUseMeshletPipeline;
                }
            }
        }

        /// <summary>
        /// Builds all GPU submission data for the currently active compact command
        /// buffer and records one raster submission. Two-pass Hi-Z calls this once
        /// for the early visibility set and once for the newly visible late set.
        /// </summary>
        private bool PrepareAndSubmitVisibleSet(
            GPUScene scene,
            XRCamera camera,
            string phaseLabel,
            EAdvancedVisibilitySynchronizationBoundary? synchronizationBoundary = null)
        {
            _gpuBatchingPreparedThisFrame = false;
            _zeroReadbackMaterialScatterPreparedThisFrame = false;
            _zeroReadbackActiveBucketListPreparedThisFrame = false;
            _meshletExpansionPreparedThisFrame = false;

            SelectVisibleCommandLods(scene, camera);
            // A pre-sealed traditional route owns every row and must not launch
            // meshlet expansion. Besides being wasted work, producing task and
            // indirect buffers for a route that cannot consume them extends
            // their synchronization/lifetime contract into unrelated passes.
            if (MeshletDirectPipelineReadyThisFrame)
                ExpandVisibleMeshlets(scene);
            ClassifyTransparencyDomains(scene);

            if (RequiresExactTransparentCandidateRejection)
                ReportExactTransparentMultiviewRejection();
            else
                ClearExactTransparentMultiviewRejection();

            // Do not early-out based on CPU-visible counters. GPU-written count
            // buffers naturally turn an empty early/late list into zero draws.
            bool strictNoFallbacks = VulkanFeatureProfile.EnforceStrictNoFallbacks;
            bool cpuBatchingEnabled = IsCpuBatchingEnabledForPass();
            bool useCpuBatchFallback = !strictNoFallbacks && (!EnableGpuDrivenBatching || cpuBatchingEnabled);
            List<HybridRenderingManager.DrawBatch>? batches;
            TimeSpan indirectStageElapsed = TimeSpan.Zero;

            if (useCpuBatchFallback)
            {
                using (BeginTiming($"PopulateMaterialIDs.{phaseLabel}"))
                    PopulateMaterialIDs(scene);

                using (BeginTiming($"BuildIndirectCommandBuffer.{phaseLabel}"))
                {
                    Stopwatch indirectStopwatch = Stopwatch.StartNew();
                    BuildIndirectCommandBuffer(scene);
                    indirectStopwatch.Stop();
                    indirectStageElapsed += indirectStopwatch.Elapsed;
                }

                using var batchTiming = BeginTiming($"BuildMaterialBatchesCpuFallback.{phaseLabel}");
                batches = BuildMaterialBatches(scene);
                CurrentBatches = batches;
                _gpuBatchingPreparedThisFrame = false;
            }
            else
            {
                if (!EnableGpuDrivenBatching && strictNoFallbacks)
                    RecordForbiddenFallback("CPU material batch fallback requested while strict no-fallbacks is active.");

                using var batchTiming = BeginTiming($"BuildGpuBatchesAndInstancing.{phaseLabel}");
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
                        Debug.MeshesWarning($"{FormatDebugPrefix("Materials")} GPU batching produced no batch ranges during {phaseLabel}. " +
                            "Enable IndirectDebug.EnableCpuBatching for emergency fallback diagnostics.");
                    }
                    return false;
                }
            }

            if (indirectStageElapsed > TimeSpan.Zero)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                    RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Indirect,
                    indirectStageElapsed);
            }

            Log(LogCategory.Indirect, LogLevel.Info, "Indirect build complete ({0}) - visible={1}", phaseLabel, VisibleCommandCount);
            Dbg($"Indirect build complete ({phaseLabel})", "Indirect");

            if (batches is not null)
                Log(LogCategory.Materials, LogLevel.Info, "Material batches={0}, visible commands={1}, phase={2}", batches.Count, VisibleCommandCount, phaseLabel);

            if (!PrepareMaterialTableAndValidateResidency(scene, batches))
            {
                Dbg($"Render abort ({phaseLabel}) - material table residency validation failed", "Materials");
                return false;
            }

            if (synchronizationBoundary.HasValue)
                AdvancedVisibilitySynchronizationContract.ApplyOpenGl(synchronizationBoundary.Value);

            Stopwatch drawStopwatch = Stopwatch.StartNew();
            _renderManager.Render(this, camera, scene, _indirectDrawBuffer!, _indirectRenderer, RenderPass, _drawCountBuffer, batches);
            drawStopwatch.Stop();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming.Draw,
                drawStopwatch.Elapsed);

            Log(LogCategory.Lifecycle, LogLevel.Info, "Render submission done ({0})", phaseLabel);
            Dbg($"Render submission done ({phaseLabel})", "Lifecycle");
            return true;
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

            XRDataBuffer commandBuffer = scene.CullControlBuffer;
            XRDataBuffer metadataBuffer = commandBuffer;
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
                DrawMetadata command;
                DrawMetadata metadata;
                try
                {
                    command = commandBuffer.GetDataRawAtIndex<DrawMetadata>(i);
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
                if (AbstractRenderer.Current is IBufferDiagnosticReadbackBackendCapability readbackCapability)
                {
                    uint byteOffset = checked(index * sizeof(uint));
                    Span<byte> bytes = stackalloc byte[sizeof(uint)];
                    if (!readbackCapability.TryReadBufferBytes(buffer, byteOffset, bytes, out reason))
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
                (_activeViewCount > 1u &&
                    (_viewDescriptorBuffer is null ||
                     _viewConstantsBuffer is null ||
                     _culledCommandViewMaskBuffer is null)) ||
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
            uint activeViewCount = _activeViewCount == 0u ? 1u : _activeViewCount;
            _lodSelectComputeShader.Uniform("ActiveViewCount", activeViewCount);
            _lodSelectComputeShader.Uniform(
                "MultiviewLodPolicy",
                (uint)EffectiveMultiviewLodPolicy);
            _lodSelectComputeShader.Uniform(
                "ExactViewMasksValid",
                HasCurrentFrameExactCommandViewMasks ? 1u : 0u);

            _culledSceneToRenderBuffer.BindTo(_lodSelectComputeShader, 0);
            _culledCountBuffer.BindTo(_lodSelectComputeShader, 1);
            scene.LODTableBuffer.BindTo(_lodSelectComputeShader, 2);
            scene.LODRequestBuffer.BindTo(_lodSelectComputeShader, 3);
            scene.LodTransitionBuffer.BindTo(_lodSelectComputeShader, 4);
            scene.CullControlBuffer.BindTo(_lodSelectComputeShader, 5);
            scene.CullBoundsBuffer.BindTo(_lodSelectComputeShader, 6);
            BindViewSetBuffers(_lodSelectComputeShader);

            uint dispatchGroups = Math.Max(1u, XRRenderProgram.ComputeDispatch.ForCommands(dispatchCommands).Item1);
            const EMemoryBarrierMask postLodBarrier = EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.Command;
            _lodSelectComputeShader.DispatchCompute(dispatchGroups, 1, 1, postLodBarrier);
            AbstractRenderer.Current?.MemoryBarrier(postLodBarrier);
            scene.MarkLodTransitionBufferGpuWritten();
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

            if (!UseMeshletPipeline && MeshPrimitivePathPreference == EMeshPrimitivePathPreference.TraditionalOnly)
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
            uint backendTaskLimit = AbstractRenderer.Current?.MaxMeshTaskDispatchGroupsX ?? uint.MaxValue;
            uint taskRecordCapacity = Math.Min(_visibleMeshletTaskBuffer.ElementCount, backendTaskLimit);
            _expandMeshletsComputeShader.Uniform("MaxMeshletTaskRecords", checked((int)taskRecordCapacity));
            _expandMeshletsComputeShader.Uniform("ExpandPreviousLodTransitions", 1);
            _expandMeshletsComputeShader.Uniform(
                "RejectExactTransparentMultiview",
                RequiresExactTransparentCandidateRejection ? 1u : 0u);

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
            scene.AllLoadedTransparencyMetadataBuffer.BindTo(
                _expandMeshletsComputeShader,
                GPUMeshletBindings.ExpandTransparencyMetadata);


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
            _indirectRenderTaskShader.Uniform(
                "ViewBatchSubmissionPolicy",
                (uint)EffectiveViewBatchSubmissionPolicy);
            _indirectRenderTaskShader.Uniform(
                "UseViewBatchClassification",
                HasCurrentFrameViewBatchClassification ? 1u : 0u);
            _indirectRenderTaskShader.Uniform(
                "RejectExactTransparentMultiview",
                RequiresExactTransparentCandidateRejection ? 1u : 0u);
        }

        private void BindIndirectShaderBuffers(GPUScene scene)
        {
            CulledSceneToRenderBuffer.BindTo(_indirectRenderTaskShader!, 0);
            _indirectDrawBuffer!.BindTo(_indirectRenderTaskShader!, 1);
            scene.MeshDataBuffer.BindTo(_indirectRenderTaskShader!, 2);
            _culledCountBuffer!.BindTo(_indirectRenderTaskShader!, 3);
            _drawCountBuffer!.BindTo(_indirectRenderTaskShader!, 4);
            _indirectOverflowFlagBuffer!.BindTo(_indirectRenderTaskShader!, 5);
            scene.CullControlBuffer.BindTo(_indirectRenderTaskShader!, 9);
            scene.LodTransitionBuffer.BindTo(_indirectRenderTaskShader!, 10);
            _viewBatchClassificationBuffer?.BindTo(
                _indirectRenderTaskShader!,
                GPUBatchingBindings.IndirectViewBatchClassification);
            scene.AllLoadedTransparencyMetadataBuffer.BindTo(
                _indirectRenderTaskShader!,
                GPUBatchingBindings.IndirectTransparencyMetadata);

            _truncationFlagBuffer!.SetDataRawAtIndex(0, 0u);
            _truncationFlagBuffer.PushSubData();
            _truncationFlagBuffer.BindTo(_indirectRenderTaskShader!, 7);

            _statsBuffer!.BindTo(_indirectRenderTaskShader!, 8);
            BindViewSetBuffers(_indirectRenderTaskShader!);
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
            DispatchBuildKeys(scene);
            if (EnableZeroReadbackMaterialScatter)
            {
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

            if (UsesCompactMaterialTableSubmission(ZeroReadbackMaterialDrawPath))
            {
                RuntimeEngine.Rendering.Stats.GpuDriven.UpdateGpuCompactionRung(
                    "WorkgroupPrefixScan64",
                    "Portable lower-capability rung; one clamped reservation per workgroup and atlas tier.");
            }

            _materialScatterComputeShader.Uniform("CurrentRenderPass", RenderPass);
            _materialScatterComputeShader.Uniform("MaxMaterialSlotLookup", (int)_materialSlotLookupBuffer.ElementCount);
            _materialScatterComputeShader.Uniform("MaxBucketCount", (int)_materialTierBucketCount);
            _materialScatterComputeShader.Uniform("MaxIndirectDrawsPerBucket", (int)_maxDrawsPerMaterialTier);
            _materialScatterComputeShader.Uniform(
                "CompactMaterialTableOutput",
                UsesCompactMaterialTableSubmission(ZeroReadbackMaterialDrawPath) ? 1u : 0u);
            _materialScatterComputeShader.Uniform("AtlasIndexCounts", new UVector3(
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Static), 0),
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Dynamic), 0),
                (uint)Math.Max(scene.GetAtlasIndexCount(EAtlasTier.Streaming), 0)));
            _materialScatterComputeShader.Uniform("AtlasVertexCounts", new UVector3(
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Static), 0),
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Dynamic), 0),
                (uint)Math.Max(scene.GetAtlasVertexCount(EAtlasTier.Streaming), 0)));
            _materialScatterComputeShader.Uniform(
                "StatsEnabled",
                _statsBuffer is null || IsCpuReadbackCountDisabledForPass()
                    ? 0u
                    : 1u);
            _materialScatterComputeShader.Uniform(
                "RejectExactTransparentMultiview",
                RequiresExactTransparentCandidateRejection ? 1u : 0u);
            _materialScatterComputeShader.Uniform(
                "ExcludeMeshletResidentRows",
                MeshletDirectPipelineReadyThisFrame && !_forceTraditionalMeshletRowsThisFrame ? 1u : 0u);

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
            scene.MeshletRangeBuffer.BindTo(_materialScatterComputeShader, GPUBatchingBindings.MaterialScatterMeshletRanges);
            _meshletExpansionOverflowFlagBuffer?.BindTo(
                _materialScatterComputeShader,
                GPUBatchingBindings.MaterialScatterMeshletExpansionOverflow);
            scene.AllLoadedTransparencyMetadataBuffer.BindTo(
                _materialScatterComputeShader,
                GPUTransparencyBindings.MaterialScatterTransparencyMetadata);

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

        /// <summary>
        /// Rebuilds the sealed material-tier stream without the meshlet exclusion
        /// after direct task/mesh submission failed before it issued work.
        /// </summary>
        internal bool RebuildMaterialScatterForTraditionalMeshletFallback(GPUScene scene, string failureReason)
        {
            ForceTraditionalMeshletRowsForCurrentSubmission(failureReason);
            bool dispatched = DispatchMaterialScatter(scene);
            _zeroReadbackMaterialScatterPreparedThisFrame = dispatched &&
                _materialTierIndirectDrawBuffer is not null &&
                _materialTierDrawCountBuffer is not null &&
                _materialSlotLookupBuffer is not null &&
                _materialSlotIds.Count > 0;
            return _zeroReadbackMaterialScatterPreparedThisFrame;
        }

        private static bool RequiresActiveMaterialBucketList(EZeroReadbackMaterialDrawPath path)
            => path == EZeroReadbackMaterialDrawPath.ActiveBucketListReadbackDiagnostic;

        private static bool UsesCompactMaterialTableSubmission(EZeroReadbackMaterialDrawPath path)
            => path is EZeroReadbackMaterialDrawPath.MaterialTable
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

        private void DispatchBuildKeys(GPUScene scene)
        {
            using var profilerScope = RuntimeEngine.Profiler.Start("GpuIndirect.DispatchBuildKeys");

            if (_buildKeysComputeShader is null ||
                _keyIndexBufferA is null ||
                _culledCountBuffer is null ||
                _viewBatchClassificationBuffer is null ||
                _culledCommandViewMaskBuffer is null)
            {
                return;
            }

            uint dispatchCommands = IsCpuReadbackCountDisabledForPass()
                ? Math.Min(Math.Max(VisibleCommandCount, 1u), _keyIndexBufferA.ElementCount)
                : Math.Max(VisibleCommandCount, 1u);

            _buildKeysComputeShader.Uniform("MaxSortKeys", (int)_keyIndexBufferA.ElementCount);
            _buildKeysComputeShader.Uniform("StateBitMask", 0x0FFFu);
            _buildKeysComputeShader.Uniform(
                "ViewBatchSubmissionPolicy",
                (uint)EffectiveViewBatchSubmissionPolicy);
            _buildKeysComputeShader.Uniform(
                "ActiveViewCount",
                _activeViewCount == 0u ? 1u : _activeViewCount);
            _buildKeysComputeShader.Uniform(
                "ExactViewMasksValid",
                HasCurrentFrameExactCommandViewMasks ? 1u : 0u);
            _buildKeysComputeShader.Uniform(
                "MultiviewLodPolicy",
                (uint)EffectiveMultiviewLodPolicy);

            var sortDomain = GpuSortPolicy.ResolveSortDomain(RenderPass, RuntimeEngine.Rendering.Settings.GpuSortDomainPolicy);
            var sortDirection = GpuSortPolicy.ResolveSortDirection(sortDomain);
            _buildKeysComputeShader.Uniform("SortDomain", (int)sortDomain);
            _buildKeysComputeShader.Uniform("SortDirection", (int)sortDirection);

            CulledSceneToRenderBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysInputCommands);
            _culledCountBuffer.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysCulledCount);
            _keyIndexBufferA.BindTo(_buildKeysComputeShader, GPUBatchingBindings.BuildKeysSortKeys);
            _viewBatchClassificationBuffer.BindTo(
                _buildKeysComputeShader,
                GPUBatchingBindings.BuildKeysClassification);
            scene.AllLoadedTransparencyMetadataBuffer.BindTo(
                _buildKeysComputeShader,
                GPUBatchingBindings.BuildKeysTransparencyMetadata);
            scene.CullControlBuffer.BindTo(_buildKeysComputeShader, 5u);
            _culledCommandViewMaskBuffer.BindTo(
                _buildKeysComputeShader,
                GPUBatchingBindings.BuildKeysExactViewMasks);

            uint groups = Math.Max(1, XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(dispatchCommands, 1u)).Item1);
            _buildKeysComputeShader.DispatchCompute(groups, 1, 1, EMemoryBarrierMask.ShaderStorage);
            _viewBatchClassificationFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            _viewBatchClassificationPublished = true;
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
            scene.CullControlBuffer.BindTo(_buildGpuBatchesComputeShader, GPUBatchingBindings.BuildBatchesDrawMetadata);

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
        private struct GpuBatchRangeReadState(
            uint batchCount,
            uint stride,
            List<HybridRenderingManager.DrawBatch> batches)
        {
            internal uint BatchCount = batchCount;
            internal uint Stride = stride;
            internal List<HybridRenderingManager.DrawBatch> Batches = batches;
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
                    RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                }

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((int)(batchCount * GPUBatchingLayout.BatchRangeStride));

                uint stride = _gpuBatchRangeBuffer.ElementSize;
                if (stride == 0)
                    stride = GPUBatchingLayout.BatchRangeStride;

                var batches = new List<HybridRenderingManager.DrawBatch>((int)batchCount);
                GpuBatchRangeReadState state = new(batchCount, stride, batches);
                if (!_gpuBatchRangeBuffer.TryReadMapped(
                        ref state,
                        static (scoped ReadOnlySpan<byte> bytes, ref GpuBatchRangeReadState readState) =>
                        {
                            int entrySize = Unsafe.SizeOf<GPUBatchRangeEntry>();
                            ulong requiredBytes = readState.BatchCount == 0
                                ? 0UL
                                : checked(((ulong)readState.BatchCount - 1UL) * readState.Stride + (uint)entrySize);
                            if (requiredBytes > (ulong)bytes.Length)
                                return false;

                            for (uint index = 0; index < readState.BatchCount; index++)
                            {
                                int offset = checked((int)(index * readState.Stride));
                                GPUBatchRangeEntry range =
                                    MemoryMarshal.Read<GPUBatchRangeEntry>(bytes.Slice(offset, entrySize));
                                if (range.DrawCount == 0u)
                                    continue;

                                readState.Batches.Add(new HybridRenderingManager.DrawBatch(
                                    range.DrawOffset,
                                    range.DrawCount,
                                    range.MaterialID));
                            }

                            return true;
                        }))
                {
                    return null;
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
                    "[RenderDispatch] Diagnostic draw path {0} is reading back {1} active material buckets for pass {2}. Use BindlessMaterialTable for production zero-readback submission.",
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

                AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((int)(activeCount * sizeof(uint)));

                var buckets = new List<uint>((int)activeCount);
                if (!_materialTierActiveBucketBuffer.TryReadMapped(bytes =>
                {
                    ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
                    for (uint i = 0; i < activeCount; ++i)
                    {
                        uint bucketIndex = values[checked((int)i)];
                        if (bucketIndex < _materialTierBucketCount)
                            buckets.Add(bucketIndex);
                    }
                    return true;
                }))
                    return null;

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
            var renderState = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState;
            XRMaterial? overrideMaterial = renderState?.OverrideMaterial;
            bool useDepthNormalMaterialVariants = renderState?.UseDepthNormalMaterialVariants ?? false;

            _currentMaterialTableIdsScratch.Clear();
            foreach (uint materialId in scene.MaterialMap.Keys)
                _currentMaterialTableIdsScratch.Add(materialId);

            foreach (uint removedId in _lastMaterialTableIds)
            {
                if (_currentMaterialTableIdsScratch.Contains(removedId))
                    continue;

                _materialTable.Remove(removedId);
            }

            bool bindlessMaterialTableRequested =
                ZeroReadbackMaterialDrawPath == EZeroReadbackMaterialDrawPath.BindlessMaterialTable;
            IMaterialTableBackendCapability? materialCapability =
                AbstractRenderer.Current as IMaterialTableBackendCapability;
            EMaterialTableTextureReferenceMode textureReferenceMode =
                EMaterialTableTextureReferenceMode.None;
            if (bindlessMaterialTableRequested &&
                materialCapability?.SupportsBindlessTextureHandles == true)
            {
                textureReferenceMode = EMaterialTableTextureReferenceMode.OpenGLBindlessHandleTable;
            }
            else if (bindlessMaterialTableRequested && materialCapability is not null)
            {
                if (materialCapability.TryEnsureMaterialTextureTable(out string reason))
                {
                    textureReferenceMode = EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable;
                }
                else
                {
                    string message = $"{FormatDebugPrefix("Materials")} Vulkan bindless material-table requested but unavailable: {reason}";
                    if (VulkanFeatureProfile.RequireBindlessMaterialTable)
                    {
                        _skipGpuSubmissionThisPass = true;
                        _skipGpuSubmissionReason = message;
                        Debug.MeshesWarning(message);
                        RuntimeEngine.Rendering.Stats.GpuDriven.RecordMaterialReadiness(
                            _currentMaterialTableIdsScratch.Count,
                            readyRows: 0,
                            nonReadyTextureReferences: 0,
                            invalidMaterialIds: 0,
                            fallbackSubmittedRows: 0,
                            materialTableGeneration: _materialTable.PublicationGeneration,
                            descriptorPublicationGeneration: 0ul);
                        return false;
                    }

                    if (_materialResidencyLogBudget > 0)
                    {
                        Debug.MeshesWarning(message + " Falling back to non-bindless material-table rows.");
                        _materialResidencyLogBudget--;
                    }
                }
            }

            bool allResident = PopulateMaterialTableRows(
                scene.MaterialMap,
                overrideMaterial,
                useDepthNormalMaterialVariants,
                textureReferenceMode,
                materialCapability,
                out int readyRows,
                out int nonReadyTextureReferences,
                out int invalidMaterialIds,
                out ulong descriptorPublicationGeneration);

            if (textureReferenceMode == EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable)
            {
                materialCapability?.FlushMaterialTextureTableUpdates();
                if (!allResident && nonReadyTextureReferences > 0)
                {
                    allResident = PopulateMaterialTableRows(
                        scene.MaterialMap,
                        overrideMaterial,
                        useDepthNormalMaterialVariants,
                        textureReferenceMode,
                        materialCapability,
                        out readyRows,
                        out nonReadyTextureReferences,
                        out invalidMaterialIds,
                        out descriptorPublicationGeneration);
                }
            }

            _materialTable.TrimTrailingUnused(128u);
            _materialTable.PushDirtyRanges();

            if (materialCapability is not null)
            {
                while (_materialTable.TryConsumeRetiredHandle(out GPUMaterialRetiredHandle retired))
                    materialCapability.ReleaseMaterialTextureReference(retired);
            }

            _lastMaterialTableIds.Clear();
            foreach (uint materialId in _currentMaterialTableIdsScratch)
                _lastMaterialTableIds.Add(materialId);

            SetMaterialTable(_materialTable);
            RuntimeEngine.Rendering.Stats.GpuDriven.RecordMaterialReadiness(
                _currentMaterialTableIdsScratch.Count,
                readyRows,
                nonReadyTextureReferences,
                invalidMaterialIds,
                fallbackSubmittedRows: 0,
                _materialTable.PublicationGeneration,
                descriptorPublicationGeneration);

            if (!allResident)
            {
                _skipGpuSubmissionThisPass = true;
                _skipGpuSubmissionReason =
                    $"Material readiness guarantee failed before indirect draw submission " +
                    $"(readyRows={readyRows}/{_currentMaterialTableIdsScratch.Count}, " +
                    $"nonReadyTextureReferences={nonReadyTextureReferences}, invalidMaterialIds={invalidMaterialIds}).";
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

        private bool PopulateMaterialTableRows(
            IReadOnlyDictionary<uint, XRMaterial> materialMap,
            XRMaterial? overrideMaterial,
            bool useDepthNormalMaterialVariants,
            EMaterialTableTextureReferenceMode textureReferenceMode,
            IMaterialTableBackendCapability? materialCapability,
            out int readyRows,
            out int nonReadyTextureReferences,
            out int invalidMaterialIds,
            out ulong descriptorPublicationGeneration)
        {
            readyRows = 0;
            nonReadyTextureReferences = 0;
            invalidMaterialIds = 0;
            descriptorPublicationGeneration = 0ul;

            foreach (var (materialId, material) in materialMap)
            {
                if (materialId == 0u)
                {
                    invalidMaterialIds++;
                    continue;
                }

                XRMaterial? effectiveMaterial = ResolveEffectiveGpuMaterial(material, overrideMaterial, useDepthNormalMaterialVariants);
                GPUMaterialEntry entry = BuildMaterialEntry(
                    effectiveMaterial,
                    textureReferenceMode,
                    materialCapability,
                    out GPUMaterialTextureReferences textureReferences,
                    out EMaterialTextureReferenceStatus residencyStatus,
                    out int rowNonReadyTextureReferences,
                    out ulong rowDescriptorPublicationGeneration);
                _materialTable!.AddOrUpdate(materialId, entry, textureReferences);

                if (residencyStatus == EMaterialTextureReferenceStatus.Ready)
                    readyRows++;
                nonReadyTextureReferences += rowNonReadyTextureReferences;
                descriptorPublicationGeneration = Math.Max(
                    descriptorPublicationGeneration,
                    rowDescriptorPublicationGeneration);
            }

            return readyRows == materialMap.Count && invalidMaterialIds == 0;
        }

        private static GPUMaterialEntry BuildMaterialEntry(
            XRMaterial? material,
            EMaterialTableTextureReferenceMode textureReferenceMode,
            IMaterialTableBackendCapability? materialCapability,
            out GPUMaterialTextureReferences textureReferences,
            out EMaterialTextureReferenceStatus residencyStatus,
            out int nonReadyTextureReferences,
            out ulong descriptorPublicationGeneration)
        {
            textureReferences = GPUMaterialTextureReferences.Empty;
            residencyStatus = EMaterialTextureReferenceStatus.Ready;
            nonReadyTextureReferences = 0;
            descriptorPublicationGeneration = 0ul;
            uint flags = 0u;

            if (material is null)
            {
                residencyStatus = EMaterialTextureReferenceStatus.Failed;
                return new GPUMaterialEntry { Flags = flags };
            }

            MaterialBindingSourceSnapshot source = MaterialBindingSourceEncoder.Encode(material);
            GPUMaterialEntry entry = source.Entry;
            XRTexture? albedo = source.Albedo;
            XRTexture? normal = source.Normal;
            XRTexture? rm = source.RM;
            flags = entry.Flags;

            if (albedo is not null)
            {
                flags |= 1u << 0;
                MaterialTextureReferenceResolution resolution = ResolveMaterialTexture(
                    material,
                    albedo,
                    "Albedo",
                    textureReferenceMode,
                    materialCapability);
                AccumulateMaterialTextureResolution(
                    resolution,
                    ref residencyStatus,
                    ref nonReadyTextureReferences,
                    ref descriptorPublicationGeneration);
                textureReferences = textureReferences with { Albedo = resolution.Reference };
            }

            if (normal is not null)
            {
                flags |= 1u << 1;
                MaterialTextureReferenceResolution resolution = ResolveMaterialTexture(
                    material,
                    normal,
                    "Normal",
                    textureReferenceMode,
                    materialCapability);
                AccumulateMaterialTextureResolution(
                    resolution,
                    ref residencyStatus,
                    ref nonReadyTextureReferences,
                    ref descriptorPublicationGeneration);
                textureReferences = textureReferences with { Normal = resolution.Reference };
            }

            if (rm is not null)
            {
                flags |= 1u << 2;
                MaterialTextureReferenceResolution resolution = ResolveMaterialTexture(
                    material,
                    rm,
                    "RM",
                    textureReferenceMode,
                    materialCapability);
                AccumulateMaterialTextureResolution(
                    resolution,
                    ref residencyStatus,
                    ref nonReadyTextureReferences,
                    ref descriptorPublicationGeneration);
                textureReferences = textureReferences with { RM = resolution.Reference };
            }

            if (residencyStatus == EMaterialTextureReferenceStatus.Ready)
                flags |= 1u << 31;

            entry.Flags = flags;
            return entry;
        }

        private static void AccumulateMaterialTextureResolution(
            in MaterialTextureReferenceResolution resolution,
            ref EMaterialTextureReferenceStatus residencyStatus,
            ref int nonReadyTextureReferences,
            ref ulong descriptorPublicationGeneration)
        {
            if (resolution.Status != EMaterialTextureReferenceStatus.Ready)
                nonReadyTextureReferences++;

            if (resolution.Status > residencyStatus)
                residencyStatus = resolution.Status;
            descriptorPublicationGeneration = Math.Max(
                descriptorPublicationGeneration,
                resolution.PublicationGeneration);
        }
        private static MaterialTextureReferenceResolution ResolveMaterialTexture(
            XRMaterial material,
            XRTexture texture,
            string semantic,
            EMaterialTableTextureReferenceMode textureReferenceMode,
            IMaterialTableBackendCapability? materialCapability)
        {
            if (!IsTextureArrayAllowedForMaterialTable(material, texture))
                return MaterialTextureReferenceResolution.Unsupported(
                    "Texture arrays require explicit material-table support.");

            if (textureReferenceMode is
                EMaterialTableTextureReferenceMode.OpenGLBindlessHandleTable or
                EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable)
            {
                return materialCapability?.ResolveMaterialTextureReference(texture, semantic)
                    ?? MaterialTextureReferenceResolution.Unsupported(
                        "The active renderer does not expose a material-table backend capability.");
            }

            return IsTextureResident(texture)
                ? new MaterialTextureReferenceResolution(
                    EMaterialTextureReferenceStatus.Ready,
                    GPUMaterialTextureReference.None,
                    0ul,
                    string.Empty)
                : MaterialTextureReferenceResolution.Pending(
                    "Material texture is not resident.");
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
                var cmd = scene.CullControlBuffer.GetDataRawAtIndex<DrawMetadata>(i);
                
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
            => ResolveMaterialId(scene, index);

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
            if (TryGetMaterialFromBuffer(scene.CullControlBuffer, visibleIndex, scene, "scene cull-control buffer", out id))
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
                var cmd = buffer.GetDataRawAtIndex<DrawMetadata>(index);
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

        internal bool TryQueueMeshletEvidenceSnapshot(
            AbstractRenderer renderer,
            bool refreshExisting = false)
        {
            // A frame can render the same collection through multiple viewports.
            // Preserve the first direct meshlet submission so a later empty
            // viewport cannot overwrite valid GPU evidence with zeros. A two-
            // phase Hi-Z pass may also preserve its final producer because only
            // that sample can contain task-level Hi-Z culls. Keep the two
            // samples in distinct destinations so the late copy cannot erase
            // phase-one evidence before its deferred readback executes.
            ulong discardGeneration = renderer.GpuDiagnosticSnapshotDiscardGeneration;
            if (_meshletEvidenceSnapshotQueuedThisFrame &&
                _meshletEvidenceSnapshotDiscardGeneration != discardGeneration)
            {
                // The backend rejected or abandoned the attempt that owned the
                // first snapshot. Permit a same-frame retry while keeping
                // ordinary later empty viewports unable to replace accepted
                // evidence.
                _meshletEvidenceSnapshotQueuedThisFrame = false;
                _meshletEvidenceRefreshSnapshotQueuedThisFrame = false;
            }

            bool queueRefresh = refreshExisting && _meshletEvidenceSnapshotQueuedThisFrame;
            if (_meshletEvidenceSnapshotQueuedThisFrame &&
                (!queueRefresh || _meshletEvidenceRefreshSnapshotQueuedThisFrame))
            {
                return true;
            }

            if (!_passMeshletEvidenceReadbacksEnabled ||
                _statsBuffer is null ||
                _meshletDispatchIndirectBuffer is null ||
                _meshletDispatchCountBuffer is null ||
                _meshletStatsDiagnosticsSnapshotBuffer is null ||
                _meshletDispatchDiagnosticsSnapshotBuffer is null ||
                _meshletStatsDiagnosticsRefreshSnapshotBuffer is null ||
                _meshletDispatchDiagnosticsRefreshSnapshotBuffer is null)
            {
                return false;
            }

            XRDataBuffer statsSnapshotBuffer = queueRefresh
                ? _meshletStatsDiagnosticsRefreshSnapshotBuffer
                : _meshletStatsDiagnosticsSnapshotBuffer;
            XRDataBuffer dispatchSnapshotBuffer = queueRefresh
                ? _meshletDispatchDiagnosticsRefreshSnapshotBuffer
                : _meshletDispatchDiagnosticsSnapshotBuffer;
            string statsSnapshotLabel = queueRefresh
                ? "MeshletStatsDiagnosticsRefreshSnapshot"
                : "MeshletStatsDiagnosticsSnapshot";
            string dispatchSnapshotLabel = queueRefresh
                ? "MeshletDispatchDiagnosticsRefreshSnapshot"
                : "MeshletDispatchDiagnosticsSnapshot";
            string dispatchCountSnapshotLabel = queueRefresh
                ? "MeshletDispatchCountDiagnosticsRefreshSnapshot"
                : "MeshletDispatchCountDiagnosticsSnapshot";

            bool statsQueued = renderer.TryEnqueueGpuDiagnosticBufferSnapshot(
                _statsBuffer,
                statsSnapshotBuffer,
                checked((nuint)GpuStatsLayout.FieldCount * sizeof(uint)),
                statsSnapshotLabel);
            bool dispatchQueued = renderer.TryEnqueueGpuDiagnosticBufferSnapshot(
                _meshletDispatchIndirectBuffer,
                dispatchSnapshotBuffer,
                checked((nuint)GPUMeshletLayout.MeshTaskIndirectCommandUIntCount * sizeof(uint)),
                dispatchSnapshotLabel);
            bool dispatchCountQueued = renderer.TryEnqueueGpuDiagnosticBufferSnapshot(
                _meshletDispatchCountBuffer,
                0,
                dispatchSnapshotBuffer,
                checked((nuint)GPUMeshletLayout.MeshTaskIndirectCommandUIntCount * sizeof(uint)),
                sizeof(uint),
                dispatchCountSnapshotLabel);
            bool statsReadbackQueued = statsQueued && renderer.QueueGpuRenderStatsBufferReadback(
                statsSnapshotBuffer,
                publishDraws: false,
                publishTriangles: true);
            bool dispatchReadbackQueued = dispatchQueued && dispatchCountQueued && renderer.QueueGpuMeshletDispatchDiagnosticsReadback(
                dispatchSnapshotBuffer);
            bool snapshotQueued = statsQueued &&
                dispatchQueued &&
                dispatchCountQueued &&
                statsReadbackQueued &&
                dispatchReadbackQueued;
            if (!snapshotQueued)
                return false;

            if (queueRefresh)
            {
                _meshletEvidenceRefreshSnapshotQueuedThisFrame = true;
            }
            else
            {
                _meshletEvidenceSnapshotQueuedThisFrame = true;
                _meshletEvidenceSnapshotDiscardGeneration = discardGeneration;
            }

            return true;
        }

        private void QueueAsyncGpuTriangleStatsReadback()
        {
            bool captureDiagnostics = ShouldCaptureDiagnosticReadbacksForPass() ||
                (AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan && VulkanDelayedCounterDiagnosticsEnabled);
            if (!captureDiagnostics)
                return;

            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (!_passMeshletEvidenceReadbacksEnabled && _statsBuffer is not null)
            {
                renderer?.QueueGpuRenderStatsBufferReadback(
                    _statsBuffer,
                    publishDraws: false,
                    publishTriangles: true);
            }

            if (renderer?.BackendId != RendererBackendId.Vulkan || !VulkanDelayedCounterDiagnosticsEnabled)
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
            // Vulkan production proof is published only by the completed
            // fence-delayed readback path. This current-frame diagnostic map
            // remains useful for non-Vulkan backends, but must not satisfy a
            // Vulkan zero-readback acceptance gate.
            if (AbstractRenderer.Current?.BackendId != RendererBackendId.Vulkan)
            {
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletTaskStats(
                    stats.MeshletTaskRecordsEmitted,
                    stats.MeshletTaskRecordsFrustumCulled,
                    stats.MeshletTaskRecordsConeCulled,
                    stats.MeshletTaskRecordsHiZCulled);
            }

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
            _twoPassCandidateCountBuffer?.Dispose();
            _twoPassPhaseOneCountBuffer?.Dispose();
            _twoPassPhaseOneCommandBuffer?.Dispose();
            _twoPassVisibilityBuffer?.Dispose();
            _drawCountBuffer?.Dispose();
            _cullingOverflowFlagBuffer?.Dispose();
            _indirectOverflowFlagBuffer?.Dispose();
            _occlusionOverflowFlagBuffer?.Dispose();
            _overflowDebugBuffer?.Dispose();
            _sortedCommandBuffer?.Dispose();
            _keyIndexBufferA?.Dispose();
            _viewBatchClassificationBuffer?.Dispose();
            _gpuBatchRangeBuffer?.Dispose();
            _gpuBatchCountBuffer?.Dispose();
            _materialSlotLookupBuffer?.Dispose();
            _materialTierIndirectDrawBuffer?.Dispose();
            _materialTierDrawCountBuffer?.Dispose();
            _twoPassPhaseOneMaterialTierIndirectDrawBuffer?.Dispose();
            _twoPassPhaseOneMaterialTierDrawCountBuffer?.Dispose();
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
            _visibleMeshletTaskBuffer?.Dispose();
            _visibleMeshletTaskCountBuffer?.Dispose();
            _meshletDispatchIndirectBuffer?.Dispose();
            _meshletDispatchCountBuffer?.Dispose();
            _meshletExpansionOverflowFlagBuffer?.Dispose();
            _meshletStatsDiagnosticsSnapshotBuffer?.Dispose();
            _meshletDispatchDiagnosticsSnapshotBuffer?.Dispose();
            _meshletStatsDiagnosticsRefreshSnapshotBuffer?.Dispose();
            _meshletDispatchDiagnosticsRefreshSnapshotBuffer?.Dispose();
            _passFilterDebugBuffer?.Dispose();
            _materialIDsBuffer?.Dispose();
            _materialTable?.Dispose();
            _keyIndexScratchBuffer?.Dispose();
            DisposeViewSetBuffers();

            DestroyHiZMipSourceViews();
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
            _expandMeshletsComputeShader?.Destroy();
            _clearUIntsComputeShader?.Destroy();
            _indirectRenderer?.Destroy();

            _hiZInitProgram?.Destroy();
            _hiZGenProgram?.Destroy();
            _hiZPhaseOneProgram?.Destroy();
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
            private readonly uint _stride;

            public bool IsValid => _buffer?.IsMapped == true;

            public MappedBufferScope(XRDataBuffer buffer)
            {
                _buffer = buffer;
                _mappedHere = false;
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

                    if (buffer.IsMapped)
                    {
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                        _stride = buffer.ElementSize != 0 ? buffer.ElementSize : sizeof(uint);
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

            public void Dispose()
            {
                if (_mappedHere && _buffer is not null)
                    _buffer.UnmapBufferData();
            }
        }

        #endregion
    }
}
