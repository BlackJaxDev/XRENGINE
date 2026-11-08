using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Explicit texture residency requests. A request is a demand signal separate from visibility:
/// it raises the policy target of one texture to a requested mip until the caller cancels it.
/// It does not count as visibility, it does not set the visible pin, and it goes through the
/// normal transition path, so the per-frame limits, the VRAM budget fit, and the publish rule
/// (render thread only, after the GPU receipt and a generation check) apply to it.
/// </summary>
internal sealed partial class ImportedTextureStreamingManager
{
    private readonly Lock _residencyRequestSync = new();
    private readonly Dictionary<long, TextureResidencyRequest> _residencyRequestsById = [];
    private readonly List<TextureResidencyRequest> _activeResidencyRequests = [];
    private readonly List<ImportedTextureStreamingRecord> _requestedRecords = [];
    private readonly List<long> _completedResidencyRequestScratch = [];
    private readonly List<TextureResidencyRequest> _droppedResidencyRequestScratch = [];
    private long _nextResidencyTicketId;
    private long _residencyRefreshSequence;
    private bool _requestImportDeferralLogged;

    /// <summary>
    /// Occurs one time for each request when its mip becomes resident. The manager sets the request
    /// state to <see cref="ETextureResidencyRequestState.Resident"/> before it raises the event.
    /// The event runs on the evaluation thread, outside the manager locks. Keep handlers short.
    /// A cancel can occur between the state change and the event, so a handler can receive one
    /// event for a ticket that it already cancelled. Handlers must ignore such tickets.
    /// </summary>
    internal event Action<TextureResidencyTicket>? ResidencyRequestCompleted;

    /// <summary>The current collect frame. Use it to compute the deadline frame of a request.</summary>
    internal long CurrentFrameId => Volatile.Read(ref _collectFrameId);

    /// <summary>The number of requests that are not cancelled.</summary>
    internal int ActiveResidencyRequestCount
    {
        get
        {
            lock (_residencyRequestSync)
                return _activeResidencyRequests.Count;
        }
    }

    /// <summary>
    /// Requests that a streamed texture holds at least <paramref name="targetMip"/>. The next evaluation
    /// queues the promotion. The request holds the size until <see cref="CancelResidency"/>.
    /// </summary>
    /// <param name="texture">A texture that imported texture streaming tracks.</param>
    /// <param name="targetMip">The mip to make resident. 0 is the full source size. A large value requests the preview only.</param>
    /// <param name="deadlineFrame">The collect frame (see <see cref="CurrentFrameId"/>) at which the caller needs the mip. Use <see cref="long.MaxValue"/> for no deadline.</param>
    /// <param name="priority">The job priority of the promotion. A promotion at or after its deadline uses <see cref="JobPriority.Highest"/>.</param>
    /// <param name="generation">The caller generation. The rejection and cancel logs show it. The manager does not compare it.</param>
    /// <returns>The ticket, or <see cref="TextureResidencyTicket.None"/> if streaming does not track the texture.</returns>
    internal TextureResidencyTicket RequestResidency(
        XRTexture2D texture,
        int targetMip,
        long deadlineFrame,
        JobPriority priority,
        int generation)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(targetMip);

        if (!TryGetStreamingRecord(texture, out ImportedTextureStreamingRecord? record))
        {
            Debug.TexturesWarning(
                $"[TextureStreaming] Residency request rejected for '{texture.Name ?? texture.FilePath}' (generation {generation}): texture streaming does not track the texture.");
            return TextureResidencyTicket.None;
        }

        long id = Interlocked.Increment(ref _nextResidencyTicketId);
        TextureResidencyRequest request = new(id, record, targetMip, deadlineFrame, priority, generation);
        lock (_residencyRequestSync)
        {
            request.ListIndex = _activeResidencyRequests.Count;
            _activeResidencyRequests.Add(request);
            _residencyRequestsById.Add(id, request);
        }

        return new TextureResidencyTicket(id);
    }

    /// <summary>
    /// Cancels a request. A resident request releases its hold, and the texture goes back to
    /// visibility-driven residency; the cancel does not force a demotion. If the request raised a
    /// promotion that is still pending, and no other request needs it, the cancel also cancels that
    /// promotion, so its result never publishes.
    /// </summary>
    /// <returns>True if the ticket identified an active request.</returns>
    internal bool CancelResidency(TextureResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return false;

        ImportedTextureStreamingRecord record;
        CancellationTokenSource? canceledLoad = null;
        uint canceledDimension = 0;
        int canceledGeneration = 0;
        string backendName = string.Empty;
        lock (_residencyRequestSync)
        {
            if (!_residencyRequestsById.Remove(ticket.Id, out TextureResidencyRequest? request))
                return false;

            RemoveActiveRequestNoLock(request);
            request.State = ETextureResidencyRequestState.None;
            record = request.Record;
            canceledGeneration = request.Generation;
            lock (record.Sync)
            {
                WriteRequestAggregateNoLock(record);
                bool pendingPromotion = record.PendingLoadCts is not null
                    && record.PendingTransitionRequestDriven
                    && record.PendingMaxDimension > record.ResidentMaxDimension
                    && record.PendingSparseTransitionResult is not { ExposureDeferred: true };
                if (pendingPromotion
                    && !TextureResidencyPolicy.RequestCoversTarget(record.RequestedMaxDimension, record.PreviewReady, record.PendingMaxDimension))
                {
                    canceledLoad = record.PendingLoadCts;
                    canceledDimension = record.PendingMaxDimension;
                    backendName = record.PendingTransitionBackendName;
                    TextureTransitionQueue.ClearPendingStateAfterLock(record);
                    record.PublicationEligibleGeneration = 0L;
                    record.LastTransitionFrameId = CurrentFrameId;
                }
            }
        }

        if (canceledLoad is not null)
        {
            string? textureName = record.Texture.TryGetTarget(out XRTexture2D? texture) ? texture.Name : null;
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                CurrentFrameId,
                textureName,
                record.FilePath,
                canceledDimension,
                backendName,
                $"residency request canceled (generation {canceledGeneration})");
            CancelPendingLoad(canceledLoad);
        }

        return true;
    }

    /// <summary>Gets the state of a request. A cancelled or unknown ticket returns <see cref="ETextureResidencyRequestState.None"/>.</summary>
    internal ETextureResidencyRequestState GetResidencyRequestState(TextureResidencyTicket ticket)
    {
        lock (_residencyRequestSync)
        {
            return _residencyRequestsById.TryGetValue(ticket.Id, out TextureResidencyRequest? request)
                ? request.State
                : ETextureResidencyRequestState.None;
        }
    }

    /// <summary>Gets the largest side of the source of a tracked texture. False if the size is not known yet.</summary>
    internal bool TryGetSourceMaxDimension(XRTexture2D texture, out uint sourceMaxDimension)
    {
        sourceMaxDimension = 0;
        if (texture is null || !_registry.TryGetRecord(texture, out ImportedTextureStreamingRecord? record) || record is null)
            return false;

        lock (record.Sync)
            sourceMaxDimension = Math.Max(record.SourceWidth, record.SourceHeight);
        return sourceMaxDimension != 0;
    }

    /// <summary>
    /// While imports are active, the policy queues no promotion of a texture that is not visible.
    /// That includes request-driven promotions. This writes one diagnostic for each import period,
    /// so the wait is not silent. The evaluation thread calls it.
    /// </summary>
    private void LogRequestDeferredByImportsOnce(ImportedTextureStreamingSnapshot snapshot)
    {
        if (_requestImportDeferralLogged)
            return;

        _requestImportDeferralLogged = true;
        Debug.Textures(
            $"[TextureStreaming] Residency request promotion of '{snapshot.TextureName ?? snapshot.FilePath}' waits: "
            + "active model imports block promotions of textures that are not visible.");
    }

    private bool TryGetStreamingRecord(XRTexture2D texture, [NotNullWhen(true)] out ImportedTextureStreamingRecord? record)
    {
        if (!_registry.TryGetRecord(texture, out ImportedTextureStreamingRecord? found) || found is null)
        {
            // A native imported texture registers its streaming source on its first use.
            // A request is a use, so the request registers it in the same way.
            texture.TryRestoreImportedTextureStreamingSource();
            _registry.TryGetRecord(texture, out found);
        }

        record = found;
        if (found is null)
            return false;

        lock (found.Sync)
            return found.Source is not null && !string.IsNullOrWhiteSpace(found.FilePath);
    }

    /// <summary>
    /// Writes the request fields of each requested record and updates the request states.
    /// The evaluation calls it before it collects snapshots. It allocates nothing after warm-up.
    /// </summary>
    private void RefreshResidencyRequests(long frameId)
    {
        List<long> completed = _completedResidencyRequestScratch;
        completed.Clear();
        lock (_residencyRequestSync)
        {
            for (int i = 0; i < _requestedRecords.Count; i++)
            {
                ImportedTextureStreamingRecord previous = _requestedRecords[i];
                lock (previous.Sync)
                    ClearRequestAggregateNoLock(previous);
            }
            _requestedRecords.Clear();

            if (_activeResidencyRequests.Count == 0)
                return;

            long sequence = ++_residencyRefreshSequence;
            EEngineQuality quality = RuntimeRenderingHostServices.Settings.TextureQuality;
            for (int i = 0; i < _activeResidencyRequests.Count; i++)
            {
                TextureResidencyRequest request = _activeResidencyRequests[i];
                ImportedTextureStreamingRecord record = request.Record;
                if (IsRequestTextureGone(record))
                {
                    _droppedResidencyRequestScratch.Add(request);
                    continue;
                }

                bool resident;
                lock (record.Sync)
                {
                    if (record.RequestRefreshSequence != sequence)
                    {
                        ClearRequestAggregateNoLock(record);
                        record.RequestRefreshSequence = sequence;
                        _requestedRecords.Add(record);
                    }

                    uint sourceMaxDimension = Math.Max(record.SourceWidth, record.SourceHeight);
                    uint requestedMaxDimension = TextureResidencyPolicy.ResolveRequestedMaxDimension(sourceMaxDimension, request.TargetMip);
                    request.RequestedMaxDimension = requestedMaxDimension;
                    AddToRequestAggregateNoLock(record, request, requestedMaxDimension);
                    resident = IsRequestResidentNoLock(record, requestedMaxDimension, sourceMaxDimension, quality);
                }

                if (resident && request.State == ETextureResidencyRequestState.Pending)
                {
                    request.State = ETextureResidencyRequestState.Resident;
                    completed.Add(request.Id);
                }
                else if (!resident && request.State == ETextureResidencyRequestState.Resident)
                {
                    // A budget demotion took the mip away. The request asks for it again; the
                    // completion event does not repeat.
                    request.State = ETextureResidencyRequestState.Pending;
                }
            }

            DropResidencyRequestsNoLock(_droppedResidencyRequestScratch);
        }

        Action<TextureResidencyTicket>? handler = ResidencyRequestCompleted;
        if (handler is null)
            return;

        for (int i = 0; i < completed.Count; i++)
            handler(new TextureResidencyTicket(completed[i]));
        completed.Clear();
    }

    /// <summary>
    /// True if the published resident size covers the request. The texture quality cap lowers the
    /// size that the request needs, because the policy never goes above that cap.
    /// </summary>
    private static bool IsRequestResidentNoLock(
        ImportedTextureStreamingRecord record,
        uint requestedMaxDimension,
        uint sourceMaxDimension,
        EEngineQuality quality)
    {
        if (!record.PreviewReady || sourceMaxDimension == 0 || record.ResidentMaxDimension == 0)
            return false;

        uint previewMaxDimension = record.Backend?.PreviewMaxDimension ?? 0u;
        uint target = TextureResidencyPolicy.ApplyTextureQuality(
            Math.Min(requestedMaxDimension, sourceMaxDimension),
            previewMaxDimension,
            quality);
        return record.ResidentMaxDimension >= target;
    }

    /// <summary>True if the texture of a record was collected, destroyed, or queued for destruction.</summary>
    private static bool IsRequestTextureGone(ImportedTextureStreamingRecord record)
        => !record.Texture.TryGetTarget(out XRTexture2D? texture) || texture.IsDestroyed || texture.IsDestroyQueued;

    /// <summary>
    /// Ends the requests of textures that are gone, so a request whose owner never cancels it does not stay for the life
    /// of the process. A dropped request has the state <see cref="ETextureResidencyRequestState.None"/>, and a later cancel
    /// of its ticket returns false. The caller holds the request lock. It clears the list.
    /// </summary>
    private void DropResidencyRequestsNoLock(List<TextureResidencyRequest> dropped)
    {
        for (int i = 0; i < dropped.Count; i++)
        {
            TextureResidencyRequest request = dropped[i];
            if (!_residencyRequestsById.Remove(request.Id))
                continue;

            RemoveActiveRequestNoLock(request);
            request.State = ETextureResidencyRequestState.None;
            Debug.Textures(
                $"[TextureStreaming] Residency request {request.Id} (generation {request.Generation}) for '{request.Record.FilePath}' ended: the texture was destroyed.");
        }

        dropped.Clear();
    }

    private void RemoveActiveRequestNoLock(TextureResidencyRequest request)
    {
        int index = request.ListIndex;
        int last = _activeResidencyRequests.Count - 1;
        if (index < 0 || index > last)
            return;

        TextureResidencyRequest moved = _activeResidencyRequests[last];
        _activeResidencyRequests[index] = moved;
        moved.ListIndex = index;
        _activeResidencyRequests.RemoveAt(last);
        request.ListIndex = -1;
    }

    /// <summary>Rewrites the request fields of one record from the active requests. The caller holds both locks.</summary>
    private void WriteRequestAggregateNoLock(ImportedTextureStreamingRecord record)
    {
        ClearRequestAggregateNoLock(record);
        uint sourceMaxDimension = Math.Max(record.SourceWidth, record.SourceHeight);
        for (int i = 0; i < _activeResidencyRequests.Count; i++)
        {
            TextureResidencyRequest other = _activeResidencyRequests[i];
            if (!ReferenceEquals(other.Record, record))
                continue;

            AddToRequestAggregateNoLock(
                record,
                other,
                TextureResidencyPolicy.ResolveRequestedMaxDimension(sourceMaxDimension, other.TargetMip));
        }
    }

    private static void AddToRequestAggregateNoLock(
        ImportedTextureStreamingRecord record,
        TextureResidencyRequest request,
        uint requestedMaxDimension)
    {
        if (requestedMaxDimension > record.RequestedMaxDimension)
            record.RequestedMaxDimension = requestedMaxDimension;
        if (request.Priority > record.RequestPriority)
            record.RequestPriority = request.Priority;
        if (request.DeadlineFrameId < record.RequestDeadlineFrameId)
            record.RequestDeadlineFrameId = request.DeadlineFrameId;
    }

    private static void ClearRequestAggregateNoLock(ImportedTextureStreamingRecord record)
    {
        record.RequestedMaxDimension = 0;
        record.RequestPriority = JobPriority.Low;
        record.RequestDeadlineFrameId = long.MaxValue;
    }
}
