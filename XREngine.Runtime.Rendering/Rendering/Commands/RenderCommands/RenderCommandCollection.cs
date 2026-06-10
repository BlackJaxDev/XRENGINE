using XREngine.Extensions;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Commands
{
    public class FarToNearRenderCommandSorter : IComparer<RenderCommand>
    {
        int IComparer<RenderCommand>.Compare(RenderCommand? x, RenderCommand? y)
            => -(x?.CompareTo(y) ?? 0);
    }
    public class NearToFarRenderCommandSorter : IComparer<RenderCommand>
    {
        int IComparer<RenderCommand>.Compare(RenderCommand? x, RenderCommand? y)
            => x?.CompareTo(y) ?? 0;
    }

    /// <summary>
    /// This class is used to manage the rendering of objects in the scene.
    /// RenderCommands are collected and placed in sorted passes that are rendered in order.
    /// At the end of the render and update loop, the buffers are swapped for consumption and the update list is cleared for the next frame.
    /// </summary>
    public sealed class RenderCommandCollection : XRBase
    {
        private static readonly CpuRenderOcclusionCoordinator s_cpuOcclusionCoordinator = new();
        private static readonly CpuSoftwareOcclusionCuller s_cpuSoftwareOcclusionCuller = new();
        private static int s_addCpuMissingPassDiagCount = 0;
        private const int SponzaCpuDiagMaxLines = 768;
        private static int s_sponzaCpuDiagLines;
        private Dictionary<int, Type?> _passSorterTypes = [];
        private Dictionary<int, long> _updatingPassSortOrderCounters = [];

        internal static CpuSoftwareOcclusionCuller CpuSoftwareOcclusion => s_cpuSoftwareOcclusionCuller;

        public bool IsShadowPass { get; private set; } = false;
        public void SetRenderPasses(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters, IEnumerable<RenderPassMetadata>? passMetadata = null)
        {
            using (_lock.EnterScope())
            {
                Dictionary<int, RenderPassMetadata> incomingPassMetadata = BuildPassMetadata(passMetadata);
                EnsureDefaultPassMetadata(passIndicesAndSorters.Keys, incomingPassMetadata);
                if (HasEquivalentPassConfiguration(passIndicesAndSorters, incomingPassMetadata))
                    return;

                string ownerName = _ownerPipeline?.DebugName ?? "<no-owner>";
                Debug.Rendering($"[RenderCommandCollection] SetRenderPasses called. Owner={ownerName} PassCount={passIndicesAndSorters.Count} Keys=[{string.Join(",", passIndicesAndSorters.Keys.OrderBy(static x => x))}]");

                _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value is null ? [] : (ICollection<RenderCommand>)new SortedSet<RenderCommand>(x.Value));
                _passSorterTypes = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value?.GetType());
                _updatingPassSortOrderCounters = passIndicesAndSorters.Keys.ToDictionary(static key => key, static _ => 0L);

                _renderingPasses = [];
                _gpuPasses = [];

                _passMetadata = incomingPassMetadata;

                foreach (KeyValuePair<int, ICollection<RenderCommand>> pass in _updatingPasses)
                {
                    // Use TryAdd to safely handle any edge cases with duplicate keys
                    _renderingPasses.TryAdd(pass.Key, []);
                    
                    if (!_gpuPasses.ContainsKey(pass.Key))
                    {
                        var gpuPass = new GPURenderPassCollection(pass.Key);
                        gpuPass.SetDebugContext(_ownerPipeline, pass.Key);
                        _gpuPasses[pass.Key] = gpuPass;
                    }

                    if (!_passMetadata.ContainsKey(pass.Key))
                        _passMetadata[pass.Key] = new RenderPassMetadata(pass.Key, $"Pass{pass.Key}", ERenderGraphPassStage.Graphics);
                }
            }
        }

        private Dictionary<int, RenderPassMetadata> BuildPassMetadata(IEnumerable<RenderPassMetadata>? passMetadata)
        {
            Dictionary<int, RenderPassMetadata> metadata = [];
            if (passMetadata is null)
                return metadata;

            foreach (RenderPassMetadata meta in passMetadata)
            {
                if (metadata.TryAdd(meta.PassIndex, meta))
                    continue;

                RenderPassMetadata existing = metadata[meta.PassIndex];
                Debug.RenderingWarningEvery(
                    $"RenderPassMetadata.Duplicate.{meta.PassIndex}",
                    TimeSpan.FromSeconds(5),
                    "[RenderDiag] Duplicate RenderPassMetadata PassIndex={0}. Keeping first ('{1}', Stage={2}), ignoring ('{3}', Stage={4}).",
                    meta.PassIndex,
                    existing.Name,
                    existing.Stage,
                    meta.Name,
                    meta.Stage);
            }

            return metadata;
        }

        private static void EnsureDefaultPassMetadata(IEnumerable<int> passIndices, Dictionary<int, RenderPassMetadata> metadata)
        {
            foreach (int passIndex in passIndices)
            {
                if (!metadata.ContainsKey(passIndex))
                    metadata[passIndex] = new RenderPassMetadata(passIndex, $"Pass{passIndex}", ERenderGraphPassStage.Graphics);
            }
        }

        private bool HasEquivalentPassConfiguration(
            Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters,
            Dictionary<int, RenderPassMetadata> passMetadata)
        {
            if (_updatingPasses.Count != passIndicesAndSorters.Count ||
                _passSorterTypes.Count != passIndicesAndSorters.Count ||
                _passMetadata.Count != passMetadata.Count)
            {
                return false;
            }

            foreach ((int passIndex, IComparer<RenderCommand>? sorter) in passIndicesAndSorters)
            {
                if (!_updatingPasses.ContainsKey(passIndex))
                    return false;

                if (!_passSorterTypes.TryGetValue(passIndex, out Type? existingSorterType) || existingSorterType != sorter?.GetType())
                    return false;
            }

            foreach ((int passIndex, RenderPassMetadata metadata) in passMetadata)
            {
                if (!_passMetadata.TryGetValue(passIndex, out RenderPassMetadata? existingMetadata) ||
                    !HasEquivalentPassMetadata(existingMetadata, metadata))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasEquivalentPassMetadata(RenderPassMetadata existing, RenderPassMetadata incoming)
        {
            if (existing.PassIndex != incoming.PassIndex ||
                existing.Stage != incoming.Stage ||
                !string.Equals(existing.Name, incoming.Name, StringComparison.Ordinal))
            {
                return false;
            }

            if (!existing.ExplicitDependencies.OrderBy(static value => value).SequenceEqual(incoming.ExplicitDependencies.OrderBy(static value => value)))
                return false;

            if (!existing.DescriptorSchemas.OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(incoming.DescriptorSchemas.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                return false;

            if (existing.ResourceUsages.Count != incoming.ResourceUsages.Count)
                return false;

            for (int index = 0; index < existing.ResourceUsages.Count; index++)
            {
                RenderPassResourceUsage existingUsage = existing.ResourceUsages[index];
                RenderPassResourceUsage incomingUsage = incoming.ResourceUsages[index];
                if (!string.Equals(existingUsage.ResourceName, incomingUsage.ResourceName, StringComparison.Ordinal) ||
                    existingUsage.ResourceType != incomingUsage.ResourceType ||
                    existingUsage.Access != incomingUsage.Access ||
                    existingUsage.LoadOp != incomingUsage.LoadOp ||
                    existingUsage.StoreOp != incomingUsage.StoreOp)
                {
                    return false;
                }
            }

            return true;
        }

        private int _numCommandsRecentlyAddedToUpdate = 0;

        private Dictionary<int, ICollection<RenderCommand>> _updatingPasses = [];
        private Dictionary<int, ICollection<RenderCommand>> _renderingPasses = [];
        private Dictionary<int, GPURenderPassCollection> _gpuPasses = [];
        private Dictionary<int, RenderPassMetadata> _passMetadata = [];
        private IRuntimeRenderPipelineDebugContext? _ownerPipeline;

        // Dirty-delta swap queue. AddCPU enqueues a command only when the command is dirty AND has
        // not already been queued for this swap cycle (RenderCommand._swapQueued). SwapBuffers
        // walks _renderingSwapQueue exclusively and skips the per-pass walk, which previously cost
        // up to 240 ms/frame on dense scenes. The lists keep their capacity across Clear() calls,
        // so steady-state add+swap does not allocate.
        private List<RenderCommand> _updatingSwapQueue = new(1024);
        private List<RenderCommand> _renderingSwapQueue = new(1024);

        public RenderCommandCollection() { }
        public RenderCommandCollection(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters)
            => SetRenderPasses(passIndicesAndSorters);

        internal void SetOwnerPipeline(IRuntimeRenderPipelineDebugContext pipeline)
        {
            _ownerPipeline = pipeline;
            foreach (KeyValuePair<int, GPURenderPassCollection> pair in _gpuPasses)
                pair.Value.SetDebugContext(_ownerPipeline, pair.Key);
        }

        private readonly Lock _lock = new();

        public int GetUpdatingCommandCount()
        {
            using (_lock.EnterScope())
                return _updatingPasses.Values.Sum(static pass => pass.Count);
        }

        public int GetUpdatingPassCount()
        {
            using (_lock.EnterScope())
                return _updatingPasses.Count;
        }

        public int GetRenderingCommandCount()
        {
            using (_lock.EnterScope())
                return _renderingPasses.Values.Sum(static pass => pass.Count);
        }

        public int GetRenderingPassCommandCount(int renderPass)
        {
            using (_lock.EnterScope())
                return _renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list)
                    ? list.Count
                    : 0;
        }

        public bool TryGetRenderingPassCommands(int renderPass, out IReadOnlyCollection<RenderCommand>? commands)
        {
            using (_lock.EnterScope())
            {
                if (_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) &&
                    list is IReadOnlyCollection<RenderCommand> readOnly)
                {
                    commands = readOnly;
                    return true;
                }
            }

            commands = null;
            return false;
        }

        public void AddRangeCPU(IEnumerable<RenderCommand> renderCommands)
        {
            foreach (RenderCommand renderCommand in renderCommands)
                AddCPU(renderCommand);
        }
        public void AddCPU(RenderCommand item)
        {
            int pass = item.RenderPass;
            if (!_updatingPasses.TryGetValue(pass, out var set))
            {
                if (s_addCpuMissingPassDiagCount < 30)
                {
                    string ownerName = _ownerPipeline?.DebugName ?? "<no-owner>";
                    Debug.Rendering($"[RenderCommandCollection:AddCPU] MISSING_PASS pass={pass} cmd={item.GetType().Name} enabled={item.Enabled} owner={ownerName} updatingPassKeys=[{string.Join(",", _updatingPasses.Keys.OrderBy(static x => x))}]");
                    s_addCpuMissingPassDiagCount++;
                }
                return; // No CPU pass found for this render command
            }

            using (_lock.EnterScope())
            {
                item.SortOrderKey = GetSortOrderKey(pass);
                int beforeCount = set.Count;
                set.Add(item);
                int afterCount = set.Count;
                ++_numCommandsRecentlyAddedToUpdate;
                if (ShouldLogSponzaCpuDiag(item))
                {
                    LogSponzaCpuDiag(
                        afterCount == beforeCount ? "collect-duplicate" : "collect-add",
                        pass,
                        item,
                        camera: null,
                        $"updatingPassCount={afterCount}, dirty={item._dirty}, swapQueued={item._swapQueued}");
                }

                // Dirty-delta enqueue: only swap commands whose state has actually changed since
                // the last publish. _swapQueued dedups across multiple AddCPU calls for the same
                // command this frame (multi-camera, multi-pipeline, multi-pass).
                if ((item._dirty || !item.HasSwappedBuffers) && !item._swapQueued)
                {
                    item._swapQueued = true;
                    _updatingSwapQueue.Add(item);
                }
            }
        }

        private long GetSortOrderKey(int pass)
        {
            long nextValue = _updatingPassSortOrderCounters.TryGetValue(pass, out long currentValue)
                ? currentValue
                : 0L;

            _updatingPassSortOrderCounters[pass] = nextValue + 1L;

            return _passSorterTypes.TryGetValue(pass, out Type? sorterType) && sorterType == typeof(FarToNearRenderCommandSorter)
                ? long.MaxValue - nextValue
                : nextValue;
        }

        public int GetCommandsAddedCount()
        {
            int added = _numCommandsRecentlyAddedToUpdate;
            _numCommandsRecentlyAddedToUpdate = 0;
            return added;
        }

        // Per-thread probe-deferred work list. Probe draws are intentionally deferred to
        // the END of RenderCPU so they test against the COMPLETE depth buffer for this pass
        // (every Visible mesh has already written its depth). Drawing probes inline in the
        // pass iteration produced visible flicker because the probe's outcome depended on
        // whether the future occluder had drawn yet within the same pass iteration.
        [ThreadStatic] private static List<DeferredProbe>? t_deferredProbes;

        private readonly struct DeferredProbe(uint queryKey, in AABB worldBounds)
        {
            public readonly uint QueryKey = queryKey;
            public readonly AABB WorldBounds = worldBounds;
        }

        /// <summary>
        /// Returns true when the CPU occlusion coordinator should be consulted for the
        /// given render pass. Background / pre / post / transparent passes do not have
        /// stable opaque depth semantics for AnySamplesPassedConservative and must be
        /// drawn unconditionally.
        /// </summary>
        private static bool RenderPassIsOcclusionTestable(int renderPass)
        {
            return renderPass == (int)EDefaultRenderPass.OpaqueDeferred
                || renderPass == (int)EDefaultRenderPass.OpaqueForward
                || renderPass == (int)EDefaultRenderPass.MaskedForward;
        }

        internal bool PrepareCpuSoftwareOcclusion(int renderPass, XRCamera? camera)
        {
            if (!CpuSoftwareOcclusionCuller.IsEnabled ||
                camera is null ||
                RuntimeEngine.Rendering.State.IsShadowPass ||
                !RenderPassIsOcclusionTestable(renderPass))
            {
                return false;
            }

            GetActiveViewportSize(out int viewportWidth, out int viewportHeight);
            XRCamera? rightEyeCamera = GetActiveRightEyeCamera();
            if (!s_cpuSoftwareOcclusionCuller.IsFrameInitializedFor(camera, rightEyeCamera, viewportWidth, viewportHeight) ||
                !s_cpuSoftwareOcclusionCuller.HasOccludersFrom(this))
            {
                s_cpuSoftwareOcclusionCuller.BeginFrame(camera, rightEyeCamera, viewportWidth, viewportHeight);
                s_cpuSoftwareOcclusionCuller.SubmitOccludersFromOpaqueCommands(this);
            }

            return s_cpuSoftwareOcclusionCuller.IsFrameOpen;
        }

        internal static bool TestCpuSoftwareOcclusionForGpuSource(GPUScene scene, uint sourceCommandIndex)
        {
            if (!CpuSoftwareOcclusionCuller.IsEnabled ||
                !s_cpuSoftwareOcclusionCuller.IsFrameOpen)
            {
                return true;
            }

            if (!scene.TryGetSourceCommand(sourceCommandIndex, out IRenderCommandMesh? command) || command is null)
                return true;

            if (CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(command))
                return true;

            if (command is not RenderCommand renderCommand || renderCommand.CullingVolume is not AABB bounds)
                return true;

            return s_cpuSoftwareOcclusionCuller.TestVisible(renderCommand.StableQueryKey, bounds);
        }

        public void RenderCPU(
            int renderPass,
            bool skipGpuCommands = false,
            XRCamera? camera = null,
            bool allowExcludedGpuFallbackMeshes = true,
            Action<IRenderCommandMesh>? onExcludedGpuFallbackMesh = null)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            EOcclusionCullingMode occlusionMode = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode;
            bool isShadowPass = RuntimeEngine.Rendering.State.IsShadowPass;
            bool useCpuQueryOcclusion =
                !isShadowPass &&
                camera is not null &&
                occlusionMode == EOcclusionCullingMode.CpuQueryAsync &&
                RenderPassIsOcclusionTestable(renderPass);
            bool useCpuSocOcclusion = PrepareCpuSoftwareOcclusion(renderPass, camera);

            EOcclusionCullingMode appliedOcclusionMode = useCpuQueryOcclusion
                ? EOcclusionCullingMode.CpuQueryAsync
                : useCpuSocOcclusion
                    ? EOcclusionCullingMode.CpuSoftwareOcclusion
                    : EOcclusionCullingMode.Disabled;

            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordActiveMode(
                appliedOcclusionMode,
                EMeshSubmissionStrategy.CpuDirect);

            if (useCpuQueryOcclusion)
            {
                s_cpuOcclusionCoordinator.BeginPass(renderPass, camera!, (uint)list.Count);
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuPassBegin(list.Count);
            }
            else
            {
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuPassSkipped(
                    noCamera: camera is null,
                    shadowPass: isShadowPass,
                    modeOff: occlusionMode != EOcclusionCullingMode.CpuQueryAsync);
            }

            // Phase 2 deferred-probe queue (reused per-thread).
            List<DeferredProbe>? deferredProbes = null;
            if (useCpuQueryOcclusion)
            {
                deferredProbes = t_deferredProbes ??= new List<DeferredProbe>(64);
                deferredProbes.Clear();
            }

            uint cpuCmdIndex = 0;
            foreach (var cmd in list)
            {
                if (skipGpuCommands && cmd is IRenderCommandMesh meshCmd)
                {
                    // Skip mesh commands that should go through GPU dispatch.
                    // Optionally allow opt-out meshes to keep rendering on CPU for diagnostics.
                    var material = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    bool excludedFromGpuIndirect = meshCmd.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;
                    if (!excludedFromGpuIndirect)
                    {
                        LogSponzaCpuDiag("skip-gpu-owned", renderPass, cmd, camera, "skipGpuCommands=True");
                        cpuCmdIndex++;
                        continue;
                    }

                    if (!allowExcludedGpuFallbackMeshes)
                    {
                        LogSponzaCpuDiag("skip-excluded-fallback-disabled", renderPass, cmd, camera, "allowExcludedGpuFallbackMeshes=False");
                        onExcludedGpuFallbackMesh?.Invoke(meshCmd);
                        cpuCmdIndex++;
                        continue;
                    }
                }

                if (useCpuQueryOcclusion && cmd is IRenderCommandMesh occlMesh)
                {
                    // Explicit per-material opt-out (skybox, fullscreen overlays, gizmos
                    // whose AABB / depth contract is unsuitable for AnySamplesPassedConservative).
                    if (CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(occlMesh))
                    {
                        LogSponzaCpuDiag("draw-cpu-query-excluded", renderPass, cmd, camera, "cpu-query-occlusion-excluded");
                        RenderWithGpuScope(cmd, renderPass);
                        cpuCmdIndex++;
                        continue;
                    }

                    // C-CPU-4: key by stable per-command identity, not foreach position.
                    // cpuCmdIndex shifts on every list mutation; StableQueryKey is assigned
                    // at command construction and never changes.
                    uint queryKey = cmd.StableQueryKey;
                    var decision = s_cpuOcclusionCoordinator.ShouldRender(renderPass, queryKey, out bool needsHardwareQuery);

                    if (decision == XREngine.Rendering.Occlusion.ECpuOcclusionDecision.Skip)
                    {
                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("skip-cpu-query", renderPass, cmd, camera, $"queryKey={queryKey}");
                        XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
                        cpuCmdIndex++;
                        continue;
                    }

                    bool cpuSocCull = decision == XREngine.Rendering.Occlusion.ECpuOcclusionDecision.Visible &&
                        needsHardwareQuery &&
                        useCpuSocOcclusion &&
                        cmd.CullingVolume is AABB cpuSocBounds &&
                        !s_cpuSoftwareOcclusionCuller.TestVisible(queryKey, cpuSocBounds);

                    if (decision == XREngine.Rendering.Occlusion.ECpuOcclusionDecision.ProbeOnly || cpuSocCull)
                    {
                        // Only the first pass to see this command in the frame actually
                        // emits the probe AABB; later passes (e.g. color pass after a
                        // depth-normal prepass) reuse the same query result. CPU SOC
                        // culls use the same deferred query path so the async query cache
                        // still gets a hardware-visible/not-visible answer.
                        if (!needsHardwareQuery)
                        {
                            // Cull telemetry: visually the mesh contributes no color this frame.
                            if (ShouldLogSponzaCpuDiag(cmd))
                                LogSponzaCpuDiag("skip-cpu-query-cached", renderPass, cmd, camera, $"queryKey={queryKey}, cpuSocCull={cpuSocCull}");
                            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
                            cpuCmdIndex++;
                            continue;
                        }

                        var probeBounds = cmd.CullingVolume;
                        if (probeBounds.HasValue && deferredProbes is not null)
                        {
                            if (CpuQueryProxyIsNearPlaneUnsafe(camera!, probeBounds.Value))
                            {
                                s_cpuOcclusionCoordinator.ForceVisible(renderPass, queryKey);
                                if (ShouldLogSponzaCpuDiag(cmd))
                                    LogSponzaCpuDiag("draw-cpu-query-near-plane", renderPass, cmd, camera, $"queryKey={queryKey}");
                                RenderWithGpuScope(cmd, renderPass);
                                cpuCmdIndex++;
                                continue;
                            }

                            // DEFERRED: probe is queued and issued after the visible-mesh
                            // loop completes so it tests against complete-depth, not the
                            // partial depth that would exist at this command's iteration
                            // point. This eliminates render-order false positives.
                            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
                            if (ShouldLogSponzaCpuDiag(cmd))
                                LogSponzaCpuDiag("skip-cpu-query-probe", renderPass, cmd, camera, $"queryKey={queryKey}, cpuSocCull={cpuSocCull}");
                            deferredProbes.Add(new DeferredProbe(queryKey, probeBounds.Value));
                            cpuCmdIndex++;
                            continue;
                        }
                        // No bounds available — fall through to full-mesh requery so the
                        // query can still refresh (correctness fallback; will flicker).
                    }

                    // Visible draw path. We deliberately do NOT bracket the mesh's own
                    // Render() call with the occlusion query: AnySamplesPassedConservative
                    // around a self-draw reports "did this draw contribute samples", which
                    // is a self-visibility test rather than an occlusion test. The mesh's
                    // first-drawn-before-occluder case (common across material buckets)
                    // would then permanently latch LastAnySamplesPassed=true and the mesh
                    // would never demote to Skip. Instead, always route the hardware query
                    // through the deferred-probe queue so it tests a proxy AABB against
                    // the pass's complete depth — matching the GPU-dispatch occlusion path.
                    if (needsHardwareQuery &&
                        deferredProbes is not null &&
                        cmd.CullingVolume is AABB visibleProbeBounds)
                    {
                        if (CpuQueryProxyIsNearPlaneUnsafe(camera!, visibleProbeBounds))
                            s_cpuOcclusionCoordinator.ForceVisible(renderPass, queryKey);
                        else
                            deferredProbes!.Add(new DeferredProbe(queryKey, visibleProbeBounds));

                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("draw-cpu-query-visible", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery=True");
                        RenderWithGpuScope(cmd, renderPass);
                    }
                    else
                    {
                        // Fallback: command has no AABB, so we can't issue a proxy probe.
                        // Bracket the mesh draw directly. This retains the self-visibility
                        // limitation for the no-bounds case (acceptable: such commands are
                        // typically full-screen overlays, skybox, debug lines, where
                        // occlusion culling isn't meaningful anyway and they're usually
                        // excluded via CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded).
                        if (needsHardwareQuery)
                            s_cpuOcclusionCoordinator.BeginQuery(renderPass, queryKey);
                        try
                        {
                            if (ShouldLogSponzaCpuDiag(cmd))
                                LogSponzaCpuDiag("draw-cpu-query-direct", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery={needsHardwareQuery}");
                            RenderWithGpuScope(cmd, renderPass);
                        }
                        finally
                        {
                            if (needsHardwareQuery)
                                s_cpuOcclusionCoordinator.EndQuery(renderPass, queryKey);
                        }
                    }

                    cpuCmdIndex++;
                    continue;
                }

                if (!useCpuQueryOcclusion && useCpuSocOcclusion && cmd is IRenderCommandMesh socMesh &&
                    !CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(socMesh) &&
                    cmd.CullingVolume is AABB socBounds &&
                    !s_cpuSoftwareOcclusionCuller.TestVisible(cmd.StableQueryKey, socBounds))
                {
                    if (ShouldLogSponzaCpuDiag(cmd))
                        LogSponzaCpuDiag("skip-cpu-soc", renderPass, cmd, camera, $"queryKey={cmd.StableQueryKey}");
                    cpuCmdIndex++;
                    continue;
                }

                cpuCmdIndex++;

                LogSponzaCpuDiag("draw-cpu", renderPass, cmd, camera, "occlusion=Disabled");
                RenderWithGpuScope(cmd, renderPass);
            }

            // Phase 3: deferred probe-only AABB draws. Now the depth buffer reflects all
            // visible meshes from this pass, so the conservative samples-passed query
            // result is a faithful "is this mesh's AABB exposed to the camera?" answer.
            if (deferredProbes is { Count: > 0 })
            {
                foreach (var probe in deferredProbes)
                {
                    s_cpuOcclusionCoordinator.BeginQuery(renderPass, probe.QueryKey);
                    try
                    {
                        XREngine.Rendering.Occlusion.CpuOcclusionProxyRenderer.Draw(probe.WorldBounds);
                    }
                    finally
                    {
                        s_cpuOcclusionCoordinator.EndQuery(renderPass, probe.QueryKey);
                    }
                }
                deferredProbes.Clear();
            }
        }

        private static bool CpuQueryProxyIsNearPlaneUnsafe(XRCamera camera, AABB bounds)
        {
            float tolerance = MathF.Max(0.001f, camera.NearZ * 0.05f);

            if (bounds.ContainsPoint(camera.Transform.RenderTranslation, tolerance))
                return true;

            Vector3 min = bounds.Min;
            Vector3 max = bounds.Max;
            float minDistance = float.PositiveInfinity;
            float maxDistance = float.NegativeInfinity;

            AccumulateRenderNearPlaneDistance(camera, new Vector3(min.X, min.Y, min.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(max.X, min.Y, min.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(min.X, max.Y, min.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(max.X, max.Y, min.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(min.X, min.Y, max.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(max.X, min.Y, max.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(min.X, max.Y, max.Z), ref minDistance, ref maxDistance);
            AccumulateRenderNearPlaneDistance(camera, new Vector3(max.X, max.Y, max.Z), ref minDistance, ref maxDistance);

            return minDistance <= tolerance && maxDistance >= -tolerance;
        }

        private static void AccumulateRenderNearPlaneDistance(
            XRCamera camera,
            Vector3 corner,
            ref float minDistance,
            ref float maxDistance)
        {
            float distance = camera.DistanceFromRenderNearPlane(corner);
            minDistance = MathF.Min(minDistance, distance);
            maxDistance = MathF.Max(maxDistance, distance);
        }

        public void RenderCPUMeshOnly(int renderPass)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            foreach (var cmd in list)
                if (cmd is IRenderCommandMesh)
                    RenderWithGpuScope(cmd, renderPass);
        }

        /// <summary>
        /// Renders only the commands in the specified pass that the GPU indirect dispatch path
        /// cannot handle on its own: non-mesh commands (debug overlays, UI, etc.) and mesh commands
        /// explicitly marked as ExcludeFromGpuIndirect / ForceCpuRendering. This is the preferred
        /// prefilter for GPU-driven render passes (zero-readback, instrumented indirect, meshlet)
        /// because it skips the full RenderCPU pipeline — no CPU-occlusion BeginPass allocation,
        /// no per-mesh skip iteration accounting, and no excluded-fallback warning machinery — all
        /// of which are wasted CPU work when the GPU owns mesh dispatch.
        /// </summary>
        public void RenderCPUNonMeshAndExcluded(int renderPass)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            foreach (var cmd in list)
            {
                if (cmd is null)
                    continue;

                if (cmd is IRenderCommandMesh meshCmd)
                {
                    var material = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    bool excludedFromGpuIndirect = meshCmd.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true;
                    if (!excludedFromGpuIndirect)
                        continue;
                }

                RenderWithGpuScope(cmd, renderPass);
            }
        }

        /// <summary>
        /// Renders only commands in the specified pass that satisfy the given predicate.
        /// </summary>
        public void RenderCPUFiltered(int renderPass, Predicate<RenderCommand> filter)
            => RenderCPUFiltered(renderPass, filter, respectCpuQueryOcclusion: false);

        /// <summary>
        /// Filtered CPU render that can optionally consult the CPU-query occlusion coordinator
        /// (non-mutating peek). Used by secondary debug passes (e.g. Full Overdraw) so that the
        /// visualization reflects the same visibility set as the primary mesh pass.
        /// </summary>
        public void RenderCPUFiltered(int renderPass, Predicate<RenderCommand> filter, bool respectCpuQueryOcclusion)
        {
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            bool useCpuQueryOcclusion =
                respectCpuQueryOcclusion &&
                !RuntimeEngine.Rendering.State.IsShadowPass &&
                RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync;

            uint cpuCmdIndex = 0;
            foreach (var cmd in list)
            {
                if (cmd is null)
                {
                    cpuCmdIndex++;
                    continue;
                }

                if (!filter(cmd))
                {
                    cpuCmdIndex++;
                    continue;
                }

                if (useCpuQueryOcclusion && cmd is IRenderCommandMesh)
                {
                    // C-CPU-4: stable per-command identity, matches primary RenderCPU keying.
                    if (!s_cpuOcclusionCoordinator.PeekShouldRender(renderPass, cmd.StableQueryKey))
                    {
                        cpuCmdIndex++;
                        continue;
                    }
                }

                RenderWithGpuScope(cmd, renderPass);
                cpuCmdIndex++;
            }
        }

        private static void RenderWithGpuScope(RenderCommand? command, int renderPass)
        {
            if (command is null)
                return;

            RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
            if (!profiler.IsProfilingActive || ShouldSkipGpuScope(command))
            {
                command.Render();
                return;
            }

            using (profiler.StartScope(BuildRenderCommandGpuScopeName(renderPass, command)))
                command.Render();
        }

        private static bool ShouldSkipGpuScope(RenderCommand command)
            => RuntimeRenderingHostServices.Current.IsShadowPass && command is IRenderCommandMesh;

        private static string BuildRenderCommandGpuScopeName(int renderPass, RenderCommand command)
        {
            if (command is IRenderCommandMesh meshCommand)
                return BuildMeshDrawGpuScopeName(renderPass, meshCommand);

            string passName = GetRenderPassDisplayName(renderPass);
            string commandName = command is RenderCommandMethod2D methodCommand
                ? methodCommand.GetGpuProfilingLabel()
                : command.GetType().Name;

            return $"RenderCommand[{passName}; {SanitizeGpuScopeLabel(commandName)}]";
        }

        private static string BuildMeshDrawGpuScopeName(int renderPass, IRenderCommandMesh meshCommand)
        {
            XRMeshRenderer? meshRenderer = meshCommand.Mesh;
            XRMaterial? material = meshCommand.MaterialOverride ?? meshRenderer?.Material;
            string passName = GetRenderPassDisplayName(renderPass);
            string rawMeshName = meshRenderer?.Mesh?.Name ?? meshRenderer?.Name ?? string.Empty;
            string rawMaterialName = material?.Name ?? string.Empty;
            string shaderName = SanitizeGpuScopeLabel(GetMaterialShaderDisplayName(material));
            string meshName = SanitizeGpuScopeLabel(string.IsNullOrWhiteSpace(rawMeshName) ? "<unnamed-mesh>" : rawMeshName);
            string materialName = SanitizeGpuScopeLabel(string.IsNullOrWhiteSpace(rawMaterialName) ? "<unnamed-material>" : rawMaterialName);
            string commandLabel = GetMeshCommandGpuScopeLabel(meshCommand, rawMeshName, rawMaterialName, shaderName);
            string commandSegment = string.IsNullOrWhiteSpace(commandLabel)
                ? string.Empty
                : $"source={SanitizeGpuScopeLabel(commandLabel)}; ";

            return string.IsNullOrWhiteSpace(shaderName)
                ? $"MeshDraw[{passName}; {commandSegment}mesh={meshName}; material={materialName}]"
                : $"MeshDraw[{passName}; {commandSegment}mesh={meshName}; material={materialName}; shader={shaderName}]";
        }

        private static string GetMeshCommandGpuScopeLabel(
            IRenderCommandMesh meshCommand,
            string? meshName,
            string? materialName,
            string? shaderName)
        {
            string? label = meshCommand switch
            {
                RenderCommandMesh3D mesh3D => mesh3D.GpuProfilingLabel,
                RenderCommandMesh2D mesh2D => mesh2D.GpuProfilingLabel,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(label))
                return label!;

            bool hasNamedResource =
                !string.IsNullOrWhiteSpace(meshName) ||
                !string.IsNullOrWhiteSpace(materialName) ||
                !string.IsNullOrWhiteSpace(shaderName);
            if (hasNamedResource || meshCommand is not RenderCommand command)
                return string.Empty;

            return $"{command.GetType().Name}#{command.StableQueryKey}";
        }

        private static string GetRenderPassDisplayName(int renderPass)
            => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
                ? ((EDefaultRenderPass)renderPass).ToString()
                : renderPass.ToString();

        private static string GetMaterialShaderDisplayName(XRMaterial? material)
        {
            if (material is null)
                return string.Empty;

            IReadOnlyList<XRShader> fragmentShaders = material.FragmentShaders;
            XRShader? fragmentShader = fragmentShaders.Count > 0
                ? fragmentShaders[fragmentShaders.Count - 1]
                : null;

            if (fragmentShader is null)
                return string.Empty;

            string? path = fragmentShader.Source?.FilePath ?? fragmentShader.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFileName(path);

            return fragmentShader.Name ?? string.Empty;
        }

        private static string SanitizeGpuScopeLabel(string label)
            => string.IsNullOrWhiteSpace(label)
                ? "<unnamed>"
                : label.Replace('\n', ' ').Replace('\r', ' ');

        public void RenderGPU(int renderPass)
            => RenderGPU(renderPass, RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true));

        public void RenderGPU(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy)
        {
            if (!_gpuPasses.TryGetValue(renderPass, out GPURenderPassCollection? gpuPass))
                return;

            if (!HasGpuEligibleMeshCommands(renderPass))
                return;
            
            IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
            if (renderState is null)
                return;

            IRuntimeRenderCamera? camera = renderState.RenderingCamera ?? renderState.SceneCamera;
            if (camera is null)
                return;

            var scene = renderState.RenderingScene;
            if (scene is null)
                return;

            if (meshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectInstrumented &&
                camera is XRCamera xrCamera)
            {
                PrepareCpuSoftwareOcclusion(renderPass, xrCamera);
            }

            bool meshletStrategy = meshSubmissionStrategy.IsAnyMeshletStrategy();
            bool previousUseMeshletPipeline = gpuPass.UseMeshletPipeline;
            if (meshletStrategy)
                gpuPass.UseMeshletPipeline = true;

            try
            {
                gpuPass.MeshSubmissionStrategy = meshSubmissionStrategy;
                ConfigureGpuViewSet(gpuPass, renderState, camera);

                if (meshletStrategy && scene is GPUScene gpuScene)
                    gpuScene.EnsureRuntimeMeshletPayloadsForMeshletDispatch();

                scene.RenderGpuPass(gpuPass);

                gpuPass.GetVisibleCounts(out uint draws, out uint instances, out _);
                scene.RecordGpuVisibility(draws, instances);

                bool allowPerViewReadback = meshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectInstrumented &&
                    RuntimeRenderingHostServices.Current.EnableGpuIndirectDebugLogging;
                if (allowPerViewReadback && gpuPass.ActiveViewCount > 0)
                {
                    uint leftDraws = gpuPass.ReadPerViewDrawCount(0u);
                    uint rightDraws = gpuPass.ActiveViewCount > 1u
                        ? gpuPass.ReadPerViewDrawCount(1u)
                        : 0u;
                    RuntimeRenderingHostServices.Current.RecordVrPerViewDrawCounts(leftDraws, rightDraws);
                }
            }
            finally
            {
                if (meshletStrategy)
                    gpuPass.UseMeshletPipeline = previousUseMeshletPipeline;
            }
        }

        public bool HasRenderingCommands(int renderPass)
        {
            using (_lock.EnterScope())
            {
                return _renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) &&
                    list.Count > 0;
            }
        }

        public bool HasGpuEligibleMeshCommands(int renderPass)
        {
            using (_lock.EnterScope())
            {
                if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) || list.Count == 0)
                    return false;

                foreach (var cmd in list)
                {
                    if (cmd is not IRenderCommandMesh meshCmd)
                        continue;

                    var material = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    if (meshCmd.ForceCpuRendering || material?.RenderOptions?.ExcludeFromGpuIndirect == true)
                        continue;

                    return true;
                }
            }

            return false;
        }

        private static void GetActiveViewportSize(out int width, out int height)
        {
            IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
            width = Math.Max(1, renderState?.WindowViewport?.InternalWidth ?? renderState?.WindowViewport?.Width ?? RuntimeEngine.EffectiveSettings.CpuSocBufferWidth);
            height = Math.Max(1, renderState?.WindowViewport?.InternalHeight ?? renderState?.WindowViewport?.Height ?? RuntimeEngine.EffectiveSettings.CpuSocBufferHeight);
        }

        private static bool IsStereoRenderPassActive()
            => RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState?.StereoPass == true;

        private static XRCamera? GetActiveRightEyeCamera()
            => RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState?.StereoRightEyeCamera as XRCamera;

        private static void ConfigureGpuViewSet(GPURenderPassCollection gpuPass, IRuntimeRenderCommandExecutionState renderState, IRuntimeRenderCamera leftCamera)
        {
            IRuntimeRenderingHostServices hostServices = RuntimeRenderingHostServices.Current;
            IRuntimeRenderCamera? rightCamera = renderState.StereoPass ? renderState.StereoRightEyeCamera : null;
            bool stereo = renderState.StereoPass && rightCamera is not null;
            bool includeMirror = hostServices.RenderWindowsWhileInVR && !hostServices.VrMirrorComposeFromEyeTextures;
            bool includeFoveated = stereo && hostServices.EnableVrFoveatedViewSet;
            float foveationOuter = Math.Clamp(
                hostServices.VrFoveationOuterRadius + hostServices.VrFoveationVisibilityMargin,
                hostServices.VrFoveationInnerRadius,
                1.5f);
            float fullResNearDistance = hostServices.VrFoveationForceFullResForUiAndNearField
                ? hostServices.VrFoveationFullResNearDistanceMeters
                : 0.0f;

            int viewCount = stereo ? 2 : 1;
            if (includeFoveated)
                viewCount += 2;
            if (includeMirror)
                viewCount += 1;

            Span<GPUViewDescriptor> descriptors = stackalloc GPUViewDescriptor[5];
            Span<GPUViewConstants> constants = stackalloc GPUViewConstants[5];

            int width = Math.Max(1, renderState.WindowViewport?.InternalWidth ?? renderState.WindowViewport?.Width ?? 1);
            int height = Math.Max(1, renderState.WindowViewport?.InternalHeight ?? renderState.WindowViewport?.Height ?? 1);

            uint passBit = gpuPass.RenderPass >= 0 && gpuPass.RenderPass < 32
                ? (1u << gpuPass.RenderPass)
                : uint.MaxValue;

            uint cursor = 0u;
            descriptors[(int)cursor] = CreateViewDescriptor(
                cursor,
                GPUViewSetLayout.InvalidViewId,
                GPUViewFlags.StereoEyeLeft | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility,
                passBit,
                0u,
                0,
                0,
                width,
                height,
                0u,
                gpuPass.CommandCapacity);
            constants[(int)cursor] = CreateViewConstants(leftCamera);
            cursor++;

            if (stereo && rightCamera is not null)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    GPUViewSetLayout.InvalidViewId,
                    GPUViewFlags.StereoEyeRight | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    1u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity,
                    gpuPass.CommandCapacity);
                constants[(int)cursor] = CreateViewConstants(rightCamera);
                cursor++;
            }

            if (includeFoveated)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    0u,
                    GPUViewFlags.StereoEyeLeft | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    0u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity * cursor,
                    gpuPass.CommandCapacity);
                descriptors[(int)cursor].FoveationA = new Vector4(
                    hostServices.VrFoveationCenterUv,
                    hostServices.VrFoveationInnerRadius,
                    foveationOuter);
                descriptors[(int)cursor].FoveationB = new Vector4(
                    hostServices.VrFoveationShadingRates,
                    fullResNearDistance);
                constants[(int)cursor] = CreateViewConstants(leftCamera);
                cursor++;

                if (rightCamera is not null)
                {
                    descriptors[(int)cursor] = CreateViewDescriptor(
                        cursor,
                        1u,
                        GPUViewFlags.StereoEyeRight | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility,
                        passBit,
                        1u,
                        0,
                        0,
                        width,
                        height,
                        gpuPass.CommandCapacity * cursor,
                        gpuPass.CommandCapacity);
                    descriptors[(int)cursor].FoveationA = new Vector4(
                        hostServices.VrFoveationCenterUv,
                        hostServices.VrFoveationInnerRadius,
                        foveationOuter);
                    descriptors[(int)cursor].FoveationB = new Vector4(
                        hostServices.VrFoveationShadingRates,
                        fullResNearDistance);
                    constants[(int)cursor] = CreateViewConstants(rightCamera);
                    cursor++;
                }
            }

            if (includeMirror)
            {
                descriptors[(int)cursor] = CreateViewDescriptor(
                    cursor,
                    0u,
                    GPUViewFlags.Mirror | GPUViewFlags.UsesSharedVisibility,
                    passBit,
                    0u,
                    0,
                    0,
                    width,
                    height,
                    gpuPass.CommandCapacity * cursor,
                    gpuPass.CommandCapacity);
                constants[(int)cursor] = CreateViewConstants(leftCamera);
                cursor++;
            }

            ValidateViewDescriptorLayout(descriptors.Slice(0, (int)cursor), gpuPass.CommandCapacity);
            gpuPass.ConfigureViewSet(descriptors.Slice(0, (int)cursor), constants.Slice(0, (int)cursor));
            uint requestedSourceView = DetermineIndirectSourceViewId(renderState, leftCamera, rightCamera);
            gpuPass.SetIndirectSourceViewId(requestedSourceView);

            if (requestedSourceView != gpuPass.IndirectSourceViewId)
            {
                throw new InvalidOperationException(
                    $"Indirect source view id {requestedSourceView} was clamped to {gpuPass.IndirectSourceViewId} for active views {gpuPass.ActiveViewCount}.");
            }
        }

        private static void ValidateViewDescriptorLayout(ReadOnlySpan<GPUViewDescriptor> descriptors, uint commandCapacity)
        {
            uint expectedOffset = 0u;
            for (int i = 0; i < descriptors.Length; i++)
            {
                GPUViewDescriptor descriptor = descriptors[i];
                if (descriptor.ViewId != (uint)i)
                    throw new InvalidOperationException($"View descriptor order mismatch at index {i}; found ViewId={descriptor.ViewId}.");

                if (descriptor.VisibleOffset != expectedOffset)
                {
                    throw new InvalidOperationException(
                        $"View {descriptor.ViewId} visible offset {descriptor.VisibleOffset} does not match expected {expectedOffset}.");
                }

                if (descriptor.VisibleCapacity != commandCapacity)
                {
                    throw new InvalidOperationException(
                        $"View {descriptor.ViewId} visible capacity {descriptor.VisibleCapacity} does not match command capacity {commandCapacity}.");
                }

                expectedOffset += commandCapacity;
            }
        }

        private static uint DetermineIndirectSourceViewId(
            IRuntimeRenderCommandExecutionState renderState,
            IRuntimeRenderCamera sceneCamera,
            IRuntimeRenderCamera? stereoRightCamera)
        {
            if (!renderState.StereoPass || stereoRightCamera is null)
                return 0u;

            if (sceneCamera.StereoEyeLeft.HasValue)
                return sceneCamera.StereoEyeLeft.Value ? 0u : 1u;

            if (ReferenceEquals(sceneCamera, stereoRightCamera))
                return 1u;

            return 0u;
        }

        private static GPUViewDescriptor CreateViewDescriptor(
            uint viewId,
            uint parentViewId,
            GPUViewFlags flags,
            uint passMaskLo,
            uint outputLayer,
            int rectX,
            int rectY,
            int rectW,
            int rectH,
            uint visibleOffset,
            uint visibleCapacity)
        {
            return new GPUViewDescriptor
            {
                ViewId = viewId,
                ParentViewId = parentViewId,
                Flags = (uint)flags,
                RenderPassMaskLo = passMaskLo,
                RenderPassMaskHi = 0u,
                OutputLayer = outputLayer,
                ViewRectX = (uint)Math.Max(0, rectX),
                ViewRectY = (uint)Math.Max(0, rectY),
                ViewRectW = (uint)Math.Max(1, rectW),
                ViewRectH = (uint)Math.Max(1, rectH),
                VisibleOffset = visibleOffset,
                VisibleCapacity = Math.Max(1u, visibleCapacity),
                FoveationA = Vector4.Zero,
                FoveationB = Vector4.Zero
            };
        }

        private static GPUViewConstants CreateViewConstants(IRuntimeRenderCamera camera)
        {
            Matrix4x4 view = camera.Transform.InverseRenderMatrix;
            Matrix4x4 projection = camera.ProjectionMatrix;
            Matrix4x4 viewProjection = view * projection;
            Vector3 cameraPos = camera.Transform.RenderTranslation;
            Vector3 cameraForward = camera.Transform.RenderForward;

            // Use previous-frame VP from the temporal accumulation pass when available,
            // so the GPU-driven path has correct motion history for motion vectors.
            Matrix4x4 prevViewProjection = VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporal) && temporal.HistoryReady
                ? temporal.PrevViewProjectionUnjittered
                : viewProjection;

            return new GPUViewConstants
            {
                View = view,
                Projection = projection,
                ViewProjection = viewProjection,
                PrevViewProjection = prevViewProjection,
                CameraPositionAndNear = new Vector4(cameraPos, camera.NearZ),
                CameraForwardAndFar = new Vector4(cameraForward, camera.FarZ)
            };
        }

        public bool TryGetGpuPass(int renderPass, out GPURenderPassCollection gpuPass)
            => _gpuPasses.TryGetValue(renderPass, out gpuPass!);

        public void SwapBuffers()
        {
            using var sample = RuntimeEngine.Profiler.Start("RenderCommandCollection.SwapBuffers");

            (_updatingPasses, _renderingPasses) = (_renderingPasses, _updatingPasses);
            (_updatingSwapQueue, _renderingSwapQueue) = (_renderingSwapQueue, _updatingSwapQueue);

            using (RuntimeEngine.Profiler.Start("RenderCommandCollection.SwapBuffers.RenderPasses"))
            {
                // Dirty-delta publish: walk only the commands that mutated since the last swap.
                // Skip commands whose dirty bit was already cleared by another collection sharing
                // this command (multi-viewport scenes publish through whichever collection swaps
                // first; subsequent collections in the same frame are no-ops because the snapshot
                // fields live on the RenderCommand instance itself).
                var queue = _renderingSwapQueue;
                int queueCount = queue.Count;
                for (int i = 0; i < queueCount; i++)
                {
                    var cmd = queue[i];
                    if (cmd is null)
                        continue;
                    if (cmd._dirty || !cmd.HasSwappedBuffers)
                        cmd.SwapBuffers();
                    else
                        cmd._swapQueued = false;
                }
                queue.Clear();
            }

            using (RuntimeEngine.Profiler.Start("RenderCommandCollection.SwapBuffers.ClearPasses"))
            {
                foreach (var pass in _updatingPasses.Values)
                    pass.Clear();
            }

            // Reset the per-pass sort-order counters. Keys can be added by AddCPU on other
            // threads in the future, so we snapshot before mutating values. The collection is
            // typically small (one entry per render pass) so the ToArray() cost is negligible.
            if (_updatingPassSortOrderCounters.Count > 0)
            {
                foreach (int passIndex in _updatingPassSortOrderCounters.Keys.ToArray())
                    _updatingPassSortOrderCounters[passIndex] = 0L;
            }

            _numCommandsRecentlyAddedToUpdate = 0;
        }

        public IEnumerable<IRenderCommandMesh> EnumerateRenderingMeshCommands()
        {
            foreach (var pass in _renderingPasses.Values)
            {
                foreach (var cmd in pass)
                {
                    if (cmd is IRenderCommandMesh meshCmd)
                        yield return meshCmd;
                }
            }
        }

        public bool TryGetPassMetadata(int passIndex, out RenderPassMetadata metadata)
            => _passMetadata.TryGetValue(passIndex, out metadata!);

        public IReadOnlyDictionary<int, RenderPassMetadata> PassMetadata => _passMetadata;

        private static bool SponzaCpuDiagEnabled
        {
            get
            {
#if DEBUG || EDITOR
                return RenderDiagnosticsFlags.ModelRenderDiagEnabled && Volatile.Read(ref s_sponzaCpuDiagLines) < SponzaCpuDiagMaxLines;
#else
                return false;
#endif
            }
        }

        private static void LogSponzaCpuDiag(string phase, int renderPass, RenderCommand cmd, XRCamera? camera, string detail)
        {
            if (!ShouldLogSponzaCpuDiag(cmd) || cmd is not RenderCommandMesh3D meshCommand)
                return;

            int line = Interlocked.Increment(ref s_sponzaCpuDiagLines);
            if (line > SponzaCpuDiagMaxLines)
                return;

            XRMeshRenderer? renderer = meshCommand.Mesh;
            var material = meshCommand.MaterialOverride ?? renderer?.Material;
            XRMesh? mesh = renderer?.Mesh;
            Debug.Rendering(
                "[SponzaFlickerDiag.CPU] frame={0} phase={1} line={2} cmd={3} stable={4} pass={5} cmdPass={6} enabled={7} renderEnabled={8} forceCpu={9} instances={10} sortKey={11} distance={12:F3} camera={13} sourceSubMesh='{14}' mesh='{15}' material='{16}' detail='{17}'",
                RuntimeEngine.Rendering.State.RenderFrameId,
                phase,
                line,
                RuntimeHelpers.GetHashCode(cmd),
                cmd.StableQueryKey,
                renderPass,
                cmd.RenderPass,
                cmd.Enabled,
                cmd.RenderEnabled,
                meshCommand.ForceCpuRendering,
                meshCommand.Instances,
                cmd.SortOrderKey,
                meshCommand.RenderDistance,
                camera?.GetHashCode().ToString() ?? "<null>",
                renderer?.SourceSubMeshAsset?.Name ?? "<null>",
                mesh?.Name ?? "<null>",
                material?.Name ?? "<null>",
                detail);
        }

        private static bool ShouldLogSponzaCpuDiag(RenderCommand cmd)
            => SponzaCpuDiagEnabled &&
               cmd is RenderCommandMesh3D meshCommand &&
               IsSponzaCommand(meshCommand);

        private static bool IsSponzaCommand(RenderCommandMesh3D command)
        {
            XRMeshRenderer? renderer = command.Mesh;
            var material = command.MaterialOverride ?? renderer?.Material;
            return ContainsSponzaToken(renderer?.SourceSubMeshAsset?.Name) ||
                   ContainsSponzaToken(renderer?.Mesh?.Name) ||
                   ContainsSponzaToken(material?.Name);
        }

        private static bool ContainsSponzaToken(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Contains("sponza", StringComparison.OrdinalIgnoreCase);

        public bool ValidatePassMetadata()
        {
            bool valid = true;

            foreach (var (passIndex, passMetadata) in _passMetadata)
            {
                if (!_gpuPasses.ContainsKey(passIndex))
                {
                    Debug.LogWarning($"Render pass metadata references index {passIndex} but no GPU pass exists. Metadata={passMetadata.Name}");
                    valid = false;
                }

                foreach (var usage in passMetadata.ResourceUsages)
                {
                    if (usage.ResourceType is ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.DepthAttachment)
                    {
                        string resourceName = usage.ResourceName;
                        if (!resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase) &&
                            !resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogWarning($"Pass {passMetadata.Name} references attachment '{resourceName}' that doesn't use fbo:: naming.");
                            valid = false;
                        }
                    }
                }
            }

            return valid;
        }
    }
}
