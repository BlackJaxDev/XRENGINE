using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>One complete owner-built canvas state. Readers retain this object while using it.</summary>
public sealed class UIPublication
{
    private static readonly Action<UIRenderRecord> RetainRenderRecordAction = RetainRenderRecord;
    private static readonly Action<UIRenderRecord> ReleaseRenderRecordAction = ReleaseRenderRecord;
    private UIHitRecord[]? _hits;
    private readonly UIRecordStore<UIHitRecord>? _hitStore;
    private readonly UICompositionSegmentSet _compositionSegments = UICompositionSegmentSet.Empty;
    private UIRenderRecord[]? _renders;
    private readonly int _visibleBackdropCount;
    private readonly int _visiblePreRenderCount;
    private readonly UIScrollRecord[] _scrollReceivers;
    private readonly UIFocusScopeRecord[] _focusScopes;
    private readonly UIRecordStore<UIRenderRecord>? _renderStore;
    private UILayoutNodeOutput[]? _flatLayout;
    private bool _retainedSurface;
    private bool _retainedQuadPayload;
    private int _claims = 1;
    private int _resourcesRetired;
    private readonly Dictionary<long, int> _renderByMembership;
    private UIPublishedWorldMeshCommand[]? _worldCommands;
    private readonly object _worldCommandGate = new();

    /// <summary>
    /// Creates a publication from complete record arrays. A null record array, or the
    /// array of <paramref name="previous"/>, shares the previous publication's store.
    /// </summary>
    internal UIPublication(ulong generation, ulong canvasLifetime, ulong layoutGeneration, ulong hitGeneration,
        long changeRevision, ReadOnlyMemory<UILayoutNodeOutput> layout,
        ReadOnlyMemory<UIElementIdentity> identities, UIHitRecord[]? hits, UIRenderRecord[]? renders,
        BoundingRectangleF canvasBounds, Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        UIPublication? previous = null,
        UIScrollRecord[]? scrollReceivers = null,
        UIFocusScopeRecord[]? focusScopes = null,
        UILayoutOutputPageView? layoutPages = null,
        Matrix4x4? authoringCanvasWorldMatrix = null,
        ECanvasDrawSpace authoringDrawSpace = ECanvasDrawSpace.Screen,
        bool authoringOffscreen = false,
        UIRenderSurface? offscreenSurface = null,
        UIDrawPayload? offscreenQuadPayload = null,
        Matrix4x4? offscreenQuadWorldMatrix = null)
    {
        Generation = generation;
        RenderGeneration = generation;
        CanvasLifetime = canvasLifetime;
        LayoutGeneration = layoutGeneration;
        HitGeneration = hitGeneration;
        ChangeRevision = changeRevision;
        _flatLayout = layout.IsEmpty ? null : layout.ToArray();
        LayoutPages = layoutPages;
        Identities = identities;
        bool shareHits = previous is not null && (hits is null || ReferenceEquals(hits, previous._hits));
        bool shareRenders = previous is not null &&
            (renders is null || ReferenceEquals(renders, previous._renders));
        if (!shareHits)
            ArgumentNullException.ThrowIfNull(hits);
        if (!shareRenders)
            ArgumentNullException.ThrowIfNull(renders);
        _hits = shareHits ? previous!._hits : hits;
        _renders = shareRenders ? previous!._renders : renders;
        (_visibleBackdropCount, _visiblePreRenderCount) = shareRenders
            ? (previous!._visibleBackdropCount, previous._visiblePreRenderCount)
            : CountBackdropPresence(renders);
        OffscreenSurface = offscreenSurface;
        OffscreenReservationGeneration = offscreenSurface?.ReservationGeneration ?? 0;
        OffscreenQuadPayload = offscreenQuadPayload;
        OffscreenQuadWorldMatrix = offscreenQuadWorldMatrix ?? Matrix4x4.Identity;
        _scrollReceivers = scrollReceivers ?? [];
        _focusScopes = focusScopes ?? [];
        ActiveFocusScopeIdentity = ResolveActiveFocusScope(identities, _focusScopes);
        if (shareRenders)
            _renderByMembership = previous!._renderByMembership;
        else
        {
            _renderByMembership = new Dictionary<long, int>(renders!.Length);
            for (int index = 0; index < renders.Length; index++)
                _renderByMembership.Add(renders[index].MembershipId, index);
        }
        try
        {
            _hitStore = shareHits
                ? RetainStore(previous!._hitStore!)
                : UIRecordStore<UIHitRecord>.Create(hits!);
            _renderStore = shareRenders
                ? RetainStore(previous!._renderStore!)
                : UIRecordStore<UIRenderRecord>.Create(renders,
                    RetainRenderRecordAction, ReleaseRenderRecordAction);
            _compositionSegments = previous is not null && previous.CanvasLifetime == canvasLifetime
                ? shareRenders
                    ? RetainCompositionSet(previous._compositionSegments)
                    : UICompositionSegmentSet.Build(_renderStore, previous._compositionSegments)
                : UICompositionSegmentSet.Build(_renderStore, null);
            RetainCanvasResources();
        }
        catch
        {
            RetireDrawPayloads();
            throw;
        }
        CanvasBounds = canvasBounds;
        AuthoringCanvasWorldMatrix = authoringCanvasWorldMatrix ?? Matrix4x4.Identity;
        AuthoringDrawSpace = authoringDrawSpace;
        AuthoringOffscreen = authoringOffscreen;
        CanvasToOutput = canvasToOutput;
        OutputToCanvas = outputToCanvas;
        DrawSpace = ECanvasDrawSpace.Screen;
        OutputRegion = canvasBounds;
        WorldToCanvas = Matrix4x4.Identity;
        ClipToWorld = Matrix4x4.Identity;
    }

    /// <summary>
    /// Creates the next owner-built publication from changed record pages. The caller
    /// keeps the identities, hit and render record counts, and render membership order
    /// of <paramref name="previous"/>. The new publication takes both store claims,
    /// also when construction fails.
    /// </summary>
    internal static UIPublication CreatePatched(UIPublication previous, ulong generation,
        ulong layoutGeneration, ulong hitGeneration, long changeRevision,
        UILayoutOutputPageView layoutPages, UIRecordStore<UIHitRecord> hitStore,
        UIRecordStore<UIRenderRecord> renderStore, ReadOnlySpan<int> changedRenderPages,
        int backdropDelta, int preRenderDelta, UIScrollRecord[] scrollReceivers,
        UIFocusScopeRecord[] focusScopes, UIRenderSurface? offscreenSurface,
        UIDrawPayload? offscreenQuadPayload, Matrix4x4 offscreenQuadWorldMatrix)
        => new(previous, generation, layoutGeneration, hitGeneration, changeRevision,
            layoutPages, hitStore, renderStore, changedRenderPages, backdropDelta,
            preRenderDelta, scrollReceivers, focusScopes, offscreenSurface,
            offscreenQuadPayload, offscreenQuadWorldMatrix);

    private UIPublication(UIPublication previous, ulong generation, ulong layoutGeneration,
        ulong hitGeneration, long changeRevision, UILayoutOutputPageView layoutPages,
        UIRecordStore<UIHitRecord> hitStore, UIRecordStore<UIRenderRecord> renderStore,
        ReadOnlySpan<int> changedRenderPages, int backdropDelta, int preRenderDelta,
        UIScrollRecord[] scrollReceivers, UIFocusScopeRecord[] focusScopes,
        UIRenderSurface? offscreenSurface, UIDrawPayload? offscreenQuadPayload,
        Matrix4x4 offscreenQuadWorldMatrix)
    {
        _hitStore = hitStore;
        _renderStore = renderStore;
        try
        {
            if (hitStore.Length != previous._hitStore!.Length ||
                renderStore.Length != previous._renderStore!.Length)
                throw new ArgumentException("A patched publication must keep record counts.");
            Generation = generation;
            RenderGeneration = generation;
            CanvasLifetime = previous.CanvasLifetime;
            LayoutGeneration = layoutGeneration;
            HitGeneration = hitGeneration;
            ChangeRevision = changeRevision;
            LayoutPages = layoutPages;
            Identities = previous.Identities;
            _hits = ReferenceEquals(hitStore, previous._hitStore) ? previous._hits : null;
            _renders = ReferenceEquals(renderStore, previous._renderStore) ? previous._renders : null;
            _visibleBackdropCount = previous._visibleBackdropCount + backdropDelta;
            _visiblePreRenderCount = previous._visiblePreRenderCount + preRenderDelta;
            if (_visibleBackdropCount < 0 || _visiblePreRenderCount < 0)
                throw new ArgumentException("A patched publication has invalid backdrop counts.");
            OffscreenSurface = offscreenSurface;
            OffscreenReservationGeneration = offscreenSurface?.ReservationGeneration ?? 0;
            OffscreenQuadPayload = offscreenQuadPayload;
            OffscreenQuadWorldMatrix = offscreenQuadWorldMatrix;
            _scrollReceivers = scrollReceivers;
            _focusScopes = focusScopes;
            ActiveFocusScopeIdentity = ResolveActiveFocusScope(Identities, focusScopes);
            // Membership order is unchanged, so the previous index lookup stays exact.
            _renderByMembership = previous._renderByMembership;
            _compositionSegments = previous.CanvasLifetime == CanvasLifetime &&
                previous._compositionSegments.TryWithReplacedRenderPages(renderStore,
                    changedRenderPages, out UICompositionSegmentSet patched)
                ? patched
                : UICompositionSegmentSet.Build(renderStore, previous._compositionSegments);
            RetainCanvasResources();
        }
        catch
        {
            RetireDrawPayloads();
            throw;
        }
        CanvasBounds = previous.CanvasBounds;
        AuthoringCanvasWorldMatrix = previous.AuthoringCanvasWorldMatrix;
        AuthoringDrawSpace = previous.AuthoringDrawSpace;
        AuthoringOffscreen = previous.AuthoringOffscreen;
        CanvasToOutput = Matrix4x4.Identity;
        OutputToCanvas = Matrix4x4.Identity;
        DrawSpace = ECanvasDrawSpace.Screen;
        OutputRegion = previous.CanvasBounds;
        WorldToCanvas = Matrix4x4.Identity;
        ClipToWorld = Matrix4x4.Identity;
    }

    private static UIElementIdentity ResolveActiveFocusScope(
        ReadOnlyMemory<UIElementIdentity> identities, UIFocusScopeRecord[] focusScopes)
    {
        UIElementIdentity activeScope = identities.Length > 0 ? identities.Span[0] : default;
        int painterOrder = int.MinValue;
        for (int index = 0; index < focusScopes.Length; index++)
            if (focusScopes[index].PainterOrder >= painterOrder)
            {
                activeScope = focusScopes[index].Identity;
                painterOrder = focusScopes[index].PainterOrder;
            }
        return activeScope;
    }

    public ulong Generation { get; }
    /// <summary>Identifies the immutable render-record set independently of shared hit/layout data.</summary>
    public ulong RenderGeneration { get; }
    public ulong CanvasLifetime { get; }
    public ulong LayoutGeneration { get; }
    public ulong HitGeneration { get; }
    public long ChangeRevision { get; }
    internal long WorldCollectionRevision { get; private set; }

    internal void SetWorldCollectionRevision(long revision)
    {
        if (_worldCommands is not null || revision <= 0)
            throw new InvalidOperationException("The world collection revision must precede frozen commands.");
        WorldCollectionRevision = revision;
    }
    public ReadOnlyMemory<UILayoutNodeOutput> Layout
        => _flatLayout ??= LayoutPages?.ToArray() ?? [];
    public UILayoutOutputPageView? LayoutPages { get; }
    public ReadOnlyMemory<UIElementIdentity> Identities { get; }
    /// <summary>Gets a flat compatibility copy of the hit records. Prefer the paged readers.</summary>
    public ReadOnlySpan<UIHitRecord> Hits => _hits ?? MaterializeFlat(ref _hits, _hitStore!);
    public int HitRecordCount => _hitStore!.Length;
    public int HitRecordPageCount => _hitStore!.PageCount;
    public ReadOnlySpan<UIHitRecord> GetHitRecordPage(int pageIndex)
        => _hitStore!.GetPage(pageIndex);
    public UIHitRecord GetHitRecord(int index) => _hitStore![index];
    /// <summary>Gets a flat compatibility copy of the render records. Prefer the paged readers.</summary>
    public ReadOnlySpan<UIRenderRecord> Renders => _renders ?? MaterializeFlat(ref _renders, _renderStore!);
    internal bool HasVisibleBackdrop => _visibleBackdropCount > 0;
    internal bool HasVisiblePreRenderDraw => _visiblePreRenderCount > 0;
    public ReadOnlySpan<UICompositionSegment> CompositionSegments => _compositionSegments.Segments;
    internal UICompositionSegmentSet CompositionStorage => _compositionSegments;
    internal bool TryGetCompositionSegment(long membershipId, out UICompositionSegment segment)
        => _compositionSegments.TryGet(membershipId, out segment);
    public ReadOnlySpan<UIScrollRecord> ScrollReceivers => _scrollReceivers;
    public ReadOnlySpan<UIFocusScopeRecord> FocusScopes => _focusScopes;
    public UIElementIdentity ActiveFocusScopeIdentity { get; }
    internal UIRecordStore<UIHitRecord> HitRecordStore => _hitStore!;
    internal UIRecordStore<UIRenderRecord> RenderRecordStore => _renderStore!;
    internal bool TryGetRenderRecordIndex(long membershipId, out int index)
        => _renderByMembership.TryGetValue(membershipId, out index);
    internal int RenderRecordCount => _renderStore!.Length;
    internal ReadOnlySpan<UIRenderRecord> GetRenderRecordPage(int pageIndex)
        => _renderStore!.GetPage(pageIndex);
    internal UIScrollRecord[] ScrollStorage => _scrollReceivers;
    internal UIFocusScopeRecord[] FocusScopeStorage => _focusScopes;
    internal ReadOnlySpan<UIPublishedWorldMeshCommand> WorldCommands
        => _worldCommands ?? [];
    internal UIRenderSurface? OffscreenSurface { get; }
    internal ulong OffscreenReservationGeneration { get; }
    internal UIDrawPayload? OffscreenQuadPayload { get; }
    internal Matrix4x4 OffscreenQuadWorldMatrix { get; }

    internal void BuildWorldCommands(XREngine.Components.UICanvasComponent canvas)
    {
        if (AuthoringDrawSpace == ECanvasDrawSpace.Screen)
            return;
        lock (_worldCommandGate)
        {
            if (_worldCommands is not null)
                return;
            List<UIPublishedWorldMeshCommand> commands = [];
            if (AuthoringOffscreen)
            {
                UIDrawPayload payload = OffscreenQuadPayload ??
                    throw new InvalidOperationException("An offscreen publication has no owned quad payload.");
                if (OffscreenSurface is null || OffscreenReservationGeneration == 0)
                    throw new InvalidOperationException("An offscreen publication has no reserved surface.");
                RenderCommandMesh3D quad = new(EDefaultRenderPass.TransparentForward)
                {
                    Mesh = payload.Renderer,
                    MaterialOverride = payload.Material,
                    RenderOptionsOverride = payload.RenderOptions,
                    WorldMatrix = OffscreenQuadWorldMatrix,
                    Instances = 1,
                };
                commands.Add(new UIPublishedWorldMeshCommand(canvas, this, quad,
                    castsShadows: false, layer: 0, cullingVolume: null));
                _worldCommands = commands.ToArray();
                return;
            }
            UIRecordStore<UIRenderRecord> renders = _renderStore!;
            for (int index = 0; index < renders.Length; index++)
            {
                ref readonly UIRenderRecord record = ref renders[index];
                if (!record.Visible)
                    continue;
                RenderCommandMesh3D template = record.FrozenWorldCommand ??
                    throw new InvalidOperationException("A visible direct-world UI draw has no frozen command.");
                commands.Add(new UIPublishedWorldMeshCommand(canvas, this, template,
                    record.WorldCastsShadows, record.WorldLayer, record.WorldCullingVolume,
                    record.WorldRenderInfo, record.SourceWorldCommand,
                    record.WorldRegistrationIncarnation, record.Crop,
                    record.Payload?.WorldCrop,
                    record.Payload?.WorldCropFragmentShader,
                    record.Payload?.WorldCropVertexShader));
            }
            _worldCommands = commands.ToArray();
        }
    }
    internal bool ResourcesRetired => Volatile.Read(ref _resourcesRetired) != 0;
    public BoundingRectangleF CanvasBounds { get; }
    /// <summary>The owner-captured canvas pose used by world draw authoring.</summary>
    public Matrix4x4 AuthoringCanvasWorldMatrix { get; }
    public ECanvasDrawSpace AuthoringDrawSpace { get; }
    public bool AuthoringOffscreen { get; }
    public Matrix4x4 CanvasToOutput { get; }
    public Matrix4x4 OutputToCanvas { get; }
    public ECanvasDrawSpace DrawSpace { get; }
    public BoundingRectangleF OutputRegion { get; }
    public Matrix4x4 WorldToCanvas { get; }
    public Matrix4x4 ClipToWorld { get; }
    public float NearClipZ { get; }
    public float FarClipZ { get; }

    public bool TryGetRenderRecord(long membershipId, out UIRenderRecord record)
    {
        if (_renderByMembership.TryGetValue(membershipId, out int index))
        {
            record = _renderStore![index];
            return true;
        }
        record = default;
        return false;
    }

    internal UIPublication WithOutputMapping(Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        ECanvasDrawSpace drawSpace, BoundingRectangleF outputRegion, Matrix4x4 worldToCanvas,
        Matrix4x4 clipToWorld, float nearClipZ, float farClipZ)
        => new(this, canvasToOutput, outputToCanvas, drawSpace, outputRegion,
            worldToCanvas, clipToWorld, nearClipZ, farClipZ);

    /// <summary>
    /// Creates an output-specific visual packet while sharing the base hit and
    /// layout generation. The caller supplies a complete frozen record set.
    /// </summary>
    internal UIPublication WithOutputVariant(UIRenderRecord[] outputRenders,
        ulong renderGeneration, Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        BoundingRectangleF outputRegion)
    {
        ArgumentNullException.ThrowIfNull(outputRenders);
        if (AuthoringDrawSpace != ECanvasDrawSpace.Screen || renderGeneration == 0)
            throw new InvalidOperationException("Output visual variants require a screen canvas and a generation.");
        return new UIPublication(this, canvasToOutput, outputToCanvas,
            ECanvasDrawSpace.Screen, outputRegion, Matrix4x4.Identity,
            Matrix4x4.Identity, 0, 1, outputRenders, renderGeneration);
    }

    private UIPublication(UIPublication source, Matrix4x4 canvasToOutput, Matrix4x4 outputToCanvas,
        ECanvasDrawSpace drawSpace, BoundingRectangleF outputRegion, Matrix4x4 worldToCanvas,
        Matrix4x4 clipToWorld, float nearClipZ, float farClipZ,
        UIRenderRecord[]? outputRenders = null, ulong? renderGeneration = null)
    {
        Generation = source.Generation;
        RenderGeneration = renderGeneration ?? source.RenderGeneration;
        CanvasLifetime = source.CanvasLifetime;
        LayoutGeneration = source.LayoutGeneration;
        HitGeneration = source.HitGeneration;
        ChangeRevision = source.ChangeRevision;
        WorldCollectionRevision = source.WorldCollectionRevision;
        _flatLayout = source._flatLayout;
        LayoutPages = source.LayoutPages;
        Identities = source.Identities;
        _hits = source._hits;
        bool shareRenders = outputRenders is null || ReferenceEquals(outputRenders, source._renders);
        _renders = shareRenders ? source._renders : outputRenders;
        (_visibleBackdropCount, _visiblePreRenderCount) = shareRenders
            ? (source._visibleBackdropCount, source._visiblePreRenderCount)
            : CountBackdropPresence(outputRenders);
        OffscreenSurface = source.OffscreenSurface;
        OffscreenReservationGeneration = source.OffscreenReservationGeneration;
        OffscreenQuadPayload = source.OffscreenQuadPayload;
        OffscreenQuadWorldMatrix = source.OffscreenQuadWorldMatrix;
        _scrollReceivers = source._scrollReceivers;
        _focusScopes = source._focusScopes;
        ActiveFocusScopeIdentity = source.ActiveFocusScopeIdentity;
        if (shareRenders)
            _renderByMembership = source._renderByMembership;
        else
        {
            _renderByMembership = new Dictionary<long, int>(outputRenders!.Length);
            for (int index = 0; index < outputRenders.Length; index++)
                _renderByMembership.Add(outputRenders[index].MembershipId, index);
        }
        try
        {
            _hitStore = RetainStore(source._hitStore!);
            _renderStore = shareRenders
                ? RetainStore(source._renderStore!)
                : UIRecordStore<UIRenderRecord>.Create(outputRenders,
                    RetainRenderRecordAction, ReleaseRenderRecordAction);
            _compositionSegments = shareRenders
                ? RetainCompositionSet(source._compositionSegments)
                : UICompositionSegmentSet.Build(_renderStore, source._compositionSegments);
            RetainCanvasResources();
        }
        catch
        {
            RetireDrawPayloads();
            throw;
        }
        CanvasBounds = source.CanvasBounds;
        AuthoringCanvasWorldMatrix = source.AuthoringCanvasWorldMatrix;
        AuthoringDrawSpace = source.AuthoringDrawSpace;
        AuthoringOffscreen = source.AuthoringOffscreen;
        CanvasToOutput = canvasToOutput;
        OutputToCanvas = outputToCanvas;
        DrawSpace = drawSpace;
        OutputRegion = outputRegion;
        WorldToCanvas = worldToCanvas;
        ClipToWorld = clipToWorld;
        NearClipZ = nearClipZ;
        FarClipZ = farClipZ;
    }

    private static (int Backdrop, int PreRender) CountBackdropPresence(
        ReadOnlySpan<UIRenderRecord> records)
    {
        int backdrop = 0;
        int preRender = 0;
        foreach (ref readonly UIRenderRecord record in records)
        {
            backdrop += BackdropContribution(record);
            preRender += PreRenderContribution(record);
        }
        return (backdrop, preRender);
    }

    private static int BackdropContribution(in UIRenderRecord record)
        => record.Visible && record.Backdrop.IsDefined ? 1 : 0;

    private static int PreRenderContribution(in UIRenderRecord record)
        => record.Visible && record.RenderPass == (int)EDefaultRenderPass.PreRender ? 1 : 0;

    private static T[] MaterializeFlat<T>(ref T[]? cache, UIRecordStore<T> store)
        where T : struct
    {
        T[] flat = new T[store.Length];
        store.CopyTo(flat);
        UIRuntimeCounters.Add(UIWorkCounter.PublicationFlatCellsMaterialized, flat.Length);
        return Interlocked.CompareExchange(ref cache, flat, null) ?? flat;
    }

    private static UIRecordStore<T> RetainStore<T>(UIRecordStore<T> store) where T : struct
    {
        store.Retain();
        return store;
    }

    private static UICompositionSegmentSet RetainCompositionSet(UICompositionSegmentSet set)
    {
        set.Retain();
        return set;
    }

    private static void RetainRenderRecord(UIRenderRecord record)
    {
        bool payloadRetained = false;
        bool textureRetained = false;
        try
        {
            if (record.Payload is { } payload)
            {
                payload.Retain();
                payloadRetained = true;
            }
            if (record.TextureSnapshot is { } texture)
            {
                texture.Retain();
                textureRetained = true;
            }
            record.AtlasSnapshot?.Retain();
        }
        catch (Exception failure)
        {
            List<Exception>? cleanupFailures = null;
            if (textureRetained)
                try { record.TextureSnapshot!.Release(); }
                catch (Exception cleanupFailure) { (cleanupFailures ??= []).Add(cleanupFailure); }
            if (payloadRetained)
                try { record.Payload!.Release(); }
                catch (Exception cleanupFailure) { (cleanupFailures ??= []).Add(cleanupFailure); }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, failure);
                throw new AggregateException("UI render record retain and cleanup failed.", cleanupFailures);
            }
            throw;
        }
    }

    private static void ReleaseRenderRecord(UIRenderRecord record)
    {
        List<Exception>? failures = null;
        try { record.Payload?.Release(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { record.TextureSnapshot?.Release(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { record.AtlasSnapshot?.Release(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures is not null)
            throw new AggregateException("UI render record cleanup failed.", failures);
    }

    private void RetainCanvasResources()
    {
        if (OffscreenSurface is not null)
        {
            OffscreenSurface.Retain();
            _retainedSurface = true;
        }
        if (OffscreenQuadPayload is not null)
        {
            OffscreenQuadPayload.Retain();
            _retainedQuadPayload = true;
        }
    }

    /// <summary>Keep draw resources while a holder or reader uses this packet.</summary>
    public void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _claims, checked(current + 1), current) != current);
    }

    /// <summary>End one packet claim after its reader or GPU artifact retires.</summary>
    public void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            if (current == 0)
                throw new InvalidOperationException("A UI publication claim was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;
        RetireDrawPayloads();
        GC.SuppressFinalize(this);
    }

    ~UIPublication() => RetireDrawPayloads();

    private void RetireDrawPayloads()
    {
        if (Interlocked.Exchange(ref _resourcesRetired, 1) != 0)
            return;
        try { _compositionSegments.Release(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI publication composition cleanup failed: {0}", failure); }
            catch { }
        }
        try { _renderStore?.Release(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI publication record cleanup failed: {0}", failure); }
            catch { }
        }
        try { _hitStore?.Release(); }
        catch (Exception failure)
        {
            try { System.Diagnostics.Trace.TraceError("UI publication hit cleanup failed: {0}", failure); }
            catch { }
        }
        if (_retainedSurface)
        {
            try { OffscreenSurface?.Release(); }
            catch (Exception)
            {
                // The surface quarantines its native owners on terminal failure.
            }
        }
        if (_retainedQuadPayload)
        {
            try { OffscreenQuadPayload?.Release(); }
            catch (Exception)
            {
                // Owned payload cleanup keeps failed native resources quarantined.
            }
        }
    }

    /// <summary>Maps a raw top-left window pointer to the displayed canvas plane.</summary>
    public bool TryMapPointer(Vector2 raw, out Vector2 canvasPoint, bool requireInsideOutput = true)
    {
        canvasPoint = default;
        if (!float.IsFinite(raw.X) || !float.IsFinite(raw.Y))
            return false;
        if (OutputRegion.Width <= 0 || OutputRegion.Height <= 0 ||
            requireInsideOutput &&
            (raw.X < OutputRegion.MinX || raw.X >= OutputRegion.MinX + OutputRegion.Width ||
             raw.Y < OutputRegion.MinY || raw.Y >= OutputRegion.MinY + OutputRegion.Height))
            return false;
        if (DrawSpace == ECanvasDrawSpace.Screen)
        {
            canvasPoint = Vector2.Transform(raw, OutputToCanvas);
            return float.IsFinite(canvasPoint.X) && float.IsFinite(canvasPoint.Y);
        }
        float x = 2 * (raw.X - OutputRegion.MinX) / OutputRegion.Width - 1;
        float y = 1 - 2 * (raw.Y - OutputRegion.MinY) / OutputRegion.Height;
        Vector4 near = Vector4.Transform(new Vector4(x, y, NearClipZ, 1), ClipToWorld);
        Vector4 far = Vector4.Transform(new Vector4(x, y, FarClipZ, 1), ClipToWorld);
        if (!float.IsFinite(near.W) || !float.IsFinite(far.W) || near.W == 0 || far.W == 0)
            return false;
        Vector3 nearCanvas = Vector3.Transform(new Vector3(near.X, near.Y, near.Z) / near.W,
            WorldToCanvas);
        Vector3 farCanvas = Vector3.Transform(new Vector3(far.X, far.Y, far.Z) / far.W,
            WorldToCanvas);
        float deltaZ = farCanvas.Z - nearCanvas.Z;
        if (!float.IsFinite(deltaZ) || MathF.Abs(deltaZ) < 1e-7f)
            return false;
        float t = -nearCanvas.Z / deltaZ;
        if (!float.IsFinite(t) || t < 0 || t > 1)
            return false;
        Vector3 hit = Vector3.Lerp(nearCanvas, farCanvas, t);
        canvasPoint = new Vector2(hit.X, hit.Y);
        return float.IsFinite(canvasPoint.X) && float.IsFinite(canvasPoint.Y);
    }
}
