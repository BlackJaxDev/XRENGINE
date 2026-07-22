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
using XREngine.Rendering.Vulkan;
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
    /// Buckets reorder-safe opaque draws by compatible material/mesh binding identity before
    /// applying front-to-back ordering inside a bucket. Transparent, UI, overlay, and diagnostic
    /// passes continue to use their existing order-preserving sorters.
    /// </summary>
    public sealed class OpaqueStateBucketRenderCommandSorter : IComparer<RenderCommand>
    {
        int IComparer<RenderCommand>.Compare(RenderCommand? x, RenderCommand? y)
        {
            int result = ResolveStateBucket(x).CompareTo(ResolveStateBucket(y));
            return result != 0 ? result : x?.CompareTo(y) ?? 0;
        }

        internal static OpaqueStateBucketKey ResolveStateBucket(RenderCommand? command)
        {
            if (command is not IRenderCommandMesh meshCommand)
                return default;

            XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
            return new OpaqueStateBucketKey(
                command.RenderPass,
                material is null ? 0 : RuntimeHelpers.GetHashCode(material.Shaders),
                material is null ? 0 : RuntimeHelpers.GetHashCode(material),
                meshCommand.RenderOptionsOverride is null ? 0 : RuntimeHelpers.GetHashCode(meshCommand.RenderOptionsOverride),
                meshCommand.Mesh is null ? 0 : RuntimeHelpers.GetHashCode(meshCommand.Mesh));
        }

        /// <summary>
        /// Captures the actual compatibility dimensions used to lower an opaque draw: pass,
        /// shader-pipeline identity, material/descriptor-layout owner, fixed render state, and
        /// mesh vertex/index binding owner. Keeping the dimensions separate avoids hash-combining
        /// collisions silently grouping incompatible draws.
        /// </summary>
        internal readonly record struct OpaqueStateBucketKey(
            int RenderPass,
            int PipelineIdentity,
            int LayoutIdentity,
            int RenderStateIdentity,
            int MeshBindingIdentity) : IComparable<OpaqueStateBucketKey>
        {
            public int CompareTo(OpaqueStateBucketKey other)
            {
                int result = RenderPass.CompareTo(other.RenderPass);
                if (result == 0) result = PipelineIdentity.CompareTo(other.PipelineIdentity);
                if (result == 0) result = LayoutIdentity.CompareTo(other.LayoutIdentity);
                if (result == 0) result = RenderStateIdentity.CompareTo(other.RenderStateIdentity);
                return result != 0 ? result : MeshBindingIdentity.CompareTo(other.MeshBindingIdentity);
            }
        }
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
        private readonly bool _bucketOpaqueState;
        private bool _sortDirty;

        public SnapshotSortedRenderCommandCollection(IComparer<RenderCommand> sorter)
        {
            _farToNear = sorter.GetType() == typeof(FarToNearRenderCommandSorter);
            _bucketOpaqueState = sorter.GetType() == typeof(OpaqueStateBucketRenderCommandSorter);
        }

        public int Count => _entries.Count;
        public bool IsReadOnly => false;

        public void Add(RenderCommand item)
            => Add(item, item.SortOrderKey);

        public void Add(RenderCommand item, long sortOrderKey)
            => Add(item, sortOrderKey, item.CaptureSortDistance(camera: null));

        public void Add(RenderCommand item, long sortOrderKey, float renderDistance)
        {
            if (!_membership.Add(item))
                return;

            _entries.Add(Entry.Capture(item, renderDistance, sortOrderKey));
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
            if (_bucketOpaqueState)
            {
                result = x.StateBucket.CompareTo(y.StateBucket);
                if (result == 0)
                    result = x.RenderDistance.CompareTo(y.RenderDistance);
            }
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
            int IdentityHash,
            OpaqueStateBucketRenderCommandSorter.OpaqueStateBucketKey StateBucket)
        {
            public static Entry Capture(RenderCommand command, float renderDistance, long sortOrderKey)
                => new(
                    command,
                    renderDistance,
                    sortOrderKey,
                    RuntimeHelpers.GetHashCode(command),
                    OpaqueStateBucketRenderCommandSorter.ResolveStateBucket(command));
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
                _renderingPassCommandCounts.Clear();
                _renderingPassCommandCounts.EnsureCapacity(_renderingPasses.Count);
                _renderingPassMeshCommandCounts.Clear();
                _renderingPassMeshCommandCounts.EnsureCapacity(_renderingPasses.Count);
                _renderingPassCommandSetSignatures.Clear();
                _renderingShadowCasterCommandSetSignature = 0u;
                _renderingCommandCount = 0;
                _renderingMeshCommandCount = 0;
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
        private readonly Dictionary<int, int> _renderingPassCommandCounts = [];
        private readonly Dictionary<int, int> _renderingPassMeshCommandCounts = [];
        private readonly Dictionary<int, ulong> _renderingPassCommandSetSignatures = [];
        private ulong _renderingShadowCasterCommandSetSignature;
        private int _renderingCommandCount = 0;
        private int _renderingMeshCommandCount = 0;
        private Dictionary<int, GPURenderPassCollection> _gpuPasses = [];
        private Dictionary<int, RenderPassMetadata> _passMetadata = [];
        private IRuntimeRenderPipelineDebugContext? _ownerPipeline;
        private bool _reportedCpuExactTransparentSpsRejection;
        private uint _reportedCpuExactTransparentSpsViewMask;
        private ETransparentMultiviewPolicy _reportedCpuExactTransparentSpsPolicy;
        private uint _activeCpuExactTransparentSpsViewMask;
        private ETransparentMultiviewPolicy _activeCpuExactTransparentSpsPolicy;

        private bool BeginCpuExactTransparentSpsFilter(int renderPass)
        {
            if (renderPass != (int)EDefaultRenderPass.TransparentForward ||
                AbstractRenderer.Current is not VulkanRenderer renderer ||
                !renderer.HasActiveMultiviewDrawTarget ||
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline is not XRRenderPipelineInstance pipeline)
            {
                _reportedCpuExactTransparentSpsRejection = false;
                return false;
            }

            ETransparentMultiviewPolicy policy =
                pipeline.RenderState.LastVisibilityContentPolicy.TransparentPolicy;
            if (policy is not ETransparentMultiviewPolicy.PerViewSorted and
                not ETransparentMultiviewPolicy.ForceSplit)
            {
                _reportedCpuExactTransparentSpsRejection = false;
                return false;
            }

            _activeCpuExactTransparentSpsViewMask = renderer.CurrentDrawViewMask;
            _activeCpuExactTransparentSpsPolicy = policy;
            return true;
        }

        private bool ShouldSkipCpuExactTransparentSpsCommand(bool filterActive, RenderCommand command)
        {
            if (!filterActive || command is not IRenderCommandMesh meshCommand)
                return false;

            XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
            if (material is null ||
                GpuTransparencyClassification.ResolveDomain(material.GetEffectiveTransparencyMode()) !=
                    EGpuTransparencyDomain.TransparentExact)
            {
                return false;
            }

            if (!_reportedCpuExactTransparentSpsRejection ||
                _reportedCpuExactTransparentSpsViewMask != _activeCpuExactTransparentSpsViewMask ||
                _reportedCpuExactTransparentSpsPolicy != _activeCpuExactTransparentSpsPolicy)
            {
                _reportedCpuExactTransparentSpsRejection = true;
                _reportedCpuExactTransparentSpsViewMask = _activeCpuExactTransparentSpsViewMask;
                _reportedCpuExactTransparentSpsPolicy = _activeCpuExactTransparentSpsPolicy;
                Debug.RenderingWarning(
                    $"[CPU-PIPELINE] Rejected exact-sorted transparent CPU candidates for Vulkan multiview mask 0x{_activeCpuExactTransparentSpsViewMask:X8}: policy {_activeCpuExactTransparentSpsPolicy} requires split per-view ordering. Approximate and OIT-compatible candidates continue.");
            }

            return true;
        }

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
            => Volatile.Read(ref _renderingCommandCount);

        public int GetRenderingMeshCommandCount()
            => Volatile.Read(ref _renderingMeshCommandCount);

        public int GetRenderingPassCommandCount(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPassCommandCounts.TryGetValue(renderPass, out int count)
                ? count
                : 0;
        }

        public int GetRenderingPassMeshCommandCount(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPassMeshCommandCounts.TryGetValue(renderPass, out int count)
                ? count
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
            => AddCPU(item, camera: null);

        public void AddCPU(RenderCommand item, IRuntimeRenderCamera? camera)
        {
            int pass = item.RenderPass;
            float renderDistance = item.CaptureSortDistance(camera);

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
                    snapshotSet.Add(item, sortOrderKey, renderDistance);
                }
                else
                {
                    item.SortOrderKey = sortOrderKey;
                    set.Add(item);
                }
                int afterCount = set.Count;
                ++_numCommandsRecentlyAddedToUpdate;
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
        [ThreadStatic] private static List<CpuOcclusionProbeCandidate>? t_visibleDrawCandidates;
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

        /// <summary>
        /// Returns whether the mesh command is currently suppressed by CPU hardware-query
        /// occlusion for this collection's active view. Intended for view-local diagnostics;
        /// render commands are shared across viewports, so this state must not be cached on
        /// the command itself.
        /// </summary>
        internal bool IsCpuQueryOccluded(RenderCommand command)
        {
            if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode != EOcclusionCullingMode.CpuQueryAsync ||
                _ownerPipeline?.IsShadowPipeline == true ||
                !RenderPassIsOcclusionTestable(command.RenderPass) ||
                command is not IRenderCommandMesh meshCommand ||
                CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(meshCommand))
            {
                return false;
            }

            XRCamera? camera = GetActiveCpuOcclusionCamera();
            OcclusionViewOwnership ownership = _ownerPipeline?.OcclusionViewOwnership ?? default;
            if (camera is null || !ownership.IsValid)
                return false;

            return s_cpuOcclusionCoordinator.TryGetCurrentDecision(
                    command.RenderPass,
                    camera,
                    command.StableQueryKey,
                    ownership,
                    out ECpuOcclusionDecision decision) &&
                decision is ECpuOcclusionDecision.Skip or ECpuOcclusionDecision.ProbeOnly;
        }

        private bool ShouldSuppressOcclusionForCurrentPass(bool suppressDepthNormalPrePass, out bool isShadowPass, out bool isDepthNormalPrePass)
        {
            isShadowPass = _ownerPipeline?.IsShadowPipeline == true;
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

            // Deferred Vulkan frame-op callbacks can execute after the ambient pipeline
            // render-state scope has unwound. The collection owner remains authoritative
            // for the camera/POV that produced these commands.
            if (camera is null && _ownerPipeline is XRRenderPipelineInstance ownerPipeline)
                camera = ownerPipeline.LastSceneCamera ?? ownerPipeline.LastRenderingCamera;

            EOcclusionCullingMode occlusionMode = RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode;
            OcclusionViewOwnership occlusionOwnership = _ownerPipeline?.OcclusionViewOwnership
                ?? default;
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
                s_cpuOcclusionCoordinator.BeginPass(
                    renderPass,
                    camera!,
                    (uint)list.Count,
                    _renderingPassCommandSetSignatures.TryGetValue(renderPass, out ulong commandSetSignature)
                        ? commandSetSignature
                        : ComputeOcclusionCommandSetSignature(list),
                    occlusionOwnership);
                if (!occlusionOwnership.IsValid)
                {
                    Debug.RenderingWarningEvery(
                        $"RenderCommandCollection.CpuOcclusion.MissingOwnership.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "CPU hardware-query occlusion is fail-visible because this render-command collection has no owning pipeline. owner={0} pass={1}",
                        _ownerPipeline?.DebugDescriptor ?? "<none>",
                        renderPass);
                }
                XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuPassBegin(0);

                if (!XREngine.Rendering.Occlusion.CpuOcclusionProxyRenderer.TryPrepare(out string proxyPrepareReason))
                {
                    s_cpuOcclusionCoordinator.ForceVisibleForPass(
                        renderPass,
                        camera,
                        ECpuOcclusionForceVisibleReason.ResourceGenerationChanged,
                        occlusionOwnership);
                    Debug.RenderingWarningEvery(
                        "CpuOcclusion.ProxyPreparePending",
                        TimeSpan.FromSeconds(2),
                        "CPU occlusion proxy preparation is pending ({0}); the pass is fail-visible for this frame.",
                        proxyPrepareReason);
                }
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
            List<CpuOcclusionProbeCandidate>? visibleDrawCandidates = null;
            List<CpuOcclusionScheduledProbe>? deferredProbes = null;
            if (useCpuQueryOcclusion)
            {
                probeCandidates = t_probeCandidates ??= new List<CpuOcclusionProbeCandidate>(128);
                visibleDrawCandidates = t_visibleDrawCandidates ??= new List<CpuOcclusionProbeCandidate>(128);
                deferredProbes = t_deferredProbes ??= new List<CpuOcclusionScheduledProbe>(64);
                probeCandidates.Clear();
                visibleDrawCandidates.Clear();
                deferredProbes.Clear();
            }

            bool filterExactTransparentSps = BeginCpuExactTransparentSpsFilter(renderPass);
            uint cpuCmdIndex = 0;
            foreach (var cmd in list)
            {
                if (ShouldSkipCpuExactTransparentSpsCommand(filterExactTransparentSps, cmd))
                {
                    cpuCmdIndex++;
                    continue;
                }

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

                ECpuOcclusionValidationRole validationRole = ECpuOcclusionValidationRole.None;
                OcclusionViewKey validationViewKey = default;
                bool recordValidationEvidence = false;
                if (camera is not null &&
                    !suppressOcclusion &&
                    RenderPassIsOcclusionTestable(renderPass) &&
                    cmd is IRenderCommandMesh validationMesh &&
                    !CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(validationMesh))
                {
                    validationRole = CpuOcclusionValidationEvidence.ResolveRole(validationMesh);
                    validationViewKey = CpuRenderOcclusionCoordinator.CreatePassKey(
                        renderPass,
                        camera,
                        occlusionOwnership);
                    bool validationScopeApplicable =
                        CpuOcclusionValidationEvidence.IsApplicableToScope(
                            validationMesh,
                            validationViewKey.Scope);
                    if (!CpuOcclusionValidationEvidence.ShouldRenderInScope(
                        validationRole,
                        validationScopeApplicable))
                    {
                        // The desktop and SPS motion/top-edge sentinels are
                        // camera-relative output oracles. Rendering one through
                        // the other camera makes cross-run parity depend on the
                        // unrelated camera basis, so exclude it from that output
                        // before either the normal draw or occlusion query path.
                        cpuCmdIndex++;
                        continue;
                    }

                    if (validationScopeApplicable)
                    {
                        CpuOcclusionValidationEvidence.RecordCandidate(
                            validationViewKey,
                            cmd.StableQueryKey,
                            validationRole,
                            appliedOcclusionMode);
                        recordValidationEvidence = true;
                    }
                }

                if (useCpuQueryOcclusion && cmd is IRenderCommandMesh occlMesh)
                {
                    // Explicit per-material opt-out (skybox, fullscreen overlays, gizmos
                    // whose AABB / depth contract is unsuitable for AnySamplesPassedConservative).
                    if (CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(occlMesh))
                    {
                        LogSponzaCpuDiag("draw-cpu-query-excluded", renderPass, cmd, camera, "cpu-query-occlusion-excluded");
                        if (recordValidationEvidence)
                        {
                            CpuOcclusionValidationEvidence.RecordRendered(
                                validationViewKey,
                                cmd.StableQueryKey,
                                validationRole,
                                appliedOcclusionMode,
                                ECpuOcclusionDecision.Visible);
                        }
                        RenderWithGpuScope(cmd, renderPass);
                        cpuCmdIndex++;
                        continue;
                    }

                    XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuTestedOne();

                    // C-CPU-4: key by stable per-command identity, not foreach position.
                    // cpuCmdIndex shifts on every list mutation; StableQueryKey is assigned
                    // at command construction and never changes.
                    uint queryKey = cmd.StableQueryKey;
                    // Scope-specific sentinels can still enter another output's
                    // render list because they are ordinary scene meshes. Keep
                    // evidence scoped to its owning output, but never let an
                    // output where the sentinel is visible query-cull it.
                    bool validationKnownVisible =
                        CpuOcclusionValidationEvidence.IsKnownVisibleRole(validationRole);
                    CpuOcclusionProbeRequest probeRequest;
                    ECpuOcclusionDecision decision;
                    if (validationKnownVisible)
                    {
                        probeRequest = s_cpuOcclusionCoordinator.ForceVisibleForValidation(
                            renderPass,
                            camera,
                            queryKey,
                            occlusionOwnership);
                        decision = ECpuOcclusionDecision.Visible;
                    }
                    else
                    {
                        decision = s_cpuOcclusionCoordinator.ShouldRender(
                            renderPass,
                            camera,
                            queryKey,
                            out probeRequest,
                            occlusionOwnership,
                            cmd.CullingVolume);
                    }
                    bool needsHardwareQuery = probeRequest.Requested;

                    if (decision == XREngine.Rendering.Occlusion.ECpuOcclusionDecision.Skip)
                    {
                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("skip-cpu-query", renderPass, cmd, camera, $"queryKey={queryKey}");
                        XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
                        if (recordValidationEvidence)
                        {
                            uint proofCoverageMask = s_cpuOcclusionCoordinator.GetOccludedProofCoverageMask(
                                renderPass,
                                camera,
                                queryKey,
                                occlusionOwnership);
                            CpuOcclusionValidationEvidence.RecordCulled(
                                validationViewKey,
                                queryKey,
                                validationRole,
                                appliedOcclusionMode,
                                proofCoverageMask,
                                decision);
                        }
                        cpuCmdIndex++;
                        continue;
                    }

                    bool cpuSocCull =
                        decision == XREngine.Rendering.Occlusion.ECpuOcclusionDecision.Visible &&
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
                            if (recordValidationEvidence)
                            {
                                uint proofCoverageMask = s_cpuOcclusionCoordinator.GetOccludedProofCoverageMask(
                                    renderPass,
                                    camera,
                                    queryKey,
                                    occlusionOwnership);
                                CpuOcclusionValidationEvidence.RecordCulled(
                                    validationViewKey,
                                    queryKey,
                                    validationRole,
                                    appliedOcclusionMode,
                                    proofCoverageMask,
                                    decision);
                            }
                            cpuCmdIndex++;
                            continue;
                        }
                        var probeBounds = cmd.CullingVolume;
                        if (probeBounds.HasValue && probeCandidates is not null)
                        {
                            if (CpuQueryProxyIsNearPlaneUnsafe(camera!, probeBounds.Value))
                            {
                                s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe, occlusionOwnership);
                                if (ShouldLogSponzaCpuDiag(cmd))
                                    LogSponzaCpuDiag("draw-cpu-query-near-plane", renderPass, cmd, camera, $"queryKey={queryKey}");
                                if (recordValidationEvidence)
                                {
                                    CpuOcclusionValidationEvidence.RecordRendered(
                                        validationViewKey,
                                        queryKey,
                                        validationRole,
                                        appliedOcclusionMode,
                                        ECpuOcclusionDecision.Visible);
                                }
                                RenderWithGpuScope(cmd, renderPass);
                                cpuCmdIndex++;
                                continue;
                            }

                            // DEFERRED: probe is queued and issued after the visible-mesh
                            // loop completes so it tests against complete-depth, not the
                            // partial depth that would exist at this command's iteration
                            // point. This eliminates render-order false positives.
                            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuCulledOne();
                            if (recordValidationEvidence)
                            {
                                uint proofCoverageMask = s_cpuOcclusionCoordinator.GetOccludedProofCoverageMask(
                                    renderPass,
                                    camera,
                                    queryKey,
                                    occlusionOwnership);
                                CpuOcclusionValidationEvidence.RecordCulled(
                                    validationViewKey,
                                    queryKey,
                                    validationRole,
                                    appliedOcclusionMode,
                                    proofCoverageMask,
                                    decision);
                            }
                            if (ShouldLogSponzaCpuDiag(cmd))
                                LogSponzaCpuDiag("skip-cpu-query-probe", renderPass, cmd, camera, $"queryKey={queryKey}, cpuSocCull={cpuSocCull}");
                            probeCandidates.Add(CreateCpuOcclusionProbeCandidate(
                                queryKey,
                                probeBounds.Value,
                                probeRequest,
                                camera!));
                            cpuCmdIndex++;
                            continue;
                        }
                        // No bounds available — fall through to the fail-visible mesh path.
                    }

                    // A visible-demotion query must bracket the exact contributing draw at
                    // its original position. This measures the pass's real depth function,
                    // alpha discard, and prepass equality semantics. An AABB is deliberately
                    // reserved for recovery of already-occluded meshes: against self-depth,
                    // a proxy can produce a false zero and progressively hide visible parts.
                    if (needsHardwareQuery &&
                        cmd.CullingVolume is AABB visibleProbeBounds)
                    {
                        string queryPrepareReason = "Missing mesh renderer";
                        if (occlMesh.Mesh is null || !occlMesh.Mesh.TryPrepareForRendering(out queryPrepareReason))
                        {
                            // A query with no draw has no visibility meaning. Keep the
                            // command fail-visible and out of the next ranked set until
                            // the backend can actually enqueue its mesh operation.
                            Debug.RenderingWarningEvery(
                                $"CpuOcclusion.VisibleQueryNotReady.{queryKey}",
                                TimeSpan.FromSeconds(2),
                                "CPU occlusion query deferred because mesh {0} is not render-ready ({1}).",
                                queryKey,
                                queryPrepareReason);
                            RenderWithGpuScope(cmd, renderPass);
                        }
                        else if (CpuQueryProxyIsNearPlaneUnsafe(camera!, visibleProbeBounds))
                        {
                            s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NearPlaneUnsafe, occlusionOwnership);
                            RenderWithGpuScope(cmd, renderPass);
                        }
                        else
                        {
                            bool queryScheduled = s_cpuOcclusionCoordinator.TryScheduleVisibleDrawQuery(
                                renderPass,
                                camera,
                                queryKey,
                                probeRequest,
                                occlusionOwnership);
                            if (!queryScheduled && visibleDrawCandidates is not null)
                            {
                                // Publish every unscheduled exact-draw request together
                                // after the loop. The coordinator ranks that complete set
                                // for the next pass instead of letting traversal order
                                // starve commands later in a large scene.
                                visibleDrawCandidates.Add(CreateCpuOcclusionProbeCandidate(
                                    queryKey,
                                    visibleProbeBounds,
                                    probeRequest,
                                    camera!));
                            }
                            if (queryScheduled)
                                s_cpuOcclusionCoordinator.BeginQuery(
                                    renderPass,
                                    camera,
                                    queryKey,
                                    occlusionOwnership,
                                    visibleProbeBounds);
                            try
                            {
                                RenderWithGpuScope(cmd, renderPass);
                            }
                            finally
                            {
                                if (queryScheduled)
                                    s_cpuOcclusionCoordinator.EndQuery(renderPass, camera, queryKey, occlusionOwnership);
                            }
                        }

                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("draw-cpu-query-visible", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery=True");
                        if (recordValidationEvidence)
                        {
                            CpuOcclusionValidationEvidence.RecordRendered(
                                validationViewKey,
                                queryKey,
                                validationRole,
                                appliedOcclusionMode,
                                decision);
                        }
                    }
                    else
                    {
                        if (needsHardwareQuery)
                        {
                            s_cpuOcclusionCoordinator.ForceVisible(renderPass, camera, queryKey, ECpuOcclusionForceVisibleReason.NoBounds, occlusionOwnership);
                            XREngine.Rendering.Occlusion.OcclusionTelemetry.RecordCpuBudgetSkipped(ECpuOcclusionQueryReason.DiagnosticForcedQuery);
                        }

                        if (ShouldLogSponzaCpuDiag(cmd))
                            LogSponzaCpuDiag("draw-cpu-query-direct", renderPass, cmd, camera, $"queryKey={queryKey}, needsHardwareQuery={needsHardwareQuery}");
                        if (recordValidationEvidence)
                        {
                            CpuOcclusionValidationEvidence.RecordRendered(
                                validationViewKey,
                                queryKey,
                                validationRole,
                                appliedOcclusionMode,
                                decision);
                        }
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
                    if (recordValidationEvidence)
                    {
                        CpuOcclusionValidationEvidence.RecordCulled(
                            validationViewKey,
                            cmd.StableQueryKey,
                            validationRole,
                            appliedOcclusionMode,
                            proofCoverageMask: 0u,
                            decision: ECpuOcclusionDecision.Skip);
                    }
                    cpuCmdIndex++;
                    continue;
                }

                cpuCmdIndex++;

                LogSponzaCpuDiag("draw-cpu", renderPass, cmd, camera, "occlusion=Disabled");
                if (recordValidationEvidence)
                {
                    CpuOcclusionValidationEvidence.RecordRendered(
                        validationViewKey,
                        cmd.StableQueryKey,
                        validationRole,
                        appliedOcclusionMode,
                        ECpuOcclusionDecision.Visible);
                }
                RenderWithGpuScope(cmd, renderPass);
            }

            if (visibleDrawCandidates is not null)
            {
                s_cpuOcclusionCoordinator.SelectVisibleDrawCandidates(
                    renderPass,
                    camera,
                    visibleDrawCandidates,
                    occlusionOwnership);
                visibleDrawCandidates.Clear();
            }

            // Phase 3: deferred AABB probes for occluded recovery. These meshes did not
            // contribute depth, so testing after the visible pass cannot self-occlude.
            // A false positive only preserves an extra draw, while a zero result safely
            // proves the bounded mesh remains fully occluded.
            if (probeCandidates is { Count: > 0 } && deferredProbes is not null)
                s_cpuOcclusionCoordinator.SelectProbeCandidates(
                    renderPass,
                    camera,
                    probeCandidates,
                    deferredProbes,
                    occlusionOwnership);

            if (deferredProbes is { Count: > 0 })
            {
                if (!XREngine.Rendering.Occlusion.CpuOcclusionProxyRenderer.TryPrepare(out string proxyPrepareReason))
                {
                    // An occlusion optimization may be unavailable while its GPU
                    // resources are generated, but it must never turn that temporary
                    // state into an empty query and then suppress visible geometry.
                    s_cpuOcclusionCoordinator.ForceVisibleForPass(
                        renderPass,
                        camera,
                        ECpuOcclusionForceVisibleReason.ResourceGenerationChanged,
                        occlusionOwnership);
                    Debug.RenderingWarningEvery(
                        "CpuOcclusion.ProxyPreparePending",
                        TimeSpan.FromSeconds(2),
                        "CPU occlusion proxy preparation is pending ({0}); queries were skipped fail-visible for this frame.",
                        proxyPrepareReason);
                    deferredProbes.Clear();
                    probeCandidates?.Clear();
                    visibleDrawCandidates?.Clear();
                    return;
                }

                foreach (var probe in deferredProbes)
                {
                    if (probe.IsHierarchyGroup)
                        s_cpuOcclusionCoordinator.BeginHierarchyQuery(
                            renderPass,
                            camera,
                            probe.HierarchyGroupKey,
                            occlusionOwnership,
                            probe.WorldBounds);
                    else
                        s_cpuOcclusionCoordinator.BeginQuery(
                            renderPass,
                            camera,
                            probe.QueryKey,
                            occlusionOwnership,
                            probe.WorldBounds);
                    try
                    {
                        XREngine.Rendering.Occlusion.CpuOcclusionProxyRenderer.Draw(probe.WorldBounds, camera!);
                    }
                    finally
                    {
                        if (probe.IsHierarchyGroup)
                            s_cpuOcclusionCoordinator.EndHierarchyQuery(renderPass, camera, probe.HierarchyGroupKey, occlusionOwnership);
                        else
                            s_cpuOcclusionCoordinator.EndQuery(renderPass, camera, probe.QueryKey, occlusionOwnership);
                    }
                }
                deferredProbes.Clear();
            }
            probeCandidates?.Clear();
            visibleDrawCandidates?.Clear();
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
            return new CpuOcclusionProbeCandidate(
                queryKey,
                bounds,
                request,
                priority,
                distance);
        }

        private static float EstimateCpuOcclusionProbePriority(in AABB bounds, XRCamera camera, out float distance)
        {
            Vector3 size = bounds.Max - bounds.Min;
            Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
            float radius = MathF.Max(0.001f, size.Length() * 0.5f);
            distance = MathF.Max(0.001f, MathF.Abs(camera.DistanceFromRenderNearPlane(center)));
            float projectedRadius = radius / distance;
            float priority = projectedRadius * projectedRadius;

            if (CpuOcclusionProjectionFootprint.TryProject(
                    bounds,
                    camera.ViewProjectionMatrixUnjittered,
                    out CpuOcclusionProjectionFootprint footprint))
            {
                const float priorityEdgeBand = 0.20f;
                float edgePressure = Math.Clamp(
                    (priorityEdgeBand - footprint.ViewportEdgeMargin) / priorityEdgeBand,
                    0.0f,
                    1.0f);
                priority += edgePressure * 2.0f;
            }
            else
            {
                // Near-plane crossings and invalid clip projections have the highest
                // reveal risk and should be refreshed ahead of central, stable proxies.
                priority += 4.0f;
            }

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

            bool filterExactTransparentSps = BeginCpuExactTransparentSpsFilter(renderPass);
            foreach (var cmd in list)
            {
                if (cmd is IRenderCommandMesh &&
                    !ShouldSkipCpuExactTransparentSpsCommand(filterExactTransparentSps, cmd))
                {
                    RenderWithGpuScope(cmd, renderPass);
                }
            }
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

            bool filterExactTransparentSps = BeginCpuExactTransparentSpsFilter(renderPass);
            foreach (var cmd in list)
            {
                if (cmd is null)
                    continue;

                if (ShouldSkipCpuExactTransparentSpsCommand(filterExactTransparentSps, cmd))
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
            OcclusionViewOwnership occlusionOwnership = _ownerPipeline?.OcclusionViewOwnership
                ?? default;

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

                if (useCpuQueryOcclusion &&
                    cmd is IRenderCommandMesh queryMesh &&
                    !CpuSoftwareOcclusionCuller.IsCpuOcclusionExcluded(queryMesh))
                {
                    // C-CPU-4: stable per-command identity, matches primary RenderCPU keying.
                    if (!s_cpuOcclusionCoordinator.PeekShouldRender(
                            renderPass,
                            camera,
                            cmd.StableQueryKey,
                            occlusionOwnership))
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

            RenderWorldSnapshot? worldSnapshot = renderState.WorldSnapshot;
            IRuntimeRenderCommandSceneContext? scene = worldSnapshot?.Scene ?? renderState.RenderingScene;
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
                RenderFrameViewSet configuredViewSet = ConfigureGpuViewSet(gpuPass, renderState, camera);
                gpuPass.ConfigureStableHiZViewSet(
                    configuredViewSet,
                    worldSnapshot?.FrameId ?? RuntimeEngine.Rendering.State.RenderFrameId);

                if (meshletStrategy && worldSnapshot is RenderWorldSnapshot snapshot)
                    snapshot.GpuScene.EnsureRuntimeMeshletPayloadsForMeshletDispatch();

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
            return _renderingPassCommandCounts.TryGetValue(renderPass, out int count) && count > 0;
        }

        public bool HasAnyRenderingCommands(ReadOnlySpan<int> renderPasses)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            for (int i = 0; i < renderPasses.Length; i++)
            {
                if (_renderingPassCommandCounts.TryGetValue(renderPasses[i], out int count) && count > 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether the published render-side snapshot contains a mesh command in the requested pass.
        /// Method/callback commands deliberately do not count as geometry workload.
        /// </summary>
        public bool HasRenderingMeshCommands(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            return _renderingPassMeshCommandCounts.TryGetValue(renderPass, out int count) && count > 0;
        }

        /// <summary>
        /// Returns whether any requested pass contains a mesh command while acquiring the render-buffer lock once.
        /// </summary>
        public bool HasAnyRenderingMeshCommands(ReadOnlySpan<int> renderPasses)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            for (int i = 0; i < renderPasses.Length; i++)
            {
                if (_renderingPassMeshCommandCounts.TryGetValue(renderPasses[i], out int count) && count > 0)
                    return true;
            }

            return false;
        }

        public bool HasGpuEligibleMeshCommands(int renderPass)
        {
            using var renderingBufferScope = EnterRenderingBufferReadScope();
            if (!_renderingPasses.TryGetValue(renderPass, out ICollection<RenderCommand>? list) || list.Count == 0)
                return false;

            if (list is List<RenderCommand> commands)
            {
                for (int i = 0; i < commands.Count; i++)
                    if (IsGpuEligibleMeshCommand(commands[i]))
                        return true;
                return false;
            }

            if (list is SnapshotSortedRenderCommandCollection sortedCommands)
            {
                foreach (RenderCommand command in sortedCommands)
                    if (IsGpuEligibleMeshCommand(command))
                        return true;
                return false;
            }

            foreach (RenderCommand command in list)
                if (IsGpuEligibleMeshCommand(command))
                    return true;

            return false;
        }

        private static bool IsGpuEligibleMeshCommand(RenderCommand command)
        {
            if (command is not IRenderCommandMesh meshCommand)
                return false;

            var material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
            return !meshCommand.ForceCpuRendering && material?.RenderOptions?.ExcludeFromGpuIndirect != true;
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

        private static RenderFrameViewSet ConfigureGpuViewSet(GPURenderPassCollection gpuPass, IRuntimeRenderCommandExecutionState renderState, IRuntimeRenderCamera leftCamera)
        {
            RenderFrameViewSet viewSet = renderState.FrameViewSet ?? RenderFrameViewSetCapture.Capture(renderState);
            Span<GPUViewDescriptor> descriptors = stackalloc GPUViewDescriptor[RenderFrameViewSet.MaxViewCount];
            Span<GPUViewConstants> constants = stackalloc GPUViewConstants[RenderFrameViewSet.MaxViewCount];
            uint passMaskLo = gpuPass.RenderPass >= 0 && gpuPass.RenderPass < 32
                ? 1u << gpuPass.RenderPass
                : uint.MaxValue;
            uint passMaskHi = gpuPass.RenderPass >= 32 && gpuPass.RenderPass < 64
                ? 1u << (gpuPass.RenderPass - 32)
                : 0u;

            RenderFrameViewSetGpuAdapter.Write(
                viewSet,
                passMaskLo,
                passMaskHi,
                gpuPass.CommandCapacity,
                descriptors,
                constants);
            ReadOnlySpan<GPUViewDescriptor> activeDescriptors = descriptors[..viewSet.ViewCount];
            ValidateViewDescriptorLayout(activeDescriptors, gpuPass.CommandCapacity);
            gpuPass.ConfigureViewSet(activeDescriptors, constants[..viewSet.ViewCount]);

            IRuntimeRenderCamera? rightCamera = renderState.StereoPass ? renderState.StereoRightEyeCamera : null;
            uint requestedSourceView = DetermineIndirectSourceViewId(renderState, leftCamera, rightCamera);
            gpuPass.SetIndirectSourceViewId(requestedSourceView);
            if (requestedSourceView != gpuPass.IndirectSourceViewId)
            {
                throw new InvalidOperationException(
                    $"Indirect source view id {requestedSourceView} was clamped to {gpuPass.IndirectSourceViewId} for active views {gpuPass.ActiveViewCount}.");
            }

            return viewSet;
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
                PublishRenderingCommandCountsNoLock();

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

                // The collection lock excludes AddCPU while existing values are reset. Replacing
                // a Dictionary value does not invalidate its key enumerator.
                if (_updatingPassSortOrderCounters.Count > 0)
                {
                    foreach (int passIndex in _updatingPassSortOrderCounters.Keys)
                        _updatingPassSortOrderCounters[passIndex] = 0L;
                }

                _numCommandsRecentlyAddedToUpdate = 0;
            }
        }

        private void PublishRenderingCommandCountsNoLock()
        {
            _renderingPassCommandCounts.Clear();
            _renderingPassCommandCounts.EnsureCapacity(_renderingPasses.Count);
            _renderingPassMeshCommandCounts.Clear();
            _renderingPassMeshCommandCounts.EnsureCapacity(_renderingPasses.Count);
            _renderingPassCommandSetSignatures.Clear();
            int total = 0;
            int meshTotal = 0;
            foreach ((int passIndex, ICollection<RenderCommand> pass) in _renderingPasses)
            {
                int count = pass.Count;
                ulong commandSetSignature = ComputeOcclusionCommandSetSignature(pass, out int meshCount);
                _renderingPassCommandCounts[passIndex] = count;
                _renderingPassMeshCommandCounts[passIndex] = meshCount;
                _renderingPassCommandSetSignatures[passIndex] = commandSetSignature;
                total += count;
                meshTotal += meshCount;
            }

            _renderingShadowCasterCommandSetSignature = ComputeShadowCasterCommandSetSignature();

            Volatile.Write(ref _renderingCommandCount, total);
            Volatile.Write(ref _renderingMeshCommandCount, meshTotal);
        }

        internal ulong ShadowCasterCommandSetSignature
            => Volatile.Read(ref _renderingShadowCasterCommandSetSignature);

        private ulong ComputeShadowCasterCommandSetSignature()
        {
            ulong hash = 14695981039346656037UL;
            AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.PreRender);
            AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueDeferred);
            AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueForward);
            AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.MaskedForward);
            AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.PostRender);
            return MixOcclusionCommandKey(hash);
        }

        private void AddShadowCasterPassSignature(ref ulong hash, EDefaultRenderPass renderPass)
        {
            int passIndex = (int)renderPass;
            ulong membershipSignature = _renderingPassCommandSetSignatures.TryGetValue(passIndex, out ulong signature)
                ? signature
                : 0u;
            ulong contentSignature = _renderingPasses.TryGetValue(passIndex, out ICollection<RenderCommand>? commands)
                ? ComputeShadowCasterPassContentSignature(commands)
                : 0u;
            hash ^= MixOcclusionCommandKey(
                membershipSignature ^
                BitOperations.RotateLeft(contentSignature, 17) ^
                (uint)passIndex);
            hash *= 1099511628211UL;
        }

        private static ulong ComputeShadowCasterPassContentSignature(ICollection<RenderCommand> commands)
        {
            // Shadow-atlas reuse depends on the content that will be rendered, not only
            // command membership. Keep the aggregate order-independent because normal
            // camera motion can change pass sorting without changing shadow content.
            ulong xor = 0UL;
            ulong sum = 0xD6E8FEB86659FD93UL;
            foreach (RenderCommand command in commands)
            {
                ulong mixed = MixOcclusionCommandKey(
                    ComputeShadowCasterCommandStateSignature(command) ^ command.StableQueryKey);
                xor ^= mixed;
                sum += mixed;
            }

            return MixOcclusionCommandKey(xor ^ BitOperations.RotateLeft(sum, 29) ^ (uint)commands.Count);
        }

        internal static ulong ComputeShadowCasterCommandStateSignature(RenderCommand command)
        {
            ulong hash = 14695981039346656037UL;
            AddShadowState(ref hash, command.Enabled ? 1u : 0u);
            AddShadowState(ref hash, command.RenderPass);

            if (command is not IRenderCommandMesh meshCommand)
                return MixOcclusionCommandKey(hash);

            AddShadowState(ref hash, meshCommand.WorldMatrix);
            AddShadowState(ref hash, meshCommand.WorldMatrixIsModelMatrix ? 1u : 0u);
            AddShadowState(ref hash, meshCommand.Instances);
            AddShadowState(ref hash, meshCommand.Mesh is null ? 0 : RuntimeHelpers.GetHashCode(meshCommand.Mesh));
            AddShadowState(ref hash, meshCommand.MaterialOverride is null ? 0 : RuntimeHelpers.GetHashCode(meshCommand.MaterialOverride));
            AddShadowState(ref hash, meshCommand.RenderOptionsOverride is null ? 0 : RuntimeHelpers.GetHashCode(meshCommand.RenderOptionsOverride));

            // Non-model/skinned commands commonly publish identity as WorldMatrix and
            // carry their animated world extent through the culling-volume override.
            if (command.CullingVolume is AABB bounds)
            {
                AddShadowState(ref hash, bounds.Min);
                AddShadowState(ref hash, bounds.Max);
            }

            return MixOcclusionCommandKey(hash);
        }

        private static void AddShadowState(ref ulong hash, Matrix4x4 value)
        {
            AddShadowState(ref hash, value.M11); AddShadowState(ref hash, value.M12); AddShadowState(ref hash, value.M13); AddShadowState(ref hash, value.M14);
            AddShadowState(ref hash, value.M21); AddShadowState(ref hash, value.M22); AddShadowState(ref hash, value.M23); AddShadowState(ref hash, value.M24);
            AddShadowState(ref hash, value.M31); AddShadowState(ref hash, value.M32); AddShadowState(ref hash, value.M33); AddShadowState(ref hash, value.M34);
            AddShadowState(ref hash, value.M41); AddShadowState(ref hash, value.M42); AddShadowState(ref hash, value.M43); AddShadowState(ref hash, value.M44);
        }

        private static void AddShadowState(ref ulong hash, Vector3 value)
        {
            AddShadowState(ref hash, value.X);
            AddShadowState(ref hash, value.Y);
            AddShadowState(ref hash, value.Z);
        }

        private static void AddShadowState(ref ulong hash, float value)
            => AddShadowState(ref hash, BitConverter.SingleToUInt32Bits(value));

        private static void AddShadowState(ref ulong hash, int value)
            => AddShadowState(ref hash, unchecked((uint)value));

        private static void AddShadowState(ref ulong hash, uint value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        private static ulong ComputeOcclusionCommandSetSignature(ICollection<RenderCommand> commands)
            => ComputeOcclusionCommandSetSignature(commands, out _);

        private static ulong ComputeOcclusionCommandSetSignature(
            ICollection<RenderCommand> commands,
            out int meshCommandCount)
        {
            // Membership is what invalidates keyed query history. Sort order is
            // intentionally ignored because normal camera motion can reorder an
            // otherwise identical set without changing StableQueryKey ownership.
            ulong xor = 0UL;
            ulong sum = 0x9E3779B97F4A7C15UL;
            meshCommandCount = 0;
            foreach (RenderCommand command in commands)
            {
                ulong mixed = MixOcclusionCommandKey(command.StableQueryKey);
                xor ^= mixed;
                sum += mixed;
                if (command is IRenderCommandMesh)
                    meshCommandCount++;
            }

            return MixOcclusionCommandKey(xor ^ BitOperations.RotateLeft(sum, 23) ^ (uint)commands.Count);
        }

        private static ulong MixOcclusionCommandKey(ulong value)
        {
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
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
            // Collection churn and steady-state direct draws can consume the entire bounded
            // diagnostic budget before an asynchronous query has time to resolve. Keep this
            // trace focused on state transitions that explain visible occlusion changes.
            if (phase is "collect-add" or "collect-duplicate" or "draw-cpu" or "draw-cpu-query-direct")
                return;

            if (!ShouldLogSponzaCpuDiag(cmd) || cmd is not RenderCommandMesh3D meshCommand)
                return;

            int line = Interlocked.Increment(ref s_sponzaCpuDiagLines);
            if (line > SponzaCpuDiagMaxLines)
                return;

            XRMeshRenderer? renderer = meshCommand.Mesh;
            var material = meshCommand.MaterialOverride ?? renderer?.Material;
            XRMesh? mesh = renderer?.Mesh;
            string spatialDetail;
            if (meshCommand.CullingVolume is AABB worldBounds)
            {
                Vector3 cameraPosition = camera?.Transform.RenderMatrix.Translation ?? new Vector3(float.NaN);
                Vector3 modelTranslation = meshCommand.WorldMatrix.Translation;
                spatialDetail =
                    $"boundsMin=({worldBounds.Min.X:F3},{worldBounds.Min.Y:F3},{worldBounds.Min.Z:F3})," +
                    $"boundsMax=({worldBounds.Max.X:F3},{worldBounds.Max.Y:F3},{worldBounds.Max.Z:F3})," +
                    $"modelT=({modelTranslation.X:F3},{modelTranslation.Y:F3},{modelTranslation.Z:F3})," +
                    $"cameraT=({cameraPosition.X:F3},{cameraPosition.Y:F3},{cameraPosition.Z:F3})";
            }
            else
            {
                spatialDetail = "bounds=<null>";
            }
            Debug.Rendering(
                "[SponzaFlickerDiag.CPU] frame={0} phase={1} line={2} cmd={3} stable={4} pass={5} cmdPass={6} enabled={7} renderEnabled={8} forceCpu={9} instances={10} sortKey={11} distance={12:F3} camera={13} sourceSubMesh='{14}' mesh='{15}' material='{16}' detail='{17}' spatial='{18}'",
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
                detail,
                spatialDetail);
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
