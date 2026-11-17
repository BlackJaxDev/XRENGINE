namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    private const int PendingSubmissionReceiptCapacity = 32;
    private const int MaxGroupedSubmissionCandidateCount = 8;
    private const int InitialSubmissionCandidateCapacity = 32;

    private readonly HashSet<ShadowRequestKey> _submissionRetryKeys = new();
    // Copy of _submissionRetryKeys for the planning thread. The render thread owns the retry keys;
    // the planning thread reads this copy when it groups the cascades of the next render plan,
    // so a cascade that only waits for a retry still joins its cascade group.
    private readonly HashSet<ShadowRequestKey> _planningSubmissionRetryKeys = new();
    private readonly object _planningSubmissionRetrySync = new();
    private readonly HashSet<ShadowRequestKey> _submissionCandidateKeySet = new();
    private readonly Dictionary<ShadowRequestKey, ulong> _latestSubmissionFrameByKey = new();
    private readonly XRGpuFence?[] _pendingSubmissionFences = new XRGpuFence?[PendingSubmissionReceiptCapacity];
    private readonly ulong[] _pendingSubmissionFrameIds = new ulong[PendingSubmissionReceiptCapacity];
    private readonly int[] _pendingSubmissionKeyCounts = new int[PendingSubmissionReceiptCapacity];
    private readonly ShadowRequestKey[][] _pendingSubmissionKeysByReceipt = CreatePendingSubmissionKeyStorage();
    private ShadowRequestKey[] _submissionCandidateKeys = new ShadowRequestKey[InitialSubmissionCandidateCapacity];
    private int _submissionCandidateCount;
    private int _pendingSubmissionReceiptCount;
    private int _nextPendingSubmissionReceiptSlot;
    private ulong _lastSubmissionTrackingFrameId = ulong.MaxValue;
    private bool _trackingSubmissionCandidates;

    private static ShadowRequestKey[][] CreatePendingSubmissionKeyStorage()
    {
        ShadowRequestKey[][] storage = new ShadowRequestKey[PendingSubmissionReceiptCapacity][];
        for (int i = 0; i < storage.Length; i++)
            storage[i] = new ShadowRequestKey[InitialSubmissionCandidateCapacity];
        return storage;
    }

    private ulong SubmissionTrackingRenderFrameId
    {
        get
        {
            ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            return renderFrameId != 0u ? renderFrameId : _frameId;
        }
    }

    private void EnsureSubmissionTrackingCapacity(int requestedCapacity)
    {
        int maxRequestCount = Math.Max(1, requestedCapacity);
        long renderCandidateCapacity =
            (long)Math.Max(0, _settings.MaxTilesRenderedPerFrame) +
            MaxGroupedSubmissionCandidateCount;
        int candidateCapacity = (int)Math.Min(
            maxRequestCount,
            Math.Max(MaxGroupedSubmissionCandidateCount, renderCandidateCapacity));

        _submissionRetryKeys.EnsureCapacity(maxRequestCount);
        lock (_planningSubmissionRetrySync)
            _planningSubmissionRetryKeys.EnsureCapacity(maxRequestCount);
        _latestSubmissionFrameByKey.EnsureCapacity(maxRequestCount);
        _submissionCandidateKeySet.EnsureCapacity(candidateCapacity);
        if (_submissionCandidateKeys.Length < candidateCapacity)
            Array.Resize(ref _submissionCandidateKeys, candidateCapacity);
        for (int i = 0; i < _pendingSubmissionKeysByReceipt.Length; i++)
            if (_pendingSubmissionKeysByReceipt[i].Length < candidateCapacity)
                Array.Resize(ref _pendingSubmissionKeysByReceipt[i], candidateCapacity);
    }

    /// <summary>
    /// Resolves the preceding frame's atlas-write receipt and starts a new bounded
    /// submission cohort. Returning false prevents the same immutable plan from
    /// being encoded twice when multiple window callbacks visit one world in a frame.
    /// </summary>
    private bool BeginSubmissionTracking()
    {
        // Immediate backends execute atlas writers while this method is on the
        // render thread. Vulkan is the deferred backend whose recorded frame can
        // still be abandoned after the atlas manager commits its CPU-side state.
        if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend !=
            RuntimeGraphicsApiKind.Vulkan)
        {
            return true;
        }

        ulong renderFrameId = SubmissionTrackingRenderFrameId;
        if (_lastSubmissionTrackingFrameId == renderFrameId)
            return false;

        _lastSubmissionTrackingFrameId = renderFrameId;
        ResolvePendingSubmissionReceipts();
        _submissionCandidateCount = 0;
        _submissionCandidateKeySet.Clear();
        _trackingSubmissionCandidates = true;
        return true;
    }

    /// <summary>
    /// Reopens submission tracking for atlas writers recorded after the
    /// scheduled-tile pass of the same render frame (the Advanced directional
    /// shadow lane records its groups from the viewport's command chain). The
    /// caller must balance it with <see cref="EndSubmissionTracking"/>, which
    /// binds a separate receipt for this cohort.
    /// </summary>
    private bool ResumeSubmissionTrackingForDeferredWork()
    {
        if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend !=
            RuntimeGraphicsApiKind.Vulkan)
        {
            return false;
        }

        _submissionCandidateCount = 0;
        _submissionCandidateKeySet.Clear();
        _trackingSubmissionCandidates = true;
        return true;
    }

    private void ResolvePendingSubmissionReceipts()
    {
        if (_pendingSubmissionReceiptCount == 0)
            return;

        for (int slot = 0; slot < _pendingSubmissionFences.Length; slot++)
        {
            XRGpuFence? fence = _pendingSubmissionFences[slot];
            if (fence is null)
                continue;

            EGpuFenceSubmissionStatus status = fence.SubmissionStatus;
            if (status == EGpuFenceSubmissionStatus.AwaitingSubmission)
                continue;

            ulong submissionFrameId = _pendingSubmissionFrameIds[slot];
            int keyCount = _pendingSubmissionKeyCounts[slot];
            ShadowRequestKey[] keys = _pendingSubmissionKeysByReceipt[slot];
            bool submitted = status == EGpuFenceSubmissionStatus.Submitted;
            int retryCount = 0;
            for (int i = 0; i < keyCount; i++)
            {
                ShadowRequestKey key = keys[i];
                if (!_latestSubmissionFrameByKey.TryGetValue(key, out ulong latestFrameId) ||
                    latestFrameId != submissionFrameId)
                {
                    continue;
                }

                _latestSubmissionFrameByKey.Remove(key);
                if (submitted)
                {
                    RemoveSubmissionRetryKey(key);
                    continue;
                }

                AddSubmissionRetryKey(key);
                InvalidateDirectionalStaticCascadeTile(key, submissionFrameId);
                retryCount++;
            }

            if (!submitted && retryCount > 0)
            {
                XREngine.Debug.LightingWarningEvery(
                    $"ShadowAtlas.SubmissionReceiptRejected.{GetHashCode()}",
                    TimeSpan.FromSeconds(1.0),
                    "[ShadowAtlas] Atlas writers from render frame {0} were rejected by backend submission (status={1}); retrying {2} latest request(s) instead of accepting unwritten atlas content.",
                    submissionFrameId,
                    status,
                    retryCount);
            }

            ReleasePendingSubmissionReceipt(slot, fence, keyCount);
        }
    }

    /// <summary>Adds a retry key on the render thread and to the planning copy.</summary>
    private void AddSubmissionRetryKey(ShadowRequestKey key)
    {
        if (!_submissionRetryKeys.Add(key))
            return;
        lock (_planningSubmissionRetrySync)
            _planningSubmissionRetryKeys.Add(key);
    }

    /// <summary>Removes a retry key on the render thread and from the planning copy.</summary>
    private void RemoveSubmissionRetryKey(ShadowRequestKey key)
    {
        if (!_submissionRetryKeys.Remove(key))
            return;
        lock (_planningSubmissionRetrySync)
            _planningSubmissionRetryKeys.Remove(key);
    }

    /// <summary>Clears the retry keys on the render thread and the planning copy.</summary>
    private void ClearSubmissionRetryKeys()
    {
        _submissionRetryKeys.Clear();
        lock (_planningSubmissionRetrySync)
            _planningSubmissionRetryKeys.Clear();
    }

    /// <summary>
    /// True on the planning thread when the last render-thread state holds a submission retry for
    /// <paramref name="key"/>.
    /// </summary>
    private bool IsSubmissionRetryPendingForPlanning(in ShadowRequestKey key)
    {
        lock (_planningSubmissionRetrySync)
            return _planningSubmissionRetryKeys.Count != 0 && _planningSubmissionRetryKeys.Contains(key);
    }

    private bool RequiresSubmissionRetry(
        ShadowAtlasRenderPlan plan,
        in ShadowAtlasRenderPlanEntry entry)
    {
        if (_submissionRetryKeys.Count == 0)
            return false;

        if (entry.Kind == ShadowAtlasRenderPlanEntryKind.Tile)
            return _submissionRetryKeys.Contains(entry.Request.Key);

        if (!TryValidatePlanMemberRange(plan, entry, "submission-retry"))
            return false;

        for (int i = 0; i < entry.MemberCount; i++)
        {
            if (plan.TryGetMember(
                    entry.MemberStart + i,
                    out ShadowAtlasRenderPlanMember member) &&
                _submissionRetryKeys.Contains(member.Request.Key))
            {
                return true;
            }
        }

        return false;
    }

    private void TrackSubmissionCandidate(ShadowRequestKey key)
    {
        if (!_trackingSubmissionCandidates)
            return;

        RemoveSubmissionRetryKey(key);
        if (!_submissionCandidateKeySet.Add(key))
            return;

        if (_submissionCandidateCount >= _submissionCandidateKeys.Length)
        {
            _submissionCandidateKeySet.Remove(key);
            _latestSubmissionFrameByKey.Remove(key);
            AddSubmissionRetryKey(key);
            _queueOverflowCount++;
            return;
        }

        _submissionCandidateKeys[_submissionCandidateCount++] = key;
    }

    /// <summary>
    /// Places one receipt after every atlas writer encoded by this manager call.
    /// Vulkan binds the receipt only when the containing command stream is actually
    /// submitted. Immediate backends bypass this bookkeeping entirely.
    /// </summary>
    private void EndSubmissionTracking()
    {
        try
        {
            if (!_trackingSubmissionCandidates || _submissionCandidateCount == 0)
                return;

            XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
            if (fence is null)
            {
                if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend ==
                    RuntimeGraphicsApiKind.Vulkan)
                {
                    for (int i = 0; i < _submissionCandidateCount; i++)
                    {
                        ShadowRequestKey key = _submissionCandidateKeys[i];
                        _latestSubmissionFrameByKey.Remove(key);
                        AddSubmissionRetryKey(key);
                    }

                    XREngine.Debug.LightingWarningEvery(
                        $"ShadowAtlas.SubmissionReceiptUnavailable.{GetHashCode()}",
                        TimeSpan.FromSeconds(1.0),
                        "[ShadowAtlas] Vulkan did not provide an atlas-write submission receipt for render frame {0}; retaining {1} request(s) for retry.",
                        SubmissionTrackingRenderFrameId,
                        _submissionCandidateCount);
                }

                return;
            }

            int receiptSlot = FindAvailablePendingSubmissionReceiptSlot();
            if (receiptSlot < 0)
            {
                for (int i = 0; i < _submissionCandidateCount; i++)
                {
                    ShadowRequestKey key = _submissionCandidateKeys[i];
                    _latestSubmissionFrameByKey.Remove(key);
                    AddSubmissionRetryKey(key);
                }

                XREngine.Debug.LightingWarningEvery(
                    $"ShadowAtlas.SubmissionReceiptCapacity.{GetHashCode()}",
                    TimeSpan.FromSeconds(1.0),
                    "[ShadowAtlas] Vulkan has {0} unresolved atlas-write receipts; retaining {1} new request(s) for retry instead of dropping submission accountability.",
                    PendingSubmissionReceiptCapacity,
                    _submissionCandidateCount);
                fence.Dispose();
                return;
            }

            ulong submissionFrameId = SubmissionTrackingRenderFrameId;
            ShadowRequestKey[] pendingKeys = _pendingSubmissionKeysByReceipt[receiptSlot];
            Array.Copy(
                _submissionCandidateKeys,
                pendingKeys,
                _submissionCandidateCount);
            _pendingSubmissionKeyCounts[receiptSlot] = _submissionCandidateCount;
            _pendingSubmissionFrameIds[receiptSlot] = submissionFrameId;
            _pendingSubmissionFences[receiptSlot] = fence;
            _pendingSubmissionReceiptCount++;
            for (int i = 0; i < _submissionCandidateCount; i++)
                _latestSubmissionFrameByKey[_submissionCandidateKeys[i]] = submissionFrameId;
        }
        finally
        {
            Array.Clear(_submissionCandidateKeys, 0, _submissionCandidateCount);
            _submissionCandidateKeySet.Clear();
            _submissionCandidateCount = 0;
            _trackingSubmissionCandidates = false;
        }
    }

    private int FindAvailablePendingSubmissionReceiptSlot()
    {
        for (int offset = 0; offset < _pendingSubmissionFences.Length; offset++)
        {
            int slot = (_nextPendingSubmissionReceiptSlot + offset) %
                _pendingSubmissionFences.Length;
            if (_pendingSubmissionFences[slot] is not null)
                continue;

            _nextPendingSubmissionReceiptSlot = (slot + 1) %
                _pendingSubmissionFences.Length;
            return slot;
        }

        return -1;
    }

    private void ReleasePendingSubmissionReceipt(
        int slot,
        XRGpuFence fence,
        int keyCount)
    {
        fence.Dispose();
        _pendingSubmissionFences[slot] = null;
        _pendingSubmissionFrameIds[slot] = 0u;
        _pendingSubmissionKeyCounts[slot] = 0;
        Array.Clear(_pendingSubmissionKeysByReceipt[slot], 0, keyCount);
        _pendingSubmissionReceiptCount--;
    }

    private void ResetSubmissionTracking()
    {
        for (int slot = 0; slot < _pendingSubmissionFences.Length; slot++)
        {
            XRGpuFence? fence = _pendingSubmissionFences[slot];
            if (fence is not null)
                ReleasePendingSubmissionReceipt(
                    slot,
                    fence,
                    _pendingSubmissionKeyCounts[slot]);
        }

        Array.Clear(_submissionCandidateKeys, 0, _submissionCandidateCount);
        _pendingSubmissionReceiptCount = 0;
        _nextPendingSubmissionReceiptSlot = 0;
        _submissionCandidateCount = 0;
        _lastSubmissionTrackingFrameId = ulong.MaxValue;
        _trackingSubmissionCandidates = false;
        ClearSubmissionRetryKeys();
        _submissionCandidateKeySet.Clear();
        _latestSubmissionFrameByKey.Clear();
    }
}
