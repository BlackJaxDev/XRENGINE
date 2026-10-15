using XREngine.Rendering.Commands;

namespace XREngine.Components.Lights;

/// <summary>
/// Keeps the collection and publication state of the primary shadow view of one directional light. It holds the
/// decision of the shadow atlas planning for the current collect pass, the collection that waits for the swap, and the
/// publication that the render side can read.
/// </summary>
/// <remarks>
/// <para>
/// The collect thread owns the plan. The atlas planning clears the plan at the start of each collect pass and records
/// it before the light collects. Only the collect thread reads it.
/// </para>
/// <para>
/// A gated collection latches the generation of the prepared frame package of the view. The swap publishes the view
/// only when the updating package of the same command collection still has that generation and is prepared. The
/// publication becomes current only when the rendering package then has the latched generation and is published. In all
/// other cases the publication becomes stale. Thus a swap never publishes an uncollected, empty updating buffer.
/// </para>
/// <para>
/// A render-thread path that needs the view now first takes a latched gated collection and publishes it before the swap
/// of its pass. The publication then also serves the plan of that pass, and the swap keeps it. Only without a latch does
/// the render thread collect into the shared updating buffer. Such a collection can overlap a gated collection that has
/// not latched yet. The overlap declines the gated collection, and a publication of the merged package stays stale.
/// </para>
/// <para>
/// A declined collection stays in the updating buffer until a swap clears it, and a later collection adds to it. The
/// light therefore swaps a declined collection out while its publication is stale, so a current publication never
/// contains the casters of a declined collection.
/// </para>
/// <para>
/// A stale publication changes <see cref="ResolveSignature"/> to a sentinel value that is unique for each stale epoch.
/// The atlas content hash then cannot match a resident tile that the old publication rendered.
/// </para>
/// <para>
/// The collect thread, the render thread and the application thread change the latch and the publication. A private
/// lock guards these fields. The lock is a leaf lock: the code that holds it reads only fields and frame package
/// properties, and it takes no other lock.
/// </para>
/// </remarks>
internal sealed class DirectionalPrimaryShadowPublicationState
{
    private const ulong StaleSignatureSalt = 0x9E3779B97F4A7C15UL;

    private readonly Lock _lock = new();

    private bool _hasPlan;
    private EDirectionalPrimaryShadowNeed _plannedNeed;
    private bool _plannedShadowOnlyCasters;
    private ulong _planFrameId;

    private RenderCommandCollection? _latchedCommands;
    private long _latchedPackageGeneration;
    private ulong _latchedPlanFrameId;

    private RenderCommandCollection? _publishedCommands;
    private long _publishedPackageGeneration;
    private ulong _publishedPlanFrameId;
    private bool _publishedBeforePassSwap;
    private ulong _staleEpoch;

    /// <summary>
    /// Gets the shadow atlas frame of the plan that the current collect pass recorded, or zero when no plan exists.
    /// Only the collect thread reads it.
    /// </summary>
    public ulong PlanFrameId => _planFrameId;

    /// <summary>
    /// Removes the plan and the latch. The atlas planning calls this for each directional light at the start of each
    /// collect pass.
    /// </summary>
    public void ClearPlan()
    {
        _hasPlan = false;
        _plannedNeed = EDirectionalPrimaryShadowNeed.None;
        _plannedShadowOnlyCasters = false;
        _planFrameId = 0UL;
        lock (_lock)
        {
            ClearLatchNoLock();
            _publishedBeforePassSwap = false;
        }
    }

    /// <summary>
    /// Records the primary decision of the atlas planning for the current collect pass.
    /// </summary>
    /// <param name="need">The first planning term that makes the primary shadow view necessary.</param>
    /// <param name="shadowOnlyCasters">
    /// Whether the scene can have shadow-casting renderables that no main view collected. The gate uses this term only
    /// when <paramref name="need"/> is <see cref="EDirectionalPrimaryShadowNeed.None"/>.
    /// </param>
    /// <param name="planFrameId">The shadow atlas frame of the render plan that this pass builds.</param>
    public void RecordPlan(EDirectionalPrimaryShadowNeed need, bool shadowOnlyCasters, ulong planFrameId)
    {
        _hasPlan = true;
        _plannedNeed = need;
        _plannedShadowOnlyCasters = shadowOnlyCasters;
        _planFrameId = planFrameId;
    }

    /// <summary>Gets the plan of the current collect pass. Returns false when the atlas planning recorded no plan.</summary>
    public bool TryGetPlan(out EDirectionalPrimaryShadowNeed need, out bool shadowOnlyCasters)
    {
        need = _plannedNeed;
        shadowOnlyCasters = _plannedShadowOnlyCasters;
        return _hasPlan;
    }

    /// <summary>
    /// Latches a gated collection of <paramref name="commands"/>. Returns false, and removes any latch, when the
    /// collection prepared no package, or when a swap of the same collection ran while it collected.
    /// </summary>
    /// <param name="commands">The command collection of the primary shadow view.</param>
    /// <param name="renderingGenerationBeforeCollect">
    /// The generation of the rendering package of <paramref name="commands"/>, read just before the collection.
    /// </param>
    public bool Latch(RenderCommandCollection commands, long renderingGenerationBeforeCollect)
    {
        BackendReadyFramePackage updating = commands.UpdatingBackendReadyPackage;
        bool prepared = updating.State == EBackendReadyFramePackageState.Prepared;
        long generation = updating.PackageGeneration;
        bool noSwapDuringCollection =
            commands.RenderingBackendReadyPackage.PackageGeneration == renderingGenerationBeforeCollect;

        lock (_lock)
        {
            if (!prepared || !noSwapDuringCollection)
            {
                ClearLatchNoLock();
                return false;
            }

            _latchedCommands = commands;
            _latchedPackageGeneration = generation;
            _latchedPlanFrameId = _planFrameId;
            return true;
        }
    }

    /// <summary>Removes the latch.</summary>
    public void ClearLatch()
    {
        lock (_lock)
            ClearLatchNoLock();
    }

    /// <summary>
    /// Takes and removes the latch. Returns true only when the latch belongs to <paramref name="commands"/>, and the
    /// updating package of <paramref name="commands"/> still has the latched generation and is prepared.
    /// </summary>
    /// <param name="commands">The command collection of the primary shadow view.</param>
    /// <param name="generation">The latched package generation.</param>
    /// <param name="planFrameId">The shadow atlas frame of the plan of the pass that latched the collection.</param>
    public bool TryTakeLatch(RenderCommandCollection commands, out long generation, out ulong planFrameId)
    {
        lock (_lock)
        {
            RenderCommandCollection? latched = _latchedCommands;
            generation = _latchedPackageGeneration;
            planFrameId = _latchedPlanFrameId;
            ClearLatchNoLock();
            if (!ReferenceEquals(latched, commands))
                return false;

            BackendReadyFramePackage updating = commands.UpdatingBackendReadyPackage;
            return updating.State == EBackendReadyFramePackageState.Prepared &&
                updating.PackageGeneration == generation;
        }
    }

    /// <summary>
    /// Reads the generation of the prepared updating package of <paramref name="commands"/> after a forced collection.
    /// Returns false when the collection prepared no package, or when a swap of the same collection ran while it
    /// collected.
    /// </summary>
    public static bool TryReadPreparedGeneration(
        RenderCommandCollection commands,
        long renderingGenerationBeforeCollect,
        out long generation)
    {
        BackendReadyFramePackage updating = commands.UpdatingBackendReadyPackage;
        generation = updating.PackageGeneration;
        return updating.State == EBackendReadyFramePackageState.Prepared &&
            commands.RenderingBackendReadyPackage.PackageGeneration == renderingGenerationBeforeCollect;
    }

    /// <summary>
    /// Returns whether the updating buffer of <paramref name="commands"/> holds a collection that no swap published:
    /// a prepared package or collected commands.
    /// </summary>
    public static bool HasUnpublishedCollection(RenderCommandCollection commands)
        => commands.UpdatingBackendReadyPackage.State == EBackendReadyFramePackageState.Prepared ||
            commands.GetUpdatingCommandCount() > 0;

    /// <summary>
    /// Makes the publication current when the rendering package of <paramref name="commands"/> is published with
    /// <paramref name="generation"/>. Otherwise makes the publication stale and returns false.
    /// </summary>
    /// <param name="commands">The command collection of the primary shadow view.</param>
    /// <param name="generation">The package generation that the swap must have published.</param>
    /// <param name="planFrameId">The shadow atlas frame of the plan that the publication serves.</param>
    /// <param name="beforePassSwap">
    /// True when a render-thread path published the latched gated collection before the swap of its pass. That swap then
    /// keeps the publication through <see cref="TryKeepPublicationAtPassSwap"/>.
    /// </param>
    public bool TryMarkPublished(RenderCommandCollection commands, long generation, ulong planFrameId, bool beforePassSwap)
    {
        lock (_lock)
        {
            BackendReadyFramePackage rendering = commands.RenderingBackendReadyPackage;
            if (rendering.State != EBackendReadyFramePackageState.Published ||
                rendering.PackageGeneration != generation)
            {
                MarkStaleNoLock();
                return false;
            }

            _publishedCommands = commands;
            _publishedPackageGeneration = generation;
            _publishedPlanFrameId = planFrameId;
            _publishedBeforePassSwap = beforePassSwap;
            return true;
        }
    }

    /// <summary>
    /// Returns whether the swap of the current pass can keep the publication: a render-thread path published the gated
    /// collection of this pass before the swap, and that publication is still current. The call consumes that state.
    /// </summary>
    public bool TryKeepPublicationAtPassSwap(RenderCommandCollection commands)
    {
        lock (_lock)
        {
            bool keep = _publishedBeforePassSwap && IsCurrentNoLock(commands);
            _publishedBeforePassSwap = false;
            return keep;
        }
    }

    /// <summary>Makes the publication stale. The next stale signature differs from all earlier ones.</summary>
    public void MarkStale()
    {
        lock (_lock)
            MarkStaleNoLock();
    }

    /// <summary>
    /// Makes the publication stale and removes the latch. Call it when the light deactivates, stops casting shadows,
    /// changes its shadow atlas mode, or replaces or resizes its primary shadow view.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            ClearLatchNoLock();
            MarkStaleNoLock();
        }
    }

    /// <summary>
    /// Returns whether the rendering package of <paramref name="commands"/> is the current publication: the last swap
    /// published a gated or forced collection of the same command collection.
    /// </summary>
    public bool IsCurrent(RenderCommandCollection commands)
    {
        lock (_lock)
            return IsCurrentNoLock(commands);
    }

    /// <summary>
    /// Returns whether the current publication of <paramref name="commands"/> belongs to the shadow atlas plan of
    /// <paramref name="planFrameId"/> or to a newer plan. A newer plan occurs when a render-thread path published the
    /// gated collection of the next pass, which is newer content than the plan in progress needs.
    /// </summary>
    public bool IsCurrentForPlanOrNewer(RenderCommandCollection commands, ulong planFrameId)
    {
        lock (_lock)
            return planFrameId != 0UL &&
                _publishedPlanFrameId >= planFrameId &&
                IsCurrentNoLock(commands);
    }

    /// <summary>
    /// Returns <paramref name="publishedSignature"/> when the publication of <paramref name="commands"/> is current.
    /// Otherwise returns a sentinel signature that is unique for the current stale epoch.
    /// </summary>
    public ulong ResolveSignature(RenderCommandCollection commands, ulong publishedSignature)
    {
        lock (_lock)
            return IsCurrentNoLock(commands)
                ? publishedSignature
                : CreateStaleSignature(_staleEpoch);
    }

    /// <summary>Creates the nonzero sentinel signature of a stale epoch.</summary>
    internal static ulong CreateStaleSignature(ulong staleEpoch)
    {
        ulong z = staleEpoch ^ StaleSignatureSalt;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z != 0UL ? z : StaleSignatureSalt;
    }

    private bool IsCurrentNoLock(RenderCommandCollection commands)
    {
        if (!ReferenceEquals(_publishedCommands, commands))
            return false;

        BackendReadyFramePackage rendering = commands.RenderingBackendReadyPackage;
        return rendering.State == EBackendReadyFramePackageState.Published &&
            rendering.PackageGeneration == _publishedPackageGeneration;
    }

    private void ClearLatchNoLock()
    {
        _latchedCommands = null;
        _latchedPackageGeneration = 0L;
        _latchedPlanFrameId = 0UL;
    }

    private void MarkStaleNoLock()
    {
        _publishedCommands = null;
        _publishedPackageGeneration = 0L;
        _publishedPlanFrameId = 0UL;
        _publishedBeforePassSwap = false;
        unchecked { _staleEpoch++; }
    }
}
