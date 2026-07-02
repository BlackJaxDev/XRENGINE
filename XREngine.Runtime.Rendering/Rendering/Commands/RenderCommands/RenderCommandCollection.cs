using XREngine.Extensions;
using System.Collections;
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
    /// Stores sorted render-pass membership using sort keys captured at collection time.
    /// The render command instances are shared by desktop, VR eye, shadow, and capture
    /// viewports; their live RenderDistance/SortOrderKey fields can be updated by another
    /// camera while this collection is being rendered. A SortedSet over the command itself
    /// is therefore unsafe because its comparison key mutates under the tree.
    /// </summary>
    internal sealed class SnapshotSortedRenderCommandCollection : ICollection<RenderCommand>, IReadOnlyCollection<RenderCommand>
    {
        private readonly List<Entry> _entries = [];
        private readonly HashSet<RenderCommand> _membership = new(ReferenceRenderCommandComparer.Instance);
        private readonly bool _farToNear;
        private bool _sortDirty;

        public SnapshotSortedRenderCommandCollection(IComparer<RenderCommand> sorter)
            => _farToNear = sorter.GetType() == typeof(FarToNearRenderCommandSorter);

        public int Count => _entries.Count;
        public bool IsReadOnly => false;

        public void Add(RenderCommand item)
            => Add(item, item.SortOrderKey);

        public void Add(RenderCommand item, long sortOrderKey)
        {
            if (!_membership.Add(item))
                return;

            _entries.Add(Entry.Capture(item, sortOrderKey));
            _sortDirty = true;
        }

        public void Clear()
        {
            _entries.Clear();
            _membership.Clear();
            _sortDirty = false;
        }

        public bool Contains(RenderCommand item)
            => _membership.Contains(item);

        public void CopyTo(RenderCommand[] array, int arrayIndex)
        {
            EnsureSorted();
            for (int i = 0; i < _entries.Count; i++)
                array[arrayIndex + i] = _entries[i].Command;
        }

        public bool Remove(RenderCommand item)
        {
            if (!_membership.Remove(item))
                return false;

            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i].Command, item))
                {
                    _entries.RemoveAt(i);
                    break;
                }
            }

            return true;
        }

        public Enumerator GetEnumerator()
        {
            EnsureSorted();
            return new Enumerator(_entries);
        }

        IEnumerator<RenderCommand> IEnumerable<RenderCommand>.GetEnumerator()
            => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

        private void EnsureSorted()
        {
            if (!_sortDirty)
                return;

            _entries.Sort(CompareEntries);
            _sortDirty = false;
        }

        private int CompareEntries(Entry x, Entry y)
        {
            int result = x.RenderDistance.CompareTo(y.RenderDistance);
            if (result == 0)
                result = x.SortOrderKey.CompareTo(y.SortOrderKey);
            if (result == 0)
                result = x.IdentityHash.CompareTo(y.IdentityHash);

            return _farToNear ? -result : result;
        }

        internal readonly record struct Entry(
            RenderCommand Command,
            float RenderDistance,
            long SortOrderKey,
            int IdentityHash)
        {
            public static Entry Capture(RenderCommand command, long sortOrderKey)
                => new(
                    command,
                    GetRenderDistance(command),
                    sortOrderKey,
                    RuntimeHelpers.GetHashCode(command));

            private static float GetRenderDistance(RenderCommand command)
                => command switch
                {
                    RenderCommand3D command3D => command3D.RenderDistance,
                    RenderCommand2D command2D => command2D.ZIndex,
                    _ => 0.0f
                };
        }

        private sealed class ReferenceRenderCommandComparer : IEqualityComparer<RenderCommand>
        {
            public static readonly ReferenceRenderCommandComparer Instance = new();

            public bool Equals(RenderCommand? x, RenderCommand? y)
                => ReferenceEquals(x, y);

            public int GetHashCode(RenderCommand obj)
                => RuntimeHelpers.GetHashCode(obj);
        }

        public struct Enumerator : IEnumerator<RenderCommand>
        {
            private readonly List<Entry> _entries;
            private int _index = -1;

            internal Enumerator(List<Entry> entries)
            {
                _entries = entries;
            }

            public readonly RenderCommand Current => _entries[_index].Command;
            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                int nextIndex = _index + 1;
                if (nextIndex >= _entries.Count)
                    return false;

                _index = nextIndex;
                return true;
            }

            public void Reset()
                => _index = -1;

            public readonly void Dispose()
            {
            }
        }
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

        public bool IsShadowPass => IsOwnedByShadowPipeline;
        public void SetRenderPasses(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters, IEnumerable<RenderPassMetadata>? passMetadata = null)
        {
            using (_lock.EnterScope())
            {
                using var renderingBufferScope = EnterRenderingBufferWriteScope();

                Dictionary<int, RenderPassMetadata> incomingPassMetadata = BuildPassMetadata(passMetadata);
                EnsureDefaultPassMetadata(passIndicesAndSorters.Keys, incomingPassMetadata);
                if (HasEquivalentPassConfiguration(passIndicesAndSorters, incomingPassMetadata))
                    return;

                string ownerName = _ownerPipeline?.DebugName ?? "<no-owner>";
                Debug.Rendering($"[RenderCommandCollection] SetRenderPasses called. Owner={ownerName} PassCount={passIndicesAndSorters.Count} Keys=[{string.Join(",", passIndicesAndSorters.Keys.OrderBy(static x => x))}]");

                _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => CreatePassCollection(x.Value));
                _passSorterTypes = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value?.GetType());
                _updatingPassSortOrderCounters = passIndicesAndSorters.Keys.ToDictionary(static key => key, static _ => 0L);

                _renderingPasses = [];
                _gpuPasses = [];

                _passMetadata = incomingPassMetadata;

                foreach (KeyValuePair<int, ICollection<RenderCommand>> pass in _updatingPasses)
                {
                    // Use TryAdd to safely handle any edge cases with duplicate keys
                    _renderingPasses.TryAdd(pass.Key, CreatePassCollection(passIndicesAndSorters[pass.Key]));
                    
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

        private static ICollection<RenderCommand> CreatePassCollection(IComparer<RenderCommand>? sorter)
            => sorter is null
                ? []
                : new SnapshotSortedRenderCommandCollection(sorter);

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
                existing.DeclarationOrder != incoming.DeclarationOrder ||
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

        // Dirty-delta swap queue. AddCPU enqueues a command only when the command is dirty and has
        // not already been queued in this collection for this swap cycle. The queue membership must
        // stay collection-local: the same RenderCommand instance can be collected by the desktop
        // viewport and OpenXR eye viewports before either collection swaps. A global queued bit would
        // make whichever collection ran first claim the only publish slot.
        private List<RenderCommand> _updatingSwapQueue = new(1024);
        private List<RenderCommand> _renderingSwapQueue = new(1024);
        private HashSet<RenderCommand> _updatingSwapQueueMembership = new(ReferenceEqualityComparer.Instance);
        private HashSet<RenderCommand> _renderingSwapQueueMembership = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// When false, this collection swaps pass membership but yields shared per-command
        /// snapshot publishing to any authoritative collection that collected the same command
        /// this frame. It may still publish commands that no authoritative view collected.
        /// </summary>
        public bool IsRenderCommandSnapshotAuthority { get; set; } = true;

        public RenderCommandCollection() { }
        public RenderCommandCollection(Dictionary<int, IComparer<RenderCommand>?> passIndicesAndSorters)
            => SetRenderPasses(passIndicesAndSorters);

        internal void SetOwnerPipeline(IRuntimeRenderPipelineDebugContext pipeline)
        {
            _ownerPipeline = pipeline;
            foreach (KeyValuePair<int, GPURenderPassCollection> pair in _gpuPasses)
                pair.Value.SetDebugContext(_ownerPipeline, pair.Key);
        }

        internal bool IsOwnedByShadowPipeline
            => _ownerPipeline?.IsShadowPipeline == true;

        private readonly Lock _lock = new();
        private readonly ReaderWriterLockSlim _renderingBufferLock = new(LockRecursionPolicy.SupportsRecursion);

        private RenderingBufferReadScope EnterRenderingBufferReadScope()
        {
            _renderingBufferLock.EnterReadLock();
            return new RenderingBufferReadScope(_renderingBufferLock);
        }

        private RenderingBufferWriteScope EnterRenderingBufferWriteScope()
        {
            _renderingBufferLock.EnterWriteLock();
            return new RenderingBufferWriteScope(_renderingBufferLock);
        }

        private readonly ref struct RenderingBufferReadScope(ReaderWriterLockSlim gate)
        {
            public void Dispose()
                => gate.ExitReadLock();
        }

        private readonly ref struct RenderingBufferWriteScope(ReaderWriterLockSlim gate)
        {
            public void Dispose()
                => gate.ExitWriteLock();
        }

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

        public int GetUpdatingPassCommandCount(int renderPass)
        {
            using (_lock.EnterScope())
                return _updatingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list)
                    ? list.Count
                    : 0;
        }

        public int GetRenderingCommandCount()
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPasses.Values.Sum(static pass => pass.Count);
        }

        public int GetRenderingPassCommandCount(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list)
                ? list.Count
                : 0;
        }

        public bool TryGetRenderingPassCommands(int renderPass, out IReadOnlyCollection<RenderCommand>? commands)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            if (_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) &&
                list is IReadOnlyCollection<RenderCommand> readOnly)
            {
                commands = readOnly;
                return true;
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

            using (_lock.EnterScope())
            {
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

                long sortOrderKey = GetSortOrderKey(pass);
                int beforeCount = set.Count;
                if (set is SnapshotSortedRenderCommandCollection snapshotSet)
                {
                    snapshotSet.Add(item, sortOrderKey);
                }
                else
                {
                    item.SortOrderKey = sortOrderKey;
                    set.Add(item);
                }
                int afterCount = set.Count;
                ++_numCommandsRecentlyAddedToUpdate;
                if (ShouldLogSponzaCpuDiag(item))
                {
                    LogSponzaCpuDiag(
                        afterCount == beforeCount ? "collect-duplicate" : "collect-add",
                        pass,
                        item,
                        camera: null,
                        $"updatingPassCount={afterCount}, dirty={item._dirty}, queuedInCollection={_updatingSwapQueueMembership.Contains(item)}");
                }

                // Dirty-delta enqueue: only swap commands whose state has actually changed since
                // the last publish. Queue membership is local to this collection so another
                // viewport cannot starve this collection before either one reaches SwapBuffers().
                bool needsPublish = item._dirty || !item.HasSwappedBuffers;
                if (needsPublish)
                {
                    if (IsRenderCommandSnapshotAuthority)
                        item._authoritativePublishQueued = true;
                }

                if (needsPublish && _updatingSwapQueueMembership.Add(item))
                {
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
            using (_lock.EnterScope())
            {
                int added = _numCommandsRecentlyAddedToUpdate;
                _numCommandsRecentlyAddedToUpdate = 0;
                return added;
            }
        }

        // Per-thread probe-deferred work list. Probe draws are intentionally deferred to
        // the END of RenderCPU so they test against the COMPLETE depth buffer for this pass
        // (every Visible mesh has already written its depth). Drawing probes inline in the
        // pass iteration produced visible flicker because the probe's outcome depended on
        // whether the future occluder had drawn yet within the same pass iteration.
        [ThreadStatic] private static List<CpuOcclusionProbeCandidate>? t_probeCandidates;
        [ThreadStatic] private static List<CpuOcclusionScheduledProbe>? t_deferredProbes;

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

        private static bool ShouldSuppressOcclusionForCurrentPass(bool suppressDepthNormalPrePass, out bool isShadowPass, out bool isDepthNormalPrePass)
        {
            isShadowPass = RuntimeEngine.Rendering.State.IsShadowPass;
            isDepthNormalPrePass = suppressDepthNormalPrePass;
            return isShadowPass || isDepthNormalPrePass;
        }

        internal bool PrepareCpuSoftwareOcclusion(int renderPass, XRCamera? camera, bool suppressCpuOcclusionForPass = false)
        {
            bool suppressOcclusion = ShouldSuppressOcclusionForCurrentPass(suppressCpuOcclusionForPass, out _, out _);
            if (!CpuSoftwareOcclusionCuller.IsEnabled ||
                camera is null ||
                suppressOcclusion ||
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
            Action<IRenderCommandMesh>? onExcludedGpuFallbackMesh = null,
            bool suppressCpuOcclusionForPass = false)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();

            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            EOcclusionCullingMode occlusionMode = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode;
            bool suppressOcclusion = ShouldSuppressOcclusionForCurrentPass(suppressCpuOcclusionForPass, out bool isShadowPass, out bool isDepthNormalPrePass);
            bool useCpuQueryOcclusion =
                !suppressOcclusion &&
                camera is not null &&
                occlusionMode == EOcclusionCullingMode.CpuQueryAsync &&
                RenderPassIsOcclusionTestable(renderPass);
            bool useCpuSocOcclusion = PrepareCpuSoftwareOcclusion(renderPass, camera, suppressCpuOcclusionForPass);

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
                    depthNormalPrePass: isDepthNormalPrePass,
                    modeOff: occlusionMode != EOcclusionCullingMode.CpuQueryAsync);
            }

            // Phase 2 deferred-probe queues (reused per-thread).
            List<CpuOcclusionProbeCandidate>? probeCandidates = null;
            List<CpuOcclusionScheduledProbe>? deferredProbes = null;
            if (useCpuQueryOcclusion)
            {
                probeCandidates = t_probeCandidates ??= new List<CpuOcclusionProbeCandidate>(128);
                deferredProbes = t_deferredProbes ??= new List<CpuOcclusionScheduledProbe>(64);
                probeCandidates.Clear();
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
                    var decision = s_cpuOcclusionCoordinator.ShouldRender(
                        renderPass,
                        camera,
                        queryKey,
                        out CpuOcclusionProbeRequest probeRequest);
                    bool needsHardwareQuery = probeRequest.Requested;

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
                        if (probeBounds.HasValue && probeCandidates is not null)
                        {
                            if (CpuQueryProxyIsNearPlaneUnsafe(camera!, probeBounds.Value))
                            {
                                s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe);
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
                            probeCandidates.Add(CreateCpuOcclusionProbeCandidate(queryKey, probeBounds.Value, probeRequest, camera!));
                            cpuCmdIndex++;
                            continue;
                        }
                        s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NoBounds);
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
                        probeCandidates is not null &&
                        cmd.CullingVolume is AABB visibleProbeBounds)
                    {
                        if (CpuQueryProxyIsNearPlaneUnsafe(camera!, visibleProbeBounds))
                            s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe);
                        else
                            probeCandidates.Add(CreateCpuOcclusionProbeCandidate(queryKey, visibleProbeBounds, probeRequest, camera!));

                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("draw-cpu-query-visible", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery=True");
                        RenderWithGpuScope(cmd, renderPass);
                    }
                    else
                    {
                        // Fallback: command has no AABB, so we can't issue a proxy probe.
                        if (needsHardwareQuery)
                        {
                            s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NoBounds);
                            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery);
                        }

                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("draw-cpu-query-direct", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery={needsHardwareQuery}");
                        RenderWithGpuScope(cmd, renderPass);
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
                    XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
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
            if (probeCandidates is { Count: > 0 } && deferredProbes is not null)
                s_cpuOcclusionCoordinator.SelectProbeCandidates(renderPass, camera, probeCandidates, deferredProbes);

            if (deferredProbes is { Count: > 0 })
            {
                foreach (var probe in deferredProbes)
                {
                    if (probe.IsHierarchyGroup)
                        s_cpuOcclusionCoordinator.BeginHierarchyQuery(renderPass, camera, probe.HierarchyGroupKey);
                    else
                        s_cpuOcclusionCoordinator.BeginQuery(renderPass, camera, probe.QueryKey);
                    try
                    {
                        XREngine.Rendering.Occlusion.CpuOcclusionProxyRenderer.Draw(probe.WorldBounds);
                    }
                    finally
                    {
                        if (probe.IsHierarchyGroup)
                            s_cpuOcclusionCoordinator.EndHierarchyQuery(renderPass, camera, probe.HierarchyGroupKey);
                        else
                            s_cpuOcclusionCoordinator.EndQuery(renderPass, camera, probe.QueryKey);
                    }
                }
                deferredProbes.Clear();
            }
            probeCandidates?.Clear();
        }

        private static CpuOcclusionProbeCandidate CreateCpuOcclusionProbeCandidate(
            uint queryKey,
            in AABB bounds,
            CpuOcclusionProbeRequest request,
            XRCamera camera)
        {
            float leftPriority = EstimateCpuOcclusionProbePriority(bounds, camera, out float leftDistance);
            float priority = leftPriority;
            float distance = leftDistance;

            XRCamera? rightEye = GetActiveRightEyeCamera();
            if (rightEye is not null && !ReferenceEquals(rightEye, camera))
            {
                float rightPriority = EstimateCpuOcclusionProbePriority(bounds, rightEye, out float rightDistance);
                if (rightPriority > priority)
                    priority = rightPriority;
                distance = MathF.Min(distance, rightDistance);
            }

            priority += request.PriorityBias;
            return new CpuOcclusionProbeCandidate(queryKey, bounds, request, priority, distance);
        }

        private static float EstimateCpuOcclusionProbePriority(in AABB bounds, XRCamera camera, out float distance)
        {
            Vector3 size = bounds.Max - bounds.Min;
            Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
            float radius = MathF.Max(0.001f, size.Length() * 0.5f);
            distance = MathF.Max(0.001f, MathF.Abs(camera.DistanceFromRenderNearPlane(center)));
            float projectedRadius = radius / distance;
            float priority = projectedRadius * projectedRadius;

            float edgeRisk = 0.0f;
            AccumulateRevealRisk(camera, new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Min.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Min.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Min.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Min.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Max.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Max.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Max.Z), ref edgeRisk);
            AccumulateRevealRisk(camera, new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Max.Z), ref edgeRisk);

            return priority + edgeRisk;
        }

        private static void AccumulateRevealRisk(XRCamera camera, Vector3 corner, ref float risk)
        {
            float nearDistance = camera.DistanceFromRenderNearPlane(corner);
            if (nearDistance < camera.NearZ * 2.0f)
                risk += 0.25f;
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
            using var renderingBufferScope = EnterRenderingBufferReadScope();

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
            using var renderingBufferScope = EnterRenderingBufferReadScope();

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
            using var renderingBufferScope = EnterRenderingBufferReadScope();

            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list))
                return;

            bool suppressOcclusion = ShouldSuppressOcclusionForCurrentPass(false, out _, out _);
            bool occlusionTestable =
                respectCpuQueryOcclusion &&
                !suppressOcclusion &&
                RenderPassIsOcclusionTestable(renderPass);
            XRCamera? camera = occlusionTestable ? GetActiveCpuOcclusionCamera() : null;
            EOcclusionCullingMode occlusionMode = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode;
            bool useCpuQueryOcclusion =
                occlusionTestable &&
                camera is not null &&
                occlusionMode == EOcclusionCullingMode.CpuQueryAsync;
            bool useCpuSocOcclusion =
                occlusionTestable &&
                camera is not null &&
                !useCpuQueryOcclusion &&
                PrepareCpuSoftwareOcclusion(renderPass, camera);

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
                    if (!s_cpuOcclusionCoordinator.PeekShouldRender(renderPass, camera, cmd.StableQueryKey))
                    {
                        cpuCmdIndex++;
                        continue;
                    }
                }

                if (useCpuSocOcclusion &&
                    cmd is IRenderCommandMesh socMesh &&
                    !CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(socMesh) &&
                    cmd.CullingVolume is AABB socBounds &&
                    !s_cpuSoftwareOcclusionCuller.TestVisible(cmd.StableQueryKey, socBounds))
                {
                    cpuCmdIndex++;
                    continue;
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
            if (!profiler.ShouldInstrumentCommandScopes || ShouldSkipGpuScope(command))
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
            using var renderingBufferScope = EnterRenderingBufferReadScope();

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
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) &&
                list.Count > 0;
        }

        public bool HasGpuEligibleMeshCommands(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
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

        private static XRCamera? GetActiveCpuOcclusionCamera()
        {
            IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;
            return renderState?.RenderingCamera as XRCamera
                ?? renderState?.SceneCamera as XRCamera;
        }

        private static void ConfigureGpuViewSet(GPURenderPassCollection gpuPass, IRuntimeRenderCommandExecutionState renderState, IRuntimeRenderCamera leftCamera)
        {
            IRuntimeRenderingHostServices hostServices = RuntimeRenderingHostServices.Current;
            IRuntimeRenderCamera? rightCamera = renderState.StereoPass ? renderState.StereoRightEyeCamera : null;
            bool stereo = renderState.StereoPass && rightCamera is not null;
            bool includeMirror = hostServices.RenderWindowsWhileInVR && hostServices.VrMirrorComposeFromEyeTextures;
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

            using (_lock.EnterScope())
            {
                using var renderingBufferScope = EnterRenderingBufferWriteScope();

                (_updatingPasses, _renderingPasses) = (_renderingPasses, _updatingPasses);
                (_updatingSwapQueue, _renderingSwapQueue) = (_renderingSwapQueue, _updatingSwapQueue);
                (_updatingSwapQueueMembership, _renderingSwapQueueMembership) = (_renderingSwapQueueMembership, _updatingSwapQueueMembership);

                using (RuntimeEngine.Profiler.Start("RenderCommandCollection.SwapBuffers.RenderPasses"))
                {
                    // Dirty-delta publish: walk only the commands that mutated since the last swap.
                    // Secondary shared-view collections yield to authoritative collections because the
                    // snapshot fields live on the RenderCommand instance itself. They still swap their
                    // own pass membership, and can publish commands that no authoritative view collected.
                    var queue = _renderingSwapQueue;
                    if (IsRenderCommandSnapshotAuthority)
                    {
                        int queueCount = queue.Count;
                        for (int i = 0; i < queueCount; i++)
                        {
                            var cmd = queue[i];
                            if (cmd is null)
                                continue;
                            if (cmd._dirty || !cmd.HasSwappedBuffers)
                                cmd.SwapBuffers();
                            cmd._authoritativePublishQueued = false;
                        }
                    }
                    else
                    {
                        int queueCount = queue.Count;
                        for (int i = 0; i < queueCount; i++)
                        {
                            var cmd = queue[i];
                            if (cmd is null)
                                continue;
                            if (!cmd._authoritativePublishQueued && (cmd._dirty || !cmd.HasSwappedBuffers))
                                cmd.SwapBuffers();
                        }
                    }
                    queue.Clear();
                    _renderingSwapQueueMembership.Clear();
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
        }

        public IEnumerable<IRenderCommandMesh> EnumerateRenderingMeshCommands()
        {
            _renderingBufferLock.EnterReadLock();
            try
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
            finally
            {
                _renderingBufferLock.ExitReadLock();
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
