using MemoryPack;

namespace XREngine.Rendering;

public partial class XRMeshRenderer
{
    // While a view is bound, a GPU palette producer owns the palette of this renderer. The
    // renderer reads its slice of the view's buffers at _externalSkinPaletteBase. A producer
    // can bind many renderers to one shared view and then publish new buffers to all of them
    // with one write.
    private object? _externalSkinPaletteSourceOwner;
    private SkinPaletteView? _externalSkinPaletteView;
    // The publication version of the bound view when it was bound. Each later publication
    // advances SkinnedOutputVersion by one.
    private ulong _externalSkinPaletteViewBindVersion;
    // The view of the buffer-based source API. It is created on first use and kept.
    private SkinPaletteView? _ownedSkinPaletteView;
    private uint _externalSkinPaletteBase;
    private uint _externalSkinPaletteCount;
    private bool _externalSkinPaletteComplete;
    private bool _externalSkinPaletteHistoryValid;

    // Advances when a renderer loses its external palette source, or gets another source
    // owner or view, in any renderer. A producer that keeps its bindings compares it with
    // its last value to find a source change that it did not make.
    private static long s_externalSkinPaletteSourceEpoch;

    /// <summary>
    /// Gets the number of external palette source losses and owner or view changes in all
    /// renderers.
    /// </summary>
    internal static long ExternalSkinPaletteSourceEpoch => Interlocked.Read(ref s_externalSkinPaletteSourceEpoch);

    [MemoryPackIgnore]
    public XRDataBuffer? ActiveSkinPaletteBuffer
        => ExternalSkinPaletteViewOrNull is { } view ? view.Current : SkinPaletteBuffer;

    [MemoryPackIgnore]
    public XRDataBuffer? ActivePreviousSkinPaletteBuffer
        => ExternalSkinPaletteViewOrNull is { } view ? view.Previous : PreviousSkinPaletteBuffer;

    /// <summary>Reports whether the external palette keeps the prior rendered pose.</summary>
    [MemoryPackIgnore]
    public bool HasValidGpuDrivenPaletteHistory => Volatile.Read(ref _externalSkinPaletteHistoryValid);

    [MemoryPackIgnore]
    public uint ActiveSkinPaletteBase => HasExternalSkinPaletteSource ? _externalSkinPaletteBase : 0u;

    [MemoryPackIgnore]
    public uint ActiveSkinPaletteCount => HasExternalSkinPaletteSource ? _externalSkinPaletteCount : (uint)(Mesh?.UtilizedBones?.Length ?? 0) + 1u;

    [MemoryPackIgnore]
    public bool HasExternalSkinPaletteSource => ExternalSkinPaletteViewOrNull is not null;

    /// <summary>Gets the bound view when compute skinning is on and the view holds a current palette.</summary>
    private SkinPaletteView? ExternalSkinPaletteViewOrNull
    {
        get
        {
            SkinPaletteView? view = Volatile.Read(ref _externalSkinPaletteView);
            return RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader &&
                view?.Current is not null && _externalSkinPaletteCount > 0u
                ? view
                : null;
        }
    }

    /// <summary>
    /// Gets the number of publications of the bound view since it was bound. The skinned
    /// output version includes this number, so each new external palette output changes the
    /// version without a call to this renderer.
    /// </summary>
    private ulong ExternalSkinPaletteOutputAdvance
    {
        get
        {
            SkinPaletteView? view = Volatile.Read(ref _externalSkinPaletteView);
            return view is null
                ? 0UL
                : unchecked(view.PublicationVersion - Volatile.Read(ref _externalSkinPaletteViewBindVersion));
        }
    }

    /// <summary>
    /// Publishes GPU palette buffers for this renderer through its own view. Buffers that only
    /// swap current and previous are the ping-pong of one producer, not a structural change.
    /// A stale bone-buffer generation rejects the publication and returns false.
    /// </summary>
    internal bool SetGpuDrivenSkinPaletteSource(
        object owner,
        XRDataBuffer skinPalette,
        XRDataBuffer? previousSkinPalette,
        uint baseElement,
        uint elementCount,
        long boneBufferGeneration,
        bool drivesCompleteBonePalette,
        bool previousPaletteValid = true)
    {
        GpuDrivenBoneCoverageSnapshot? change;
        bool structuralChange;
        lock (_dirtyBoneSyncRoot)
        {
            if (boneBufferGeneration != _boneBufferGeneration)
                return false;

            SkinPaletteView view = _ownedSkinPaletteView ??= new();
            bool bound = ReferenceEquals(_externalSkinPaletteView, view);
            bool sameBuffers = bound && ReferenceEquals(view.Current, skinPalette) &&
                ReferenceEquals(view.Previous, previousSkinPalette);
            bool swappedBuffers = bound && ReferenceEquals(view.Current, previousSkinPalette) &&
                ReferenceEquals(view.Previous, skinPalette);
            // Only this renderer reads its own view, so the buffers can change before the bind.
            view.Publish(skinPalette, previousSkinPalette);
            structuralChange = BindExternalSkinPaletteViewLocked(owner, view, baseElement, elementCount,
                drivesCompleteBonePalette, previousPaletteValid, out change) || !sameBuffers && !swappedBuffers;
        }
        if (structuralChange)
            MarkSkinnedOutputDirty();
        if (change is { } changedSnapshot)
            _gpuDrivenBoneCoverageChanged?.Invoke(this, changedSnapshot);
        return true;
    }

    /// <summary>
    /// Binds this renderer to a shared palette view at one slice. The producer publishes the
    /// view's buffers for each output; this call is needed only when the owner, view, slice,
    /// completeness, or history validity changes. A stale bone-buffer generation rejects the
    /// binding and returns false.
    /// </summary>
    internal bool BindGpuDrivenSkinPaletteView(
        object owner,
        SkinPaletteView view,
        uint baseElement,
        uint elementCount,
        long boneBufferGeneration,
        bool drivesCompleteBonePalette,
        bool previousPaletteValid)
    {
        GpuDrivenBoneCoverageSnapshot? change;
        bool structuralChange;
        lock (_dirtyBoneSyncRoot)
        {
            if (boneBufferGeneration != _boneBufferGeneration)
                return false;
            structuralChange = BindExternalSkinPaletteViewLocked(owner, view, baseElement, elementCount,
                drivesCompleteBonePalette, previousPaletteValid, out change);
        }
        if (structuralChange)
            MarkSkinnedOutputDirty();
        if (change is { } changedSnapshot)
            _gpuDrivenBoneCoverageChanged?.Invoke(this, changedSnapshot);
        return true;
    }

    /// <summary>
    /// Moves this renderer from a shared view to its own view with the same buffers. A producer
    /// calls this before it publishes buffers that no longer hold the slice of this renderer, so
    /// the renderer keeps the last palette that it received.
    /// </summary>
    internal void DetachSkinPaletteView(SkinPaletteView view)
    {
        lock (_dirtyBoneSyncRoot)
        {
            if (!ReferenceEquals(_externalSkinPaletteView, view))
                return;
            SkinPaletteView owned = _ownedSkinPaletteView ??= new();
            owned.Publish(view.Current, view.Previous);
            FoldExternalSkinPaletteOutputAdvanceLocked();
            Volatile.Write(ref _externalSkinPaletteViewBindVersion, owned.PublicationVersion);
            Volatile.Write(ref _externalSkinPaletteView, owned);
        }
    }

    internal void ClearGpuDrivenSkinPaletteSource(object owner)
    {
        GpuDrivenBoneCoverageSnapshot? change = null;
        lock (_dirtyBoneSyncRoot)
        {
            if (!ReferenceEquals(_externalSkinPaletteSourceOwner, owner))
                return;
            bool oldCoverage = _hasCompleteGpuDrivenBoneCoverage;
            ClearExternalSkinPaletteSourceLocked();
            GpuDrivenBoneCoverageSnapshot snapshot = RefreshGpuDrivenBoneCoverageLocked();
            if (snapshot.IsFullyCovered != oldCoverage)
                change = snapshot;
        }
        MarkSkinnedOutputDirty();
        if (change is { } changedSnapshot)
            _gpuDrivenBoneCoverageChanged?.Invoke(this, changedSnapshot);
    }

    /// <summary>
    /// Stores the owner, view, and slice. Returns true when the owner, view, or slice changed.
    /// The caller holds <see cref="_dirtyBoneSyncRoot"/>.
    /// </summary>
    /// <param name="change">The new coverage when it changed; otherwise null.</param>
    private bool BindExternalSkinPaletteViewLocked(object owner, SkinPaletteView view, uint baseElement,
        uint elementCount, bool drivesCompleteBonePalette, bool previousPaletteValid,
        out GpuDrivenBoneCoverageSnapshot? change)
    {
        bool oldCoverage = _hasCompleteGpuDrivenBoneCoverage;
        bool sameOwner = ReferenceEquals(_externalSkinPaletteSourceOwner, owner);
        bool sameView = ReferenceEquals(_externalSkinPaletteView, view);
        bool structuralChange = !sameOwner || !sameView || _externalSkinPaletteBase != baseElement ||
            _externalSkinPaletteCount != elementCount ||
            _externalSkinPaletteComplete != drivesCompleteBonePalette;
        if (!sameView)
        {
            FoldExternalSkinPaletteOutputAdvanceLocked();
            Volatile.Write(ref _externalSkinPaletteViewBindVersion, view.PublicationVersion);
            Volatile.Write(ref _externalSkinPaletteView, view);
        }
        if (!sameOwner || !sameView)
            Interlocked.Increment(ref s_externalSkinPaletteSourceEpoch);
        _externalSkinPaletteSourceOwner = owner;
        _externalSkinPaletteBase = baseElement;
        _externalSkinPaletteCount = elementCount;
        _externalSkinPaletteComplete = drivesCompleteBonePalette;
        SetField(ref _externalSkinPaletteHistoryValid, previousPaletteValid, publishNotifications: false,
            propertyName: nameof(HasValidGpuDrivenPaletteHistory));
        GpuDrivenBoneCoverageSnapshot snapshot = RefreshGpuDrivenBoneCoverageLocked();
        change = snapshot.IsFullyCovered != oldCoverage ? snapshot : null;
        return structuralChange;
    }

    /// <summary>
    /// Adds the outputs of the bound view to the renderer's own skinned output version before
    /// the view changes, so the version never moves back. The caller holds
    /// <see cref="_dirtyBoneSyncRoot"/>.
    /// </summary>
    private void FoldExternalSkinPaletteOutputAdvanceLocked()
    {
        ulong advance = ExternalSkinPaletteOutputAdvance;
        if (advance != 0UL)
            _skinnedOutputInvalidation.AddVersions(advance);
    }

    private void ClearExternalSkinPaletteSourceLocked()
    {
        bool hadSource = _externalSkinPaletteView is not null || _externalSkinPaletteSourceOwner is not null;
        FoldExternalSkinPaletteOutputAdvanceLocked();
        _externalSkinPaletteSourceOwner = null;
        Volatile.Write(ref _externalSkinPaletteView, null);
        Volatile.Write(ref _externalSkinPaletteViewBindVersion, 0UL);
        // Release the buffer references of the buffer-based source.
        _ownedSkinPaletteView?.Publish(null, null);
        _externalSkinPaletteBase = 0u;
        _externalSkinPaletteCount = 0u;
        _externalSkinPaletteComplete = false;
        SetField(ref _externalSkinPaletteHistoryValid, false, publishNotifications: false,
            propertyName: nameof(HasValidGpuDrivenPaletteHistory));
        if (hadSource)
            Interlocked.Increment(ref s_externalSkinPaletteSourceEpoch);
    }
}
