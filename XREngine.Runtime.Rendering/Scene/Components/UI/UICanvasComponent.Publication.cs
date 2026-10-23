using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    [ThreadStatic]
    private static UICanvasComponent? t_renderingCanvas;
    internal static UICanvasComponent? RenderingCanvasForCurrentThread => t_renderingCanvas;
    private readonly List<UIHitRecord> _publicationHitScratch = [];
    private readonly List<UIScrollRecord> _publicationScrollScratch = [];
    private readonly List<UIFocusScopeRecord> _publicationFocusScopeScratch = [];
    private readonly List<UIRenderRecord> _publicationRenderScratch = [];
    private readonly List<UIRenderRecord> _publicationOrderedRenderScratch = [];
    private readonly List<long> _publicationPainterOrder = [];
    private readonly Dictionary<long, UIRenderRecord> _publicationRenderByMembership = [];
    private ulong _publicationPainterOrderRevision = ulong.MaxValue;
    private readonly Dictionary<UIElementIdentity, int> _publicationLayoutIndices = [];
    private readonly Dictionary<UIElementIdentity, int> _publicationSubtreeEnds = [];
    private readonly ConcurrentDictionary<object, UIPublication> _presentedPublications =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<object, (ulong FrameId, UIPublication Publication, ulong ReceiptId)> _renderedPublications =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, (UIPublication Source, Matrix4x4 CanvasToOutput,
        Matrix4x4 WorldToCanvas, Matrix4x4 ClipToWorld, ECanvasDrawSpace DrawSpace,
        float NearClipZ, float FarClipZ, UIPublication Mapped)>
        _outputMappingCache = new(ReferenceEqualityComparer.Instance);
    private readonly object _publicationGate = new();
    private UIPublication? _candidatePublication;
    private UIPublication? _collectingPublication;
    private UIPublication? _currentPublication;
    private Action<UIRenderRecord>? _collectPublishedUnbatched;
    private string? _lastPublicationCaptureFailure;
    private ulong _nextPublicationGeneration;
    private ulong _nextHitGeneration;
    private long _canvasLifetime = 1;

    /// <summary>Gets the current canvas incarnation for deferred input requests.</summary>
    public ulong PublicationLifetime => (ulong)Volatile.Read(ref _canvasLifetime);

    /// <summary>Acquires the packet used by a render attempt. The caller must release it in a finally block.</summary>
    private UIPublication? AcquireCurrentPublication()
    {
        lock (_publicationGate)
        {
            UIPublication? publication = _currentPublication;
            publication?.Retain();
            return publication;
        }
    }

    /// <summary>Acquires the last packet accepted by an output. The caller must release it in a finally block.</summary>
    public bool TryAcquirePresentedPublication(object output, out UIPublication? publication)
    {
        lock (_publicationGate)
        {
            if (_presentedPublications.TryGetValue(output, out publication))
            {
                publication.Retain();
                return true;
            }
        }
        publication = null;
        return false;
    }

    /// <summary>Removes an output latch when an accepted image no longer contains this canvas.</summary>
    public void ClearPresentedPublication(object output)
    {
        lock (_publicationGate)
            if (_presentedPublications.TryRemove(output, out UIPublication? old))
                old.Release();
    }

    /// <summary>Releases all packet references for a detached output.</summary>
    public void RemovePublicationOutput(object output)
    {
        lock (_publicationGate)
        {
            if (_presentedPublications.TryRemove(output, out UIPublication? presented))
                presented.Release();
            if (_renderedPublications.TryRemove(output, out var rendered))
                rendered.Publication.Release();
            if (_outputMappingCache.Remove(output, out var mapping))
            {
                mapping.Source.Release();
                mapping.Mapped.Release();
            }
        }
    }

    /// <summary>Gets draw values from the packet bound to the current collection.</summary>
    public bool TryGetCollectingRenderRecord(long membershipId, out UIRenderRecord record)
    {
        if (_collectingPublication is { } publication)
            return publication.TryGetRenderRecord(membershipId, out record);
        record = default;
        return false;
    }

    /// <summary>Acquires the exact rendered packet and receipt for one output frame. The caller must release the packet in a finally block.</summary>
    public bool TryAcquireRenderedPublication(object output, ulong frameId,
        out UIPublication? publication, out ulong receiptId)
    {
        lock (_publicationGate)
        {
            if (_renderedPublications.TryGetValue(output, out var receipt) &&
                receipt.FrameId == frameId)
            {
                receipt.Publication.Retain();
                publication = receipt.Publication;
                receiptId = receipt.ReceiptId;
                return true;
            }
        }
        publication = null;
        receiptId = 0;
        return false;
    }

    /// <summary>Records an output completion for the exact packet used by that output.</summary>
    public void MarkPresented(object output, UIPublication publication)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(publication);
        lock (_publicationGate)
        {
            if (!IsActive || publication.CanvasLifetime != (ulong)_canvasLifetime)
                return;
            publication.Retain();
            _presentedPublications.TryGetValue(output, out UIPublication? old);
            _presentedPublications[output] = publication;
            old?.Release();
        }
    }

    private void CapturePublicationCandidate()
    {
        long lifetime = Volatile.Read(ref _canvasLifetime);
        UICanvasTransform canvas = CanvasTransform;
        ulong layoutGeneration = canvas.CurrentLayoutGeneration;
        if (layoutGeneration == 0 || canvas.IsLayoutInvalidated || canvas.IsUpdatingLayout)
            return;

        long revision = canvas.ChangeJournal.Revision;
        Matrix4x4 canvasWorldMatrix = canvas.WorldMatrix;
        ECanvasDrawSpace authoringDrawSpace = canvas.DrawSpace;
        bool authoringOffscreen = UseOffscreenRenderingForNonScreenSpaces();
        BoundingRectangleF canvasBounds = canvas.GetActualBounds();
        bool painterOrderChanged = VisualScene2D.TryCopyRenderableMembershipOrder(
            ref _publicationPainterOrderRevision, _publicationPainterOrder);
        UIPublication? previous;
        lock (_publicationGate)
        {
            previous = _candidatePublication ?? _currentPublication;
            previous?.Retain();
        }
        try
        {
        if (previous is not null && previous.LayoutGeneration == layoutGeneration &&
            previous.ChangeRevision == revision && !painterOrderChanged &&
            previous.AuthoringCanvasWorldMatrix == canvasWorldMatrix &&
            previous.AuthoringDrawSpace == authoringDrawSpace &&
            previous.AuthoringOffscreen == authoringOffscreen &&
            previous.CanvasBounds == canvasBounds)
            return;

        ReadOnlyMemory<UIElementIdentity> identities = canvas.CurrentLayoutIdentities;
        UILayoutOutputPageView? layoutPages = canvas.CurrentLayoutOutputPages;
        ReadOnlyMemory<UILayoutNodeOutput> legacyLayout = layoutPages is null
            ? canvas.CurrentLayoutOutputs : default;
        if (identities.Length != (layoutPages?.Length ?? legacyLayout.Length))
            return;
        layoutPages ??= UILayoutOutputPageView.CopyFrom(legacyLayout.Span);

        _publicationLayoutIndices.Clear();
        for (int index = 0; index < identities.Length; index++)
            _publicationLayoutIndices.Add(identities.Span[index], index);
        _publicationSubtreeEnds.Clear();
        CaptureSubtreeEnds(canvas);
        _publicationHitScratch.Clear();
        _publicationScrollScratch.Clear();
        _publicationFocusScopeScratch.Clear();
        _publicationRenderScratch.Clear();
        CaptureHitRecords(canvas, canvas, canvas.ElementIdentity, layoutPages,
            default, default, default);
        OrderPublicationRenders();
        OrderPublicationFocusScopes();
        for (int index = 0; index < _publicationRenderScratch.Count; index++)
        {
            UIRenderRecord record = _publicationRenderScratch[index];
            bool directWorld = authoringDrawSpace != ECanvasDrawSpace.Screen && !authoringOffscreen;
            if (!record.Visible || record.CaptureFailure is null &&
                (directWorld ? record.FrozenWorldCommand is not null :
                    record.SupportsBatch || record.FrozenCommand is not null))
                continue;
            string failure = record.CaptureFailure ??
                $"UI.DrawCapture.MissingCommand: render member {record.MembershipId} has no frozen {(directWorld ? "world" : "canvas")} draw command.";
            if (_lastPublicationCaptureFailure != failure)
            {
                _lastPublicationCaptureFailure = failure;
                Debug.LogWarning(failure);
            }
            return;
        }
        bool hitsChanged = previous is null ||
            !CollectionsMarshal.AsSpan(_publicationHitScratch).SequenceEqual(previous.Hits);
        bool scrollChanged = previous is null ||
            !CollectionsMarshal.AsSpan(_publicationScrollScratch).SequenceEqual(previous.ScrollReceivers);
        bool scopesChanged = previous is null ||
            !CollectionsMarshal.AsSpan(_publicationFocusScopeScratch).SequenceEqual(previous.FocusScopes);
        bool rendersChanged = previous is null ||
            !CollectionsMarshal.AsSpan(_publicationRenderScratch).SequenceEqual(previous.Renders);
        UIHitRecord[] hits = hitsChanged
            ? _publicationHitScratch.ToArray() : previous!.HitStorage;
        UIScrollRecord[] scroll = scrollChanged
            ? _publicationScrollScratch.ToArray() : previous!.ScrollStorage;
        UIFocusScopeRecord[] scopes = scopesChanged
            ? _publicationFocusScopeScratch.ToArray() : previous!.FocusScopeStorage;
        UIRenderRecord[] renders = rendersChanged
            ? _publicationRenderScratch.ToArray() : previous!.RenderStorage;
        ulong hitGeneration = previous?.HitGeneration ?? ++_nextHitGeneration;
        if (previous is not null && hitsChanged)
            hitGeneration = ++_nextHitGeneration;
        _lastPublicationCaptureFailure = null;
        lock (_publicationGate)
        {
            // A capture adapter can mutate the tree while it copies draw values.
            // Admit only the exact layout, journal, and painter census we read.
            ulong painterRevision = _publicationPainterOrderRevision;
            if (lifetime != _canvasLifetime ||
                !ReferenceEquals(canvas, CanvasTransform) ||
                canvas.CurrentLayoutGeneration != layoutGeneration ||
                canvas.ChangeJournal.Revision != revision ||
                canvas.WorldMatrix != canvasWorldMatrix ||
                canvas.DrawSpace != authoringDrawSpace ||
                UseOffscreenRenderingForNonScreenSpaces() != authoringOffscreen ||
                canvas.GetActualBounds() != canvasBounds ||
                canvas.IsLayoutInvalidated || canvas.IsUpdatingLayout ||
                VisualScene2D.TryCopyRenderableMembershipOrder(
                    ref painterRevision, _publicationPainterOrder))
                return;
            UIPublication replacement = new UIPublication(
                ++_nextPublicationGeneration, (ulong)lifetime, layoutGeneration, hitGeneration,
                revision, default, identities, hits, renders, canvasBounds,
                Matrix4x4.Identity, Matrix4x4.Identity, previous, scroll, scopes,
                layoutPages, canvasWorldMatrix, authoringDrawSpace, authoringOffscreen);
            try { replacement.BuildWorldCommands(this); }
            catch
            {
                replacement.Release();
                throw;
            }
            UIPublication? oldCandidate = _candidatePublication;
            _candidatePublication = replacement;
            oldCandidate?.Release();
        }
        }
        finally { previous?.Release(); }
    }

    private int CaptureSubtreeEnds(UITransform transform)
    {
        if (!_publicationLayoutIndices.TryGetValue(transform.ElementIdentity, out int index))
            return 0;
        int end = index + 1;
        for (int childIndex = 0; childIndex < transform.Children.Count; childIndex++)
            if (transform.Children[childIndex] is UITransform child)
                end = Math.Max(end, CaptureSubtreeEnds(child));
        _publicationSubtreeEnds[transform.ElementIdentity] = end;
        return end;
    }

    private void OrderPublicationRenders()
    {
        _publicationRenderByMembership.Clear();
        for (int index = 0; index < _publicationRenderScratch.Count; index++)
        {
            UIRenderRecord record = _publicationRenderScratch[index];
            _publicationRenderByMembership[record.MembershipId] = record;
        }
        _publicationOrderedRenderScratch.Clear();
        for (int index = 0; index < _publicationPainterOrder.Count; index++)
            if (_publicationRenderByMembership.TryGetValue(
                _publicationPainterOrder[index], out UIRenderRecord record))
                _publicationOrderedRenderScratch.Add(record);
        _publicationRenderScratch.Clear();
        _publicationRenderScratch.AddRange(_publicationOrderedRenderScratch);
    }

    private void OrderPublicationFocusScopes()
    {
        for (int scopeIndex = 0; scopeIndex < _publicationFocusScopeScratch.Count; scopeIndex++)
        {
            UIFocusScopeRecord scope = _publicationFocusScopeScratch[scopeIndex];
            int painterOrder = -1;
            for (int renderIndex = 0; renderIndex < _publicationRenderScratch.Count; renderIndex++)
                if (_publicationLayoutIndices.TryGetValue(
                        _publicationRenderScratch[renderIndex].Identity, out int nodeIndex) &&
                    scope.Contains(nodeIndex))
                    painterOrder = renderIndex;
            _publicationFocusScopeScratch[scopeIndex] = scope with { PainterOrder = painterOrder };
        }
    }

    private void CaptureHitRecords(UICanvasTransform canvas, UITransform transform,
        UIElementIdentity focusScopeIdentity, UILayoutOutputPageView layoutPages,
        UIElementIdentity virtualListIdentity, UIElementIdentity virtualRowIdentity,
        UIVirtualRowLease virtualRowLease)
    {
        if (!_publicationLayoutIndices.TryGetValue(transform.ElementIdentity, out int layoutIndex) ||
            !layoutPages[layoutIndex].Visible)
            return;

        if (transform is UIBoundableTransform row && transform.Parent is UIListTransform list &&
            list.TryGetVirtualRowLease(row, out UIVirtualRowLease rowLease))
        {
            virtualListIdentity = list.ElementIdentity;
            virtualRowIdentity = row.ElementIdentity;
            virtualRowLease = rowLease;
        }

        if (transform.SceneNode?.Components is { } nodeComponents)
        {
            for (int index = 0; index < nodeComponents.Count; index++)
            {
                if (nodeComponents[index] is not { IsActiveInHierarchy: true } owner)
                    continue;
                if (owner is UIContextMenuComponent { IsOpen: true } &&
                    transform is UIBoundableTransform { BlocksInputBehind: true })
                {
                    focusScopeIdentity = transform.ElementIdentity;
                    _publicationFocusScopeScratch.Add(new UIFocusScopeRecord(
                        focusScopeIdentity, layoutIndex,
                        _publicationSubtreeEnds[focusScopeIdentity], transform.Depth, -1));
                }
                if (owner is IUIScrollReceiver receiver)
                    _publicationScrollScratch.Add(new UIScrollRecord(owner, receiver,
                        transform.ElementIdentity, layoutIndex,
                        _publicationSubtreeEnds[transform.ElementIdentity], transform.Depth, index));
            }
        }

        if (transform is UIBoundableTransform bounds && bounds.ActualSize.X > 0 && bounds.ActualSize.Y > 0 &&
            transform.SceneNode?.Components is { } components)
        {
            for (int index = 0; index < components.Count; index++)
            {
                if (components[index] is not UIComponent { IsActiveInHierarchy: true } component)
                    continue;
                RenderInfo2D? renderInfo = component switch
                {
                    UIInteractableComponent interactable => interactable.RenderInfo2D,
                    UIRenderableComponent renderable => renderable.RenderInfo2D,
                    _ => null,
                };
                if (renderInfo is null || !renderInfo.IsVisible ||
                    component is not UIInteractableComponent && !bounds.BlocksInputBehind)
                {
                    if (component is UIRenderableComponent hiddenRenderable)
                        _publicationRenderScratch.Add(hiddenRenderable.CapturePublicationRenderRecord(
                            in layoutPages[layoutIndex]));
                    continue;
                }

                if (component is UIRenderableComponent renderableComponent)
                    _publicationRenderScratch.Add(renderableComponent.CapturePublicationRenderRecord(
                        in layoutPages[layoutIndex]));

                long interactionRevision = component is UIInteractableComponent target
                    ? target.InteractionRevision : 0;
                _publicationHitScratch.Add(new UIHitRecord(
                    renderInfo, renderInfo.MembershipId, transform.ElementIdentity,
                    interactionRevision, canvas.WorldMatrix * transform.InverseWorldMatrix,
                    bounds.ActualSize, bounds.GetCanvasRegion(canvas),
                    UIClipRegion.ResolveCrop(bounds, false), layoutIndex,
                    transform.Parent is UITransform parent &&
                    _publicationLayoutIndices.TryGetValue(parent.ElementIdentity, out int parentIndex)
                        ? parentIndex : -1,
                    _publicationSubtreeEnds[transform.ElementIdentity], transform.Depth,
                    renderInfo.LayerIndex, renderInfo.IndexWithinLayer,
                    component is UIInteractableComponent, bounds.BlocksInputBehind,
                    component is UIInteractableComponent focusTarget && focusTarget.IsTabStop,
                    component is UIInteractableComponent indexedTarget ? indexedTarget.TabIndex : 0,
                    focusScopeIdentity, virtualListIdentity, virtualRowIdentity,
                    virtualRowLease));
            }
        }

        for (int index = 0; index < transform.Children.Count; index++)
            if (transform.Children[index] is UITransform child)
                CaptureHitRecords(canvas, child, focusScopeIdentity, layoutPages,
                    virtualListIdentity, virtualRowIdentity, virtualRowLease);
    }

    private void BeginPublicationCollection()
    {
        if (_collectingPublication is not null)
        {
            _renderPipeline.MeshRenderCommands.DiscardUpdatingCommands();
            BatchCollector.DiscardCollectedCandidate();
        }
        lock (_publicationGate)
        {
            UIPublication? next = _candidatePublication ?? _currentPublication;
            next?.Retain();
            UIPublication? old = _collectingPublication;
            _collectingPublication = next;
            old?.Release();
        }
        BatchCollector.BeginPublicationCollection(_collectingPublication?.Generation ?? 0);
    }

    private void CollectPublishedCanvasDraws()
    {
        if (_collectingPublication is not { } publication)
            return;
        BatchCollector.CollectPublishedRuns(publication,
            _renderPipeline.MeshRenderCommands,
            _collectPublishedUnbatched ??= CollectPublishedUnbatched);
    }

    private void CollectPublishedUnbatched(UIRenderRecord record)
    {
        RenderCommandMesh2D command = record.FrozenCommand ??
            throw new InvalidOperationException(
                "A published unbatched UI draw is missing its owner-captured command.");
        if (!command.Enabled)
            return;
        command.OwnerRenderInfo = record.RenderInfo;
        command.CollectedForRender(Camera2D);
        _renderPipeline.MeshRenderCommands.AddCPU(command, Camera2D);
    }

    private bool CanCommitCollectedPublication()
        => _collectingPublication is { } publication &&
            BatchCollector.CollectedPublicationGeneration == publication.Generation &&
            publication.CanvasLifetime == (ulong)Volatile.Read(ref _canvasLifetime);

    private void CommitCollectedPublication()
    {
        lock (_publicationGate)
        {
            if (_collectingPublication is not { } candidate ||
                candidate.CanvasLifetime != (ulong)_canvasLifetime)
                return;
            UIPublication? oldCurrent = _currentPublication;
            Volatile.Write(ref _currentPublication, candidate);
            if (ReferenceEquals(_candidatePublication, candidate))
            {
                _candidatePublication = null;
                candidate.Release();
            }
            _collectingPublication = null;
            oldCurrent?.Release();
        }
    }

    private void RecordRenderedPublication(object output, UIPublication publication, ulong receiptId)
    {
        if (publication.CanvasLifetime != (ulong)Volatile.Read(ref _canvasLifetime))
            return;
        if (output is XRViewport viewport &&
            publication.CanvasBounds.Width > 0 && publication.CanvasBounds.Height > 0)
        {
            var region = viewport.Region;
            Matrix4x4 canvasToOutput =
                Matrix4x4.CreateTranslation(-publication.CanvasBounds.MinX,
                    -publication.CanvasBounds.MinY, 0) *
                Matrix4x4.CreateScale(region.Width / publication.CanvasBounds.Width,
                    -region.Height / publication.CanvasBounds.Height, 1) *
                Matrix4x4.CreateTranslation(region.MinX, region.MinY + region.Height, 0);
            if (Matrix4x4.Invert(canvasToOutput, out Matrix4x4 outputToCanvas))
            {
                ECanvasDrawSpace drawSpace = CanvasTransform.DrawSpace;
                Matrix4x4 worldToCanvas = Matrix4x4.Identity;
                Matrix4x4 clipToWorld = Matrix4x4.Identity;
                float nearClipZ = 0;
                float farClipZ = 1;
                if (drawSpace != ECanvasDrawSpace.Screen)
                {
                    if (!TryGetWorldToCanvasMatrix(out worldToCanvas))
                        return;
                    XRCamera? camera = drawSpace == ECanvasDrawSpace.Camera
                        ? CanvasTransform.CameraSpaceCamera ?? viewport.ActiveCamera
                        : viewport.ActiveCamera;
                    if (camera?.Transform is null)
                        return;
                    clipToWorld = camera.InverseProjectionMatrix * camera.Transform.RenderMatrix;
                    nearClipZ = RenderClipSpacePolicy.DepthBufferToClipZ(
                        camera.GetNearDepthValue(), RuntimeEngine.Rendering.EffectiveClipDepthRange);
                    farClipZ = RenderClipSpacePolicy.DepthBufferToClipZ(
                        camera.GetFarDepthValue(), RuntimeEngine.Rendering.EffectiveClipDepthRange);
                }
                lock (_publicationGate)
                {
                    if (_outputMappingCache.TryGetValue(output, out var cache) &&
                        ReferenceEquals(cache.Source, publication) &&
                        cache.CanvasToOutput == canvasToOutput &&
                        cache.WorldToCanvas == worldToCanvas &&
                        cache.ClipToWorld == clipToWorld &&
                        cache.DrawSpace == drawSpace &&
                        cache.NearClipZ == nearClipZ && cache.FarClipZ == farClipZ)
                        publication = cache.Mapped;
                    else
                    {
                        UIPublication source = publication;
                        publication = source.WithOutputMapping(canvasToOutput, outputToCanvas,
                            drawSpace,
                            new BoundingRectangleF(new Vector2(region.MinX, region.MinY),
                                new Vector2(region.Width, region.Height)),
                            worldToCanvas, clipToWorld, nearClipZ, farClipZ);
                        source.Retain();
                        bool hadOldMapping = _outputMappingCache.TryGetValue(output, out var oldMapping);
                        _outputMappingCache[output] = (source, canvasToOutput,
                            worldToCanvas, clipToWorld, drawSpace, nearClipZ, farClipZ, publication);
                        if (hadOldMapping)
                        {
                            oldMapping.Source.Release();
                            oldMapping.Mapped.Release();
                        }
                    }
                    SetRenderedPublicationNoLock(output, publication, receiptId);
                }
                return;
            }
        }
        lock (_publicationGate)
            SetRenderedPublicationNoLock(output, publication, receiptId);
    }

    private void SetRenderedPublicationNoLock(object output, UIPublication publication, ulong receiptId)
    {
        if (publication.CanvasLifetime != (ulong)_canvasLifetime)
            return;
        publication.Retain();
        _renderedPublications.TryGetValue(output, out var old);
        _renderedPublications[output] = (RuntimeEngine.Rendering.State.RenderFrameId,
            publication, receiptId);
        old.Publication?.Release();
    }

    private void ClearPublications()
    {
        List<UIPublication> detached = [];
        lock (_publicationGate)
        {
            Interlocked.Increment(ref _canvasLifetime);
            if (_candidatePublication is { } candidate)
                detached.Add(candidate);
            _candidatePublication = null;
            if (_collectingPublication is { } collecting)
                detached.Add(collecting);
            _collectingPublication = null;
            if (_currentPublication is { } current)
                detached.Add(current);
            Volatile.Write(ref _currentPublication, null);
            foreach (var entry in _presentedPublications)
                detached.Add(entry.Value);
            _presentedPublications.Clear();
            foreach (var entry in _renderedPublications)
                detached.Add(entry.Value.Publication);
            _renderedPublications.Clear();
            foreach (var entry in _outputMappingCache)
            {
                detached.Add(entry.Value.Source);
                detached.Add(entry.Value.Mapped);
            }
            _outputMappingCache.Clear();
        }
        for (int index = 0; index < detached.Count; index++)
            detached[index].Release();
    }
}
