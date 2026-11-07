using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Rendering.Residency;

namespace XREngine.Cutscenes;

/// <summary>
/// A residency backend for the sub-meshes of a cutscene. The asset of a sub-mesh is its stable key
/// (<see cref="CutsceneResidencyAssetKeys.ForSubMesh(SceneNode, int, int)"/>), and its levels are LOD tiers
/// (<see cref="SubMeshLodTiers"/>): T0 is the coarsest LOD, T1 the LODs between, and T2 LOD 0.
/// </summary>
/// <remarks>
/// <para>
/// A cooked sub-mesh of codec version 2 keeps its coarsest LOD inline, so T0 is resident at once. A request for T1 or T2
/// loads the separate LOD payloads of that tier that are not resident on a job thread (<see cref="SubMeshLodPayloads.TryLoadLod"/>):
/// the read, the length and checksum checks, the parse, and the hash checks. The prepared meshes are published at the next
/// frame boundary, which is the next <see cref="SetPlayhead"/> on the update thread (or <see cref="PublishPending"/>): the
/// backend sets <see cref="SubMeshLOD.Mesh"/>, each <see cref="RenderableMesh"/> of the sub-mesh makes the LOD selectable and
/// advances its LOD registration version, and then the backend completes the ticket. The render thread does no disk IO and
/// no hash work for this. A LOD that one placement loaded is resident for all placements of the sub-mesh; a reference count
/// keeps it while any ticket holds it.
/// </para>
/// <para>
/// The backend follows the contract of <see cref="IResidencyBackend"/>. A cancel of running work drops the prepared data and
/// publishes nothing, so the residency does not change. A cancel of a complete ticket releases the LODs that the backend
/// published when no other ticket holds them: the LOD leaves the selectable set first, and its renderer goes to the deferred
/// destruction queue. A LOD that was resident before the backend (for example after a load with
/// <see cref="ELodPayloadLoadPolicy.All"/>) is never released, so the world outside the cutscene does not change.
/// </para>
/// <para>
/// An editor sub-mesh and an older cooked sub-mesh (codec version 1) have all LODs resident: <see cref="GetLevel"/> gives
/// <see cref="EResidencyLevel.Target"/>, and a request completes at once. <see cref="LegacySubMeshCount"/> counts the
/// version 1 sub-meshes, because eviction cannot release their LODs. A LOD that fails to load stays not resident, its ticket
/// does not complete, and one warning names the sub-mesh, the LOD, and the reason.
/// </para>
/// </remarks>
public sealed class MeshResidencyBackend : IResidencyBackend, IDisposable
{
    private static readonly Action<object?> LoadAction = static state => ((LodLoad)state!).Owner.Load((LodLoad)state);

    private readonly IReadOnlyList<SceneNode> _roots;
    private readonly TaskScheduler _scheduler;
    private readonly Lock _sync = new();
    private readonly Dictionary<long, BackendTicket> _tickets = [];
    private readonly Dictionary<SubMeshLOD, LodLoad> _loads = new(ReferenceEqualityComparer.Instance);
    private readonly List<LodLoad> _prepared = [];
    private readonly List<LodLoad> _publishScratch = [];
    private readonly List<long> _completedScratch = [];
    private readonly HashSet<SubMeshLOD> _warnedLods = new(ReferenceEqualityComparer.Instance);
    private Dictionary<Guid, MeshAssetEntry> _entries = [];
    private CutsceneResidencyPlanner? _planner;
    private long _nextTicketId;
    private bool _disposed;
    private int _rejectedRequestCount;
    private int _failedLoadCount;

    /// <summary>Creates a backend that finds sub-meshes under scene roots.</summary>
    /// <param name="roots">The roots of the scene hierarchies to search. The backend searches them again at each selection.</param>
    public MeshResidencyBackend(IEnumerable<SceneNode> roots)
        : this(roots, TaskScheduler.Default)
    {
    }

    /// <summary>Creates a backend with a scheduler for the load jobs. A test runs the scheduler by hand.</summary>
    /// <param name="roots">The roots of the scene hierarchies to search.</param>
    /// <param name="scheduler">The scheduler of the load jobs.</param>
    internal MeshResidencyBackend(IEnumerable<SceneNode> roots, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(scheduler);
        _roots = [.. roots];
        _scheduler = scheduler;
        RebuildIndex();
    }

    /// <summary>The number of sub-mesh assets that the backend finds.</summary>
    public int AssetCount => _entries.Count;

    /// <summary>
    /// The number of found sub-meshes that came from an older cooked payload (codec version 1). They load with all LODs
    /// resident, and eviction cannot release their LODs. Cook the content again for progressive mesh loading.
    /// </summary>
    public int LegacySubMeshCount { get; private set; }

    /// <summary>The number of requests that the backend did not accept, because the asset is not a found sub-mesh.</summary>
    public int RejectedRequestCount => Volatile.Read(ref _rejectedRequestCount);

    /// <summary>The number of LOD payload loads that failed.</summary>
    public int FailedLoadCount => Volatile.Read(ref _failedLoadCount);

    /// <summary>The number of LODs that gave a load warning. Each LOD warns one time for the life of the backend.</summary>
    public int WarnedLodCount
    {
        get
        {
            lock (_sync)
                return _warnedLods.Count;
        }
    }

    /// <summary>
    /// The largest number of LODs that one <see cref="PublishPending"/> publishes. Each publication builds a renderer for
    /// each placement on the update thread, and the next render swap registers the new LOD in the GPU scene mesh atlas.
    /// The cost of that registration grows with the size of the atlas, not with the size of the LOD, so the count of
    /// publications in one frame sets the swap cost. A jump that completes many loads spreads them over frames. The rest
    /// stay prepared for the next frame. The default is <see cref="DefaultMaxPublicationsPerFrame"/>.
    /// </summary>
    public int MaxPublicationsPerFrame { get; set; } = DefaultMaxPublicationsPerFrame;

    /// <summary>
    /// The default of <see cref="MaxPublicationsPerFrame"/>. Two publications per frame keep the swap of a cold jump in
    /// Sponza near the frame budget, and all T1 LODs still arrive in about 200 ms at 60 Hz.
    /// </summary>
    public const int DefaultMaxPublicationsPerFrame = 2;

    /// <summary>The number of LODs that the backend published and still holds.</summary>
    public int PublishedLodCount
    {
        get
        {
            lock (_sync)
            {
                int count = 0;
                foreach (LodLoad load in _loads.Values)
                    if (load.State == ELoadState.Published)
                        count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Builds the default backend of an open cutscene: the roots of the bound nodes, and the scene roots of their worlds.
    /// </summary>
    /// <param name="compiled">The compiled cutscene. The backend does not use it now.</param>
    /// <param name="bindings">The resolved bindings.</param>
    public static IResidencyBackend? CreateDefault(CompiledCutscene compiled, CutsceneBindingTable bindings)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(bindings);
        return new MeshResidencyBackend(CollectRoots(bindings));
    }

    /// <inheritdoc />
    public void AttachPlanner(CutsceneResidencyPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        lock (_sync)
            _planner = planner;
    }

    /// <inheritdoc />
    public void BeginSelection()
        => RebuildIndex();

    /// <inheritdoc />
    public bool CanResolve(Guid assetId)
        => _entries.ContainsKey(assetId);

    /// <inheritdoc />
    /// <remarks>Publishes the prepared LODs before the planner update.</remarks>
    public void SetPlayhead(CutsceneTime position, double rate)
        => PublishPending();

    /// <inheritdoc />
    public ResidencyTicket Request(in ResidencyRequest request)
    {
        if (!_entries.TryGetValue(request.AssetId, out MeshAssetEntry? entry))
            return Reject(request, "the asset is not a sub-mesh of the scene");
        if (request.TargetLevel is < (int)EResidencyLevel.Proxy or > (int)EResidencyLevel.Target)
            return Reject(request, "the level must be T0, T1, or T2");

        BackendTicket ticket;
        CutsceneResidencyPlanner? planner;
        List<LodLoad>? toStart = null;
        lock (_sync)
        {
            if (_disposed)
                return ResidencyTicket.None;

            List<LodLoad> loads = [];
            SubMeshLOD[] lods = entry.Lods;
            for (int i = 0; i < lods.Length; i++)
            {
                SubMeshLOD lod = lods[i];
                if (!lod.IsPayloadDeferred || (int)SubMeshLodTiers.GetTier(i, lods.Length) != request.TargetLevel)
                    continue;

                if (!_loads.TryGetValue(lod, out LodLoad? load))
                {
                    load = new LodLoad(this, lod, i, entry.SubMesh.Name);
                    _loads.Add(lod, load);
                }
                load.RefCount++;
                if (load.State == ELoadState.None)
                {
                    if (lod.Mesh is not null)
                        load.State = ELoadState.Resident;
                    else
                    {
                        load.State = ELoadState.Loading;
                        (toStart ??= []).Add(load);
                    }
                }
                loads.Add(load);
            }

            ticket = new BackendTicket(++_nextTicketId, entry, request.TargetLevel, [.. loads]);
            _tickets.Add(ticket.Id, ticket);
            ticket.Complete = AllResident(ticket);
            planner = ticket.Complete ? _planner : null;
        }

        if (toStart is not null)
            for (int i = 0; i < toStart.Count; i++)
                Task.Factory.StartNew(LoadAction, toStart[i], CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);

        // The level is resident already, so the planner hears of the completion at once.
        planner?.NotifyCompleted(new ResidencyTicket(ticket.Id));
        return new ResidencyTicket(ticket.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A cancel of running work drops its prepared mesh. A cancel of a complete ticket releases each LOD that the backend
    /// published and that no other ticket holds.
    /// </remarks>
    public void Cancel(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        List<LodLoad>? released = null;
        lock (_sync)
        {
            if (!_tickets.Remove(ticket.Id, out BackendTicket? entry))
                return;

            entry.Cancelled = true;
            for (int i = 0; i < entry.Loads.Length; i++)
            {
                LodLoad load = entry.Loads[i];
                if (--load.RefCount > 0)
                    continue;
                if (load.State is ELoadState.Prepared or ELoadState.Published)
                    (released ??= []).Add(load);
                else if (load.State != ELoadState.Loading)
                {
                    load.State = ELoadState.None;
                    _loads.Remove(load.Lod);
                }
            }
        }

        Release(released);
    }

    /// <inheritdoc />
    /// <remarks>The level is the highest tier of a resident LOD of the sub-mesh. It does not allocate.</remarks>
    public EResidencyLevel GetLevel(Guid assetId)
    {
        if (!_entries.TryGetValue(assetId, out MeshAssetEntry? entry))
            return EResidencyLevel.None;

        SubMeshLOD[] lods = entry.Lods;
        int level = (int)EResidencyLevel.None;
        for (int i = 0; i < lods.Length; i++)
        {
            if (lods[i].Mesh is null)
                continue;
            int tier = (int)SubMeshLodTiers.GetTier(i, lods.Length);
            if (tier > level)
                level = tier;
        }
        return (EResidencyLevel)level;
    }

    /// <summary>
    /// Publishes the LODs that finished loading and completes the tickets whose LODs are all resident. Call it on the
    /// update thread. <see cref="SetPlayhead"/> calls it before each planner update.
    /// </summary>
    public void PublishPending()
    {
        CutsceneResidencyPlanner? planner;
        lock (_sync)
        {
            if (_disposed || _prepared.Count == 0)
                return;

            _publishScratch.Clear();
            int limit = Math.Max(1, MaxPublicationsPerFrame);
            int taken = 0;
            for (; taken < _prepared.Count && _publishScratch.Count < limit; taken++)
                if (_prepared[taken].State == ELoadState.Prepared && _prepared[taken].RefCount > 0)
                    _publishScratch.Add(_prepared[taken]);
            _prepared.RemoveRange(0, taken);
        }

        // The scene objects react on this thread, so the backend lock is not held.
        for (int i = 0; i < _publishScratch.Count; i++)
        {
            LodLoad load = _publishScratch[i];
            XRMesh? mesh;
            bool alreadyResident;
            lock (_sync)
            {
                mesh = load.State == ELoadState.Prepared ? load.Prepared : null;
                alreadyResident = load.Lod.Mesh is not null;
                if (mesh is not null)
                {
                    load.Prepared = null;
                    // Another owner gave the LOD a mesh meanwhile. The backend did not publish it, so it never releases it.
                    load.State = alreadyResident ? ELoadState.Resident : ELoadState.Published;
                }
            }
            if (mesh is null)
                continue;
            if (alreadyResident)
                mesh.Destroy(now: true);
            else
                load.Lod.Mesh = mesh;
        }
        _publishScratch.Clear();

        lock (_sync)
        {
            _completedScratch.Clear();
            foreach (BackendTicket ticket in _tickets.Values)
            {
                if (ticket.Complete || !AllResident(ticket))
                    continue;
                // The level is set before the planner hears of the completion.
                ticket.Complete = true;
                _completedScratch.Add(ticket.Id);
            }
            planner = _planner;
        }

        if (planner is null)
            return;
        for (int i = 0; i < _completedScratch.Count; i++)
            planner.NotifyCompleted(new ResidencyTicket(_completedScratch[i]));
        _completedScratch.Clear();
    }

    /// <summary>Cancels all tickets and releases the LODs that the backend published. A running job drops its result.</summary>
    public void Dispose()
    {
        List<LodLoad>? released = null;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _planner = null;
            foreach (BackendTicket ticket in _tickets.Values)
                ticket.Cancelled = true;
            _tickets.Clear();
            foreach (LodLoad load in _loads.Values)
            {
                load.RefCount = 0;
                if (load.State is ELoadState.Prepared or ELoadState.Published)
                    (released ??= []).Add(load);
            }
            _prepared.Clear();
        }

        Release(released);
    }

    private void Release(List<LodLoad>? released)
    {
        if (released is null)
            return;

        for (int i = 0; i < released.Count; i++)
        {
            LodLoad load = released[i];
            XRMesh? prepared;
            bool unpublish;
            lock (_sync)
            {
                // A request between the cancel and this point can hold the LOD again.
                if (load.RefCount > 0)
                    continue;
                prepared = load.Prepared;
                unpublish = load.State == ELoadState.Published;
                load.Prepared = null;
                load.State = ELoadState.None;
                _loads.Remove(load.Lod);
            }

            // A prepared mesh never reached a renderer. A published mesh leaves the renderables first; their renderers go
            // to the deferred destruction queue, so the frames in flight keep their resources.
            prepared?.Destroy(now: true);
            if (unpublish)
                load.Lod.Mesh = null;
        }
    }

    private void Load(LodLoad load)
    {
        lock (_sync)
        {
            if (load.RefCount == 0 || _disposed)
            {
                load.State = ELoadState.None;
                _loads.Remove(load.Lod);
                return;
            }
        }

        XRMesh? mesh = null;
        string? failure;
        try
        {
            if (!SubMeshLodPayloads.TryLoadLod(load.Lod.CookedDescriptor!, out mesh, out failure))
                mesh = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            mesh = null;
            failure = ex.Message;
        }

        bool keep = false;
        lock (_sync)
        {
            if (load.RefCount == 0 || _disposed)
            {
                load.State = ELoadState.None;
                _loads.Remove(load.Lod);
            }
            else if (mesh is null)
                load.State = ELoadState.Failed;
            else
            {
                load.Prepared = mesh;
                load.State = ELoadState.Prepared;
                _prepared.Add(load);
                keep = true;
            }
        }

        if (mesh is null)
        {
            if (failure is not null)
            {
                Interlocked.Increment(ref _failedLoadCount);
                bool warn;
                lock (_sync)
                    warn = _warnedLods.Add(load.Lod);
                if (warn)
                    Debug.LogWarning(ELogCategory.Meshes, "Cutscene mesh residency: cooked sub-mesh '{0}' LOD {1} stays not resident: {2}.", load.SubMeshName ?? string.Empty, load.Index, failure);
            }
            return;
        }
        if (!keep)
            mesh.Destroy(now: true);
    }

    private static bool AllResident(BackendTicket ticket)
    {
        for (int i = 0; i < ticket.Loads.Length; i++)
            if (ticket.Loads[i].State is not (ELoadState.Resident or ELoadState.Published))
                return false;
        return true;
    }

    private ResidencyTicket Reject(in ResidencyRequest request, string reason)
    {
        Interlocked.Increment(ref _rejectedRequestCount);
        Debug.LogWarning(ELogCategory.Meshes, "Cutscene mesh residency: the request for asset {0} at level T{1} (generation {2}) was not accepted: {3}.", request.AssetId, request.TargetLevel, request.Generation, reason);
        return ResidencyTicket.None;
    }

    /// <summary>Finds the sub-meshes under the roots again. It allocates, so it runs at a selection and not in each frame.</summary>
    private void RebuildIndex()
    {
        Dictionary<Guid, MeshAssetEntry> entries = [];
        HashSet<SubMesh> legacy = new(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < _roots.Count; r++)
            Visit(_roots[r], entries, legacy);
        _entries = entries;
        LegacySubMeshCount = legacy.Count;
    }

    private static void Visit(SceneNode node, Dictionary<Guid, MeshAssetEntry> entries, HashSet<SubMesh> legacy)
    {
        var components = node.Components;
        for (int c = 0; c < components.Count; c++)
        {
            if (components[c] is not ModelComponent { Model: { } model })
                continue;

            var meshes = model.Meshes;
            for (int m = 0; m < meshes.Count; m++)
            {
                if (meshes[m] is not { } subMesh)
                    continue;
                entries[CutsceneResidencyAssetKeys.ForSubMesh(node, c, m)] = new MeshAssetEntry(subMesh, [.. subMesh.LODs]);
                if (subMesh.CookedCodecVersion == 1)
                    legacy.Add(subMesh);
            }
        }

        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i]?.SceneNode is { } child)
                Visit(child, entries, legacy);
    }

    private static List<SceneNode> CollectRoots(CutsceneBindingTable bindings)
    {
        HashSet<SceneNode> seen = new(ReferenceEqualityComparer.Instance);
        List<SceneNode> roots = [];
        for (int slot = 0; slot < bindings.SlotCount; slot++)
        {
            if (bindings.GetNode(slot) is not { } node)
                continue;

            SceneNode root = CutsceneResidencySceneWalker.GetRoot(node);
            if (seen.Add(root))
                roots.Add(root);
            if (root.World is not RuntimeWorld { TargetWorld: { } world })
                continue;

            var scenes = world.Scenes;
            for (int s = 0; s < scenes.Count; s++)
            {
                if (scenes[s] is not { } scene)
                    continue;
                var sceneRoots = scene.RootNodes;
                for (int n = 0; n < sceneRoots.Count; n++)
                    if (sceneRoots[n] is { } sceneRoot && seen.Add(sceneRoot))
                        roots.Add(sceneRoot);
            }
        }
        return roots;
    }

    /// <summary>The load state of one LOD that a ticket holds.</summary>
    private enum ELoadState
    {
        None,
        Loading,
        Prepared,
        Published,
        Resident,
        Failed,
    }

    /// <summary>One found sub-mesh, with its LODs in sort order.</summary>
    private sealed class MeshAssetEntry(SubMesh subMesh, SubMeshLOD[] lods)
    {
        public SubMesh SubMesh { get; } = subMesh;
        public SubMeshLOD[] Lods { get; } = lods;
    }

    /// <summary>The shared load of one LOD. All tickets that need the LOD hold it, with a reference count.</summary>
    private sealed class LodLoad(MeshResidencyBackend owner, SubMeshLOD lod, int index, string? subMeshName)
    {
        public MeshResidencyBackend Owner { get; } = owner;
        public SubMeshLOD Lod { get; } = lod;
        public int Index { get; } = index;
        public string? SubMeshName { get; } = subMeshName;
        public int RefCount { get; set; }
        public ELoadState State { get; set; }
        public XRMesh? Prepared { get; set; }
    }

    /// <summary>One request of the planner and the LODs it holds.</summary>
    private sealed class BackendTicket(long id, MeshAssetEntry entry, int level, LodLoad[] loads)
    {
        public long Id { get; } = id;
        public MeshAssetEntry Entry { get; } = entry;
        public int Level { get; } = level;
        public LodLoad[] Loads { get; } = loads;
        public bool Complete { get; set; }
        public bool Cancelled { get; set; }
    }
}
