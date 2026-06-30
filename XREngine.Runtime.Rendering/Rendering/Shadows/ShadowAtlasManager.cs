using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasManager
{
    private const int AtlasKindCount = 3;
    private const int ShadowEncodingCount = 4;
    private const string DirectionalGroupedFramesCounterName = "ShadowAtlas.Directional.GroupedFrames";
    private const string DirectionalSequentialFallbackFramesCounterName = "ShadowAtlas.Directional.SequentialFallbackFrames";
    private const ulong LodVoluntaryChangeCooldownFrames = 6u;
    private const ulong LodDownsizeRePromotionCooldownFrames = 36u;
    private const ulong DemotionPromotionCooldownFrames = 12u;
    // Keep recently used regions warm long enough to survive transient camera/
    // relevance gaps without repacking visible cascades a few frames later.
    private const ulong ResidentEvictionTtlFrames = 240u;
    private const float DemotionSwitchMargin = 0.25f;

    private sealed class RequestComparer : IComparer<ShadowMapRequest>
    {
        public static readonly RequestComparer Instance = new(null);
        private readonly ShadowAtlasManager? _owner;

        public RequestComparer(ShadowAtlasManager? owner)
        {
            _owner = owner;
        }

        public int Compare(ShadowMapRequest x, ShadowMapRequest y)
        {
            int result = y.EditorPinned.CompareTo(x.EditorPinned);
            if (result != 0)
                return result;

            result = GetRenderOrderBucket(x).CompareTo(GetRenderOrderBucket(y));
            if (result != 0)
                return result;

            result = y.Priority.CompareTo(x.Priority);
            if (result != 0)
                return result;

            if (_owner is not null &&
                _owner.TryComparePriorPlacement(x.Key, y.Key, out result) &&
                result != 0)
            {
                return result;
            }

            return x.Key.CompareTo(y.Key);
        }

        private static int GetRenderOrderBucket(ShadowMapRequest request)
        {
            if (!request.IsDirty && request.CanReusePreviousFrame)
                return 4;

            return request.ProjectionType switch
            {
                EShadowProjectionType.DirectionalCascade or EShadowProjectionType.DirectionalPrimary => 0,
                EShadowProjectionType.SpotPrimary => 1,
                EShadowProjectionType.PointFace => 2,
                _ => 3,
            };
        }
    }

    private sealed class GroupedAllocationMemberComparer : IComparer<ShadowAtlasGroupedAllocationMember>
    {
        public static readonly GroupedAllocationMemberComparer Instance = new();

        public int Compare(ShadowAtlasGroupedAllocationMember x, ShadowAtlasGroupedAllocationMember y)
        {
            int result = x.CascadeIndex.CompareTo(y.CascadeIndex);
            return result != 0 ? result : x.RecordIndex.CompareTo(y.RecordIndex);
        }
    }

    private struct BalancedAllocationEntry
    {
        public required ShadowMapRequest Request { get; init; }
        public required uint MinimumResolution { get; init; }
        public required float RelevanceScore { get; init; }
        public uint Resolution { get; set; }
        public ShadowAtlasAllocation Allocation { get; set; }
        public bool AllocationSolved { get; set; }
    }

    private enum ShadowLodTransitionReason
    {
        Voluntary = 0,
        ForcedDownsize = 1,
        ForcedUpsize = 2,
    }

    private readonly record struct ShadowLodState(
        uint Resolution,
        ulong LastChangedFrame,
        ShadowLodTransitionReason LastTransitionReason);

    private readonly record struct ShadowDemotionState(ulong LastDemotedFrame, float LastRelevanceScore);
    private readonly record struct ShadowResidentEntry(ShadowAtlasAllocation Allocation, ulong LastRequestedFrame);
    private readonly record struct PendingSkippedAllocation(ShadowMapRequest Request, ShadowAtlasAllocation Allocation);
    private readonly record struct DirectionalGroupReservationFailure(
        Guid LightId,
        ShadowRequestDomain Domain,
        EShadowMapEncoding Encoding,
        string Reason);

    private readonly record struct DirectionalCascadeGroupKey(
        Guid LightId,
        ShadowRequestDomain Domain,
        EShadowMapEncoding Encoding,
        int AtlasId,
        int PageIndex);

    private readonly record struct PointFaceGroupKey(
        Guid LightId,
        ShadowRequestDomain Domain,
        EShadowMapEncoding Encoding,
        int AtlasId,
        int PageIndex);

    private readonly record struct DirectionalAtlasRenderEvent(
        Guid LightId,
        int RenderedCascades,
        bool SequentialCascadeRender,
        bool GroupedRenderAttempted,
        bool GroupedRenderSucceeded,
        bool CriticalBudgetBypassUsed,
        string SelectedCascadeBackend,
        string FallbackReason,
        double ElapsedShadowMilliseconds);

    private struct ShadowAtlasSolveDiagnosticsBuilder
    {
        public int ClassifiedRequestCount;
        public int DirectionalRequestCount;
        public int SpotRequestCount;
        public int PointRequestCount;
        public int DepthRequestCount;
        public int Variance2RequestCount;
        public int ExponentialVariance2RequestCount;
        public int ExponentialVariance4RequestCount;
        public int BalancedSolveAttemptCount;
        public int FailedCandidateCount;
        public int DemotionCount;
        public int StickyDemotionCount;
        public int DirectionalGroupDemotionCount;
        public int DeterministicFallbackDemotionCount;
        public int PriorReserveHitCount;
        public int PriorReserveMissCount;
        public int PriorSubBlockHitCount;
        public int PriorSubBlockMissCount;
        public int PageAllocationAttemptCount;
        public int PageAllocationSuccessCount;
        public int PageCreateAttemptCount;
        public int PageCreateSuccessCount;
        public int PageClearCount;
        public int DirectionalGroupSeedCount;
        public int DirectionalGroupMemberCount;
        public int DirectionalGroupCoLocationFailureCount;
        public int PointGroupSeedCount;
        public int PointGroupMemberCount;
        public int PointGroupCoLocationFailureCount;
        public SkipReason LastFailureReason;

        public void Reset()
            => this = default;

        public ShadowAtlasSolveDiagnostics ToSnapshot(double elapsedMilliseconds)
            => new(
                elapsedMilliseconds,
                ClassifiedRequestCount,
                DirectionalRequestCount,
                SpotRequestCount,
                PointRequestCount,
                DepthRequestCount,
                Variance2RequestCount,
                ExponentialVariance2RequestCount,
                ExponentialVariance4RequestCount,
                BalancedSolveAttemptCount,
                FailedCandidateCount,
                DemotionCount,
                StickyDemotionCount,
                DirectionalGroupDemotionCount,
                DeterministicFallbackDemotionCount,
                PriorReserveHitCount,
                PriorReserveMissCount,
                PriorSubBlockHitCount,
                PriorSubBlockMissCount,
                PageAllocationAttemptCount,
                PageAllocationSuccessCount,
                PageCreateAttemptCount,
                PageCreateSuccessCount,
                PageClearCount,
                DirectionalGroupSeedCount,
                DirectionalGroupMemberCount,
                DirectionalGroupCoLocationFailureCount,
                PointGroupSeedCount,
                PointGroupMemberCount,
                PointGroupCoLocationFailureCount,
                LastFailureReason);
    }

    private readonly List<ShadowMapRequest> _requests;
    private readonly List<ShadowMapRequest>[] _requestBuckets;
    private readonly RequestComparer _requestComparer;
    private readonly List<BalancedAllocationEntry> _balancedAllocationEntries = new();
    private readonly List<ShadowAtlasAllocation> _frameAllocations;
    private readonly List<ShadowAtlasGroupedDirectionalCascadeAllocation> _directionalCascadeGroups = new();
    private readonly Dictionary<DirectionalCascadeGroupKey, List<ShadowAtlasGroupedAllocationMember>> _directionalCascadeGroupMembersByKey = new();
    private readonly List<DirectionalCascadeGroupKey> _directionalCascadeGroupKeyScratch = new(16);
    private readonly List<ShadowAtlasGroupedPointFaceAllocation> _pointFaceGroups = new();
    private readonly Dictionary<PointFaceGroupKey, List<ShadowAtlasGroupedAllocationMember>> _pointFaceGroupMembersByKey = new();
    private readonly List<PointFaceGroupKey> _pointFaceGroupKeyScratch = new(16);
    private readonly List<ShadowDirectionalAtlasLightDiagnostic> _directionalAtlasLightDiagnostics = new();
    private readonly List<DirectionalAtlasRenderEvent> _directionalAtlasRenderEvents = new();
    private readonly List<DirectionalGroupReservationFailure> _directionalGroupReservationFailures = new();
    private readonly List<ShadowAtlasPageDescriptor> _pageDescriptors;
    private readonly ShadowAtlasEncodingState[] _encodingStates;
    private readonly object _submitSync = new();
    private Dictionary<ShadowRequestKey, ShadowAtlasAllocation> _previousAllocations = new();
    private readonly Dictionary<ShadowRequestKey, ShadowAtlasAllocation> _currentAllocations = new();
    private readonly Dictionary<ShadowRequestKey, int> _currentAllocationIndices = new();
    private readonly Dictionary<ShadowRequestKey, ShadowResidentEntry> _residentAllocations = new();
    private readonly Dictionary<ShadowRequestKey, ShadowLodState> _lodStates = new();
    private readonly Dictionary<ShadowRequestKey, ShadowDemotionState> _demotionStates = new();
    private readonly List<ShadowRequestKey> _residentRemovalScratch = new();
    private readonly List<ShadowRequestKey> _demotionRemovalScratch = new();
    private readonly List<PendingSkippedAllocation> _pendingSkippedAllocations = new();
    private readonly ShadowAtlasFrameData[] _frameBuffers = [new(), new()];
    private int _publishedFrameIndex;
    private ShadowAtlasManagerSettings _settings;
    private ulong _frameId;
    private ulong _fallbackFrameId;
    private ulong _generation;
    private int _queueOverflowCount;
    private int _tilesScheduledThisFrame;
    private int _activeCameraCount;
    private bool _directionalGroupedFrame;
    private bool _directionalSequentialFallbackFrame;
    private bool _repackRequested;
    private ShadowAtlasSolveDiagnosticsBuilder _solveDiagnostics;
    private ShadowAtlasSolveDiagnostics _lastSolveDiagnostics;

    public ShadowAtlasManager()
        : this(ShadowAtlasManagerSettings.Default)
    {
    }

    public ShadowAtlasManager(ShadowAtlasManagerSettings settings)
    {
        _settings = NormalizeSettings(settings);
        _requestComparer = new RequestComparer(this);
        _requests = new(_settings.MaxRequestsPerFrame);
        _requestBuckets = new List<ShadowMapRequest>[AtlasKindCount * ShadowEncodingCount];
        for (int i = 0; i < _requestBuckets.Length; i++)
            _requestBuckets[i] = new List<ShadowMapRequest>(_settings.MaxRequestsPerFrame / _requestBuckets.Length + 1);

        _frameAllocations = new(_settings.MaxRequestsPerFrame);
        _pageDescriptors = new();
        _encodingStates = new ShadowAtlasEncodingState[AtlasKindCount * ShadowEncodingCount];
        for (int kind = 0; kind < AtlasKindCount; kind++)
            for (int encoding = 0; encoding < ShadowEncodingCount; encoding++)
                _encodingStates[GetStateIndex((EShadowAtlasKind)kind, (EShadowMapEncoding)encoding)] =
                    new((EShadowAtlasKind)kind, (EShadowMapEncoding)encoding);

        Configure(_settings);
    }

    public ShadowAtlasManagerSettings Settings => _settings;
    public ulong CurrentFrameId => _frameId;
    public ShadowAtlasFrameData PublishedFrameData => _frameBuffers[_publishedFrameIndex];
    public ShadowAtlasSolveDiagnostics LastSolveDiagnostics => _lastSolveDiagnostics;
    public IReadOnlyList<ShadowMapRequest> Requests => _requests;

    public void Configure(ShadowAtlasManagerSettings settings)
    {
        ShadowAtlasManagerSettings normalized = NormalizeSettings(settings);
        bool resourceShapeChanged = normalized.PageSize != _settings.PageSize ||
            normalized.MaxPages != _settings.MaxPages ||
            normalized.MaxMemoryBytes != _settings.MaxMemoryBytes ||
            normalized.MinTileResolution != _settings.MinTileResolution ||
            normalized.MaxTileResolution != _settings.MaxTileResolution;

        _settings = normalized;
        if (_requests.Capacity < _settings.MaxRequestsPerFrame)
            _requests.Capacity = _settings.MaxRequestsPerFrame;
        if (_frameAllocations.Capacity < _settings.MaxRequestsPerFrame)
            _frameAllocations.Capacity = _settings.MaxRequestsPerFrame;
        _previousAllocations.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _currentAllocations.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _currentAllocationIndices.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _residentAllocations.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _lodStates.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _demotionStates.EnsureCapacity(_settings.MaxRequestsPerFrame);
        _residentRemovalScratch.Capacity = Math.Max(_residentRemovalScratch.Capacity, _settings.MaxRequestsPerFrame);
        _demotionRemovalScratch.Capacity = Math.Max(_demotionRemovalScratch.Capacity, _settings.MaxRequestsPerFrame);
        _pendingSkippedAllocations.Capacity = Math.Max(_pendingSkippedAllocations.Capacity, _settings.MaxRequestsPerFrame);
        _directionalAtlasLightDiagnostics.Capacity = Math.Max(_directionalAtlasLightDiagnostics.Capacity, Math.Min(8, _settings.MaxRequestsPerFrame));
        _directionalAtlasRenderEvents.Capacity = Math.Max(_directionalAtlasRenderEvents.Capacity, Math.Min(16, _settings.MaxRequestsPerFrame));
        _directionalGroupReservationFailures.Capacity = Math.Max(_directionalGroupReservationFailures.Capacity, Math.Min(16, _settings.MaxRequestsPerFrame));

        int bucketCapacity = Math.Max(1, _settings.MaxRequestsPerFrame / _requestBuckets.Length);
        for (int i = 0; i < _requestBuckets.Length; i++)
            if (_requestBuckets[i].Capacity < bucketCapacity)
                _requestBuckets[i].Capacity = bucketCapacity;

        if (resourceShapeChanged)
            ResetResources();
    }

    public void ConfigureFromEngineSettings()
        => Configure(ShadowAtlasManagerSettings.FromCurrentRuntimeSettings());

    public void BeginFrame(IRuntimeRenderWorld world, ReadOnlySpan<XRCamera> activeCameras)
    {
        ConfigureFromEngineSettings();
        BeginFrameCore(RuntimeEngine.Rendering.State.RenderFrameId, activeCameras.Length);
    }

    public void BeginFrame(ulong frameId, int activeCameraCount = 0)
        => BeginFrameCore(frameId, activeCameraCount);

    private void BeginFrameCore(ulong frameId, int activeCameraCount)
    {
        if (frameId != 0u)
        {
            _frameId = frameId;
        }
        else
        {
            unchecked { _fallbackFrameId++; }
            if (_fallbackFrameId == 0u)
                _fallbackFrameId = 1u;
            _frameId = _fallbackFrameId;
        }

        _activeCameraCount = Math.Max(0, activeCameraCount);
        _queueOverflowCount = 0;
        _tilesScheduledThisFrame = 0;
        _requests.Clear();
        _frameAllocations.Clear();
        _directionalCascadeGroups.Clear();
        _pointFaceGroups.Clear();
        _directionalAtlasLightDiagnostics.Clear();
        _directionalAtlasRenderEvents.Clear();
        _currentAllocations.Clear();
        _currentAllocationIndices.Clear();
        _pendingSkippedAllocations.Clear();
        _directionalGroupedFrame = false;
        _directionalSequentialFallbackFrame = false;
        _directionalGroupReservationFailures.Clear();
        _solveDiagnostics.Reset();
        _lastSolveDiagnostics = default;
        for (int i = 0; i < _requestBuckets.Length; i++)
            _requestBuckets[i].Clear();

        for (int i = 0; i < _encodingStates.Length; i++)
            _encodingStates[i].BeginFrame();
    }

    public bool Submit(in ShadowMapRequest request)
    {
        lock (_submitSync)
        {
            if (!IsValidRequest(request))
            {
                _frameAllocations.Add(CreateSkippedAllocation(request, SkipReason.InvalidRequest));
                return false;
            }

            if (_activeCameraCount <= 0 && request.ProjectionType != EShadowProjectionType.DirectionalPrimary)
            {
                _frameAllocations.Add(CreateSkippedAllocation(request, SkipReason.NoConsumerCamera));
                return false;
            }

            if (_requests.Count >= _settings.MaxRequestsPerFrame)
            {
                _queueOverflowCount++;
                int worstIndex = FindWorstQueuedRequestIndex();
                ShadowMapRequest dropped = _requests[worstIndex];
                if (RequestComparer.Instance.Compare(request, dropped) < 0)
                {
                    _requests[worstIndex] = request;
                    _frameAllocations.Add(CreateSkippedAllocation(dropped, SkipReason.QueueOverflow));
                    XREngine.Debug.Lighting($"[ShadowAtlas] Dropped shadow request for {dropped.Key} because the per-frame request queue is full ({_settings.MaxRequestsPerFrame}).");
                    return true;
                }

                _frameAllocations.Add(CreateSkippedAllocation(request, SkipReason.QueueOverflow));
                XREngine.Debug.Lighting($"[ShadowAtlas] Dropped shadow request for {request.Key} because the per-frame request queue is full ({_settings.MaxRequestsPerFrame}).");
                return false;
            }

            _requests.Add(request);
            return true;
        }
    }

    private int FindWorstQueuedRequestIndex()
    {
        int worstIndex = 0;
        for (int i = 1; i < _requests.Count; i++)
        {
            if (RequestComparer.Instance.Compare(_requests[worstIndex], _requests[i]) < 0)
                worstIndex = i;
        }

        return worstIndex;
    }

    public void SolveAllocations()
    {
        using var sample = RuntimeEngine.Profiler.Start("ShadowAtlasManager.SolveAllocations");
        long solveStart = Stopwatch.GetTimestamp();
        _solveDiagnostics.Reset();

        ClassifyRequestsForSolve();

        for (int kind = 0; kind < AtlasKindCount; kind++)
            for (int encoding = 0; encoding < ShadowEncodingCount; encoding++)
                SolveAllocationsForState((EShadowAtlasKind)kind, (EShadowMapEncoding)encoding);

        BuildDirectionalCascadeGroups();
        BuildPointFaceGroups();

        _lastSolveDiagnostics = _solveDiagnostics.ToSnapshot(ElapsedMilliseconds(solveStart));
        RuntimeEngine.Rendering.Stats.RecordShadowAtlasSolveDiagnostics(_lastSolveDiagnostics);
        WarnIfSlowSolve(_lastSolveDiagnostics);
    }

    private void ClassifyRequestsForSolve()
    {
        for (int i = 0; i < _requestBuckets.Length; i++)
            _requestBuckets[i].Clear();

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            RecordClassifiedRequest(request);
            int bucketIndex = GetStateIndex(GetAtlasKind(request.ProjectionType), request.Encoding);
            _requestBuckets[bucketIndex].Add(request);
        }

        _requests.Clear();
        AppendSortedBucketsToRequestList(EShadowAtlasKind.Directional);
        AppendSortedBucketsToRequestList(EShadowAtlasKind.Spot);
        AppendSortedBucketsToRequestList(EShadowAtlasKind.Point);
    }

    private void AppendSortedBucketsToRequestList(EShadowAtlasKind atlasKind)
    {
        for (int encoding = 0; encoding < ShadowEncodingCount; encoding++)
        {
            List<ShadowMapRequest> bucket = _requestBuckets[GetStateIndex(atlasKind, (EShadowMapEncoding)encoding)];
            if (bucket.Count == 0)
                continue;

            bucket.Sort(_requestComparer);
            _requests.AddRange(bucket);
        }
    }

    private void RecordClassifiedRequest(ShadowMapRequest request)
    {
        _solveDiagnostics.ClassifiedRequestCount++;

        switch (GetAtlasKind(request.ProjectionType))
        {
            case EShadowAtlasKind.Directional:
                _solveDiagnostics.DirectionalRequestCount++;
                break;
            case EShadowAtlasKind.Spot:
                _solveDiagnostics.SpotRequestCount++;
                break;
            case EShadowAtlasKind.Point:
                _solveDiagnostics.PointRequestCount++;
                break;
        }

        switch (request.Encoding)
        {
            case EShadowMapEncoding.Depth:
                _solveDiagnostics.DepthRequestCount++;
                break;
            case EShadowMapEncoding.Variance2:
                _solveDiagnostics.Variance2RequestCount++;
                break;
            case EShadowMapEncoding.ExponentialVariance2:
                _solveDiagnostics.ExponentialVariance2RequestCount++;
                break;
            case EShadowMapEncoding.ExponentialVariance4:
                _solveDiagnostics.ExponentialVariance4RequestCount++;
                break;
        }
    }

    private void WarnIfSlowSolve(ShadowAtlasSolveDiagnostics diagnostics)
    {
        double thresholdMs = ResolveSlowSolveWarningThresholdMilliseconds(_settings);
        if (diagnostics.ElapsedMilliseconds < thresholdMs)
            return;

        XREngine.Debug.LightingWarningEvery(
            "ShadowAtlas.SolveAllocations.Slow",
            TimeSpan.FromSeconds(2.0),
            "[ShadowAtlas] Slow allocation solve: elapsedMs={0:F2}, thresholdMs={1:F2}, requests={2}, attempts={3}, failedCandidates={4}, demotions={5}, stickyDemotions={6}, directionalGroupDemotions={7}, fallbackDemotions={8}, pageAlloc={9}/{10}, pageCreates={11}/{12}, pageClears={13}, priorReserve={14}/{15}, priorSubBlock={16}/{17}, directionalGroups={18}/{19}/{20}, pointGroups={21}/{22}/{23}, lastFailure={24}.",
            diagnostics.ElapsedMilliseconds,
            thresholdMs,
            diagnostics.ClassifiedRequestCount,
            diagnostics.BalancedSolveAttemptCount,
            diagnostics.FailedCandidateCount,
            diagnostics.DemotionCount,
            diagnostics.StickyDemotionCount,
            diagnostics.DirectionalGroupDemotionCount,
            diagnostics.DeterministicFallbackDemotionCount,
            diagnostics.PageAllocationSuccessCount,
            diagnostics.PageAllocationAttemptCount,
            diagnostics.PageCreateSuccessCount,
            diagnostics.PageCreateAttemptCount,
            diagnostics.PageClearCount,
            diagnostics.PriorReserveHitCount,
            diagnostics.PriorReserveHitCount + diagnostics.PriorReserveMissCount,
            diagnostics.PriorSubBlockHitCount,
            diagnostics.PriorSubBlockHitCount + diagnostics.PriorSubBlockMissCount,
            diagnostics.DirectionalGroupSeedCount,
            diagnostics.DirectionalGroupMemberCount,
            diagnostics.DirectionalGroupCoLocationFailureCount,
            diagnostics.PointGroupSeedCount,
            diagnostics.PointGroupMemberCount,
            diagnostics.PointGroupCoLocationFailureCount,
            diagnostics.LastFailureReason);
    }

    private void WarnDeterministicFallbackDemotion(int demotionCount, int attempt, int maxAttempts, int entryCount)
    {
        XREngine.Debug.LightingWarningEvery(
            "ShadowAtlas.SolveAllocations.FallbackDemotion",
            TimeSpan.FromSeconds(2.0),
            "[ShadowAtlas] Allocation solve reached the attempt ceiling and applied deterministic fallback demotion: demotions={0}, attempt={1}, maxAttempts={2}, entries={3}, frame={4}.",
            demotionCount,
            attempt,
            maxAttempts,
            entryCount,
            _frameId);
    }

    private static double ResolveSlowSolveWarningThresholdMilliseconds(ShadowAtlasManagerSettings settings)
    {
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ShadowAtlasSolveWarnMs);
        if (!string.IsNullOrWhiteSpace(value) &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) &&
            threshold > 0.0)
        {
            return threshold;
        }

        return Math.Max(2.0, settings.MaxRenderMilliseconds);
    }

    private bool TryComparePriorPlacement(ShadowRequestKey x, ShadowRequestKey y, out int result)
    {
        bool hasX = TryGetPriorPlacement(x, out ShadowAtlasAllocation priorX);
        bool hasY = TryGetPriorPlacement(y, out ShadowAtlasAllocation priorY);
        if (!hasX && !hasY)
        {
            result = 0;
            return false;
        }

        if (hasX != hasY)
        {
            result = hasX ? -1 : 1;
            return true;
        }

        result = priorX.PageIndex.CompareTo(priorY.PageIndex);
        if (result != 0)
            return true;

        result = priorX.PixelRect.Y.CompareTo(priorY.PixelRect.Y);
        if (result != 0)
            return true;

        result = priorX.PixelRect.X.CompareTo(priorY.PixelRect.X);
        if (result != 0)
            return true;

        result = priorX.Resolution.CompareTo(priorY.Resolution);
        return true;
    }

    private bool TryGetPriorPlacement(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
    {
        if (TryGetResidentAllocation(key, out allocation) &&
            allocation.IsResident &&
            allocation.PageIndex >= 0 &&
            allocation.PixelRect.Width > 0 &&
            allocation.PixelRect.Height > 0)
        {
            return true;
        }

        allocation = default;
        return false;
    }

    private void SolveAllocationsForState(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
    {
        _balancedAllocationEntries.Clear();
        _pendingSkippedAllocations.Clear();
        List<ShadowMapRequest> bucket = _requestBuckets[GetStateIndex(atlasKind, encoding)];

        for (int i = 0; i < bucket.Count; i++)
        {
            ShadowMapRequest request = bucket[i];

            if (!request.Light.CastsShadows || !request.Light.IsActiveInHierarchy)
            {
                _frameAllocations.Add(CreateSkippedAllocation(request, SkipReason.DisabledByLight));
                continue;
            }

            if (request.ForcedSkipReason != SkipReason.None)
            {
                ShadowAtlasAllocation allocation = CreateSkippedAllocation(request, request.ForcedSkipReason);
                if (allocation.IsResident)
                {
                    _pendingSkippedAllocations.Add(new PendingSkippedAllocation(request, allocation));
                    continue;
                }

                PublishCurrentAllocation(request, allocation);
                continue;
            }

            uint desired = NormalizeTileResolution(
                request.DesiredResolution,
                Math.Max(request.MinimumResolution, _settings.MinTileResolution),
                Math.Min(_settings.MaxTileResolution, _settings.PageSize),
                _settings.PageSize);
            uint minimum = ResolveBalancedMinimumResolution(request, desired, _settings.PageSize);
            if (request.ProjectionType != EShadowProjectionType.DirectionalCascade)
                desired = ApplyLodHysteresis(request, desired, minimum);
            minimum = Math.Min(minimum, desired);

            _balancedAllocationEntries.Add(new BalancedAllocationEntry
            {
                Request = request,
                MinimumResolution = minimum,
                RelevanceScore = ResolveRelevanceScore(request),
                Resolution = desired,
            });
        }

        int entryCount = _balancedAllocationEntries.Count;
        ApplyDirectionalCascadeGroupResolutionCaps(entryCount);
        ShadowAtlasEncodingState state = GetEncodingState(atlasKind, encoding);
        if (entryCount == 0)
        {
            PublishPendingSkippedAllocations(state);
            return;
        }

        if (!TryBuildBalancedAllocations(state, entryCount, out SkipReason failureReason))
        {
            state.BeginFrame();
            for (int i = 0; i < entryCount; i++)
                PublishCurrentAllocation(_balancedAllocationEntries[i].Request, CreateSkippedAllocation(_balancedAllocationEntries[i].Request, failureReason));
            PublishPendingSkippedAllocations(state);
            return;
        }

        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            ShadowAtlasAllocation allocation = entry.Allocation;
            _currentAllocations[entry.Request.Key] = allocation;
            _currentAllocationIndices[entry.Request.Key] = _frameAllocations.Count;
            if (allocation.IsResident)
                UpdateLodState(entry.Request.Key, allocation.Resolution);
            _frameAllocations.Add(allocation);
        }

        PublishPendingSkippedAllocations(state);
    }

    private void PublishPendingSkippedAllocations(ShadowAtlasEncodingState state)
    {
        for (int i = 0; i < _pendingSkippedAllocations.Count; i++)
        {
            PendingSkippedAllocation pending = _pendingSkippedAllocations[i];
            ShadowAtlasAllocation allocation = pending.Allocation;
            if (TryReserveExistingAllocation(state, allocation))
            {
                PublishCurrentAllocation(pending.Request, allocation);
                continue;
            }

            PublishCurrentAllocation(
                pending.Request,
                CreateNonResidentSkippedAllocation(pending.Request, allocation.SkipReason));
        }

        _pendingSkippedAllocations.Clear();
    }

    private static bool TryReserveExistingAllocation(ShadowAtlasEncodingState state, in ShadowAtlasAllocation allocation)
    {
        if (!allocation.IsResident ||
            allocation.PageIndex < 0 ||
            allocation.Resolution == 0u ||
            allocation.PixelRect.Width <= 0 ||
            allocation.PixelRect.Height <= 0 ||
            allocation.AtlasKind != state.AtlasKind)
        {
            return false;
        }

        int size = checked((int)allocation.Resolution);
        return state.TryReserve(allocation.PageIndex, allocation.PixelRect.X, allocation.PixelRect.Y, size);
    }

    private void PublishCurrentAllocation(in ShadowMapRequest request, in ShadowAtlasAllocation allocation)
    {
        int allocationIndex = _frameAllocations.Count;
        if (allocation.IsResident)
        {
            _currentAllocations[request.Key] = allocation;
            _currentAllocationIndices[request.Key] = allocationIndex;
        }

        _frameAllocations.Add(allocation);
    }

    private void BuildDirectionalCascadeGroups()
    {
        _directionalCascadeGroups.Clear();
        ClearDirectionalCascadeGroupBuildMap();

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            if (request.ProjectionType != EShadowProjectionType.DirectionalCascade ||
                GetAtlasKind(request.ProjectionType) != EShadowAtlasKind.Directional)
            {
                continue;
            }

            if (!_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation allocation) ||
                !_currentAllocationIndices.TryGetValue(request.Key, out int recordIndex) ||
                !CanDirectionalCascadeJoinGroup(allocation))
            {
                _solveDiagnostics.DirectionalGroupCoLocationFailureCount++;
                continue;
            }

            DirectionalCascadeGroupKey key = new(
                request.Key.LightId,
                request.Key.Domain,
                request.Encoding,
                allocation.AtlasId,
                allocation.PageIndex);

            if (!_directionalCascadeGroupMembersByKey.TryGetValue(key, out List<ShadowAtlasGroupedAllocationMember>? members))
            {
                members = new List<ShadowAtlasGroupedAllocationMember>(4);
                _directionalCascadeGroupMembersByKey.Add(key, members);
            }

            if (members.Count == 0)
                _directionalCascadeGroupKeyScratch.Add(key);

            members.Add(new ShadowAtlasGroupedAllocationMember(
                request.FaceOrCascadeIndex,
                recordIndex,
                allocation.PixelRect,
                allocation.InnerPixelRect,
                ViewportScissorIndex: 0,
                allocation.UvScaleBias));
            _solveDiagnostics.DirectionalGroupMemberCount++;
        }

        for (int i = 0; i < _directionalCascadeGroupKeyScratch.Count; i++)
        {
            DirectionalCascadeGroupKey key = _directionalCascadeGroupKeyScratch[i];
            List<ShadowAtlasGroupedAllocationMember> members = _directionalCascadeGroupMembersByKey[key];
            _solveDiagnostics.DirectionalGroupSeedCount++;
            if (members.Count <= 1)
                continue;

            members.Sort(GroupedAllocationMemberComparer.Instance);
            ShadowAtlasGroupedAllocationMember[] publishedMembers = ShadowAtlasFrameData.RentGroupedMemberArray(members.Count);
            for (int memberIndex = 0; memberIndex < publishedMembers.Length; memberIndex++)
            {
                ShadowAtlasGroupedAllocationMember member = members[memberIndex];
                publishedMembers[memberIndex] = member with { ViewportScissorIndex = memberIndex };
            }

            _directionalCascadeGroups.Add(new ShadowAtlasGroupedDirectionalCascadeAllocation(
                key.LightId,
                key.Domain,
                key.Encoding,
                EShadowAtlasKind.Directional,
                key.AtlasId,
                key.PageIndex,
                publishedMembers.Length,
                publishedMembers));
        }
    }

    private void ClearDirectionalCascadeGroupBuildMap()
    {
        for (int i = 0; i < _directionalCascadeGroupKeyScratch.Count; i++)
            if (_directionalCascadeGroupMembersByKey.TryGetValue(_directionalCascadeGroupKeyScratch[i], out List<ShadowAtlasGroupedAllocationMember>? members))
                members.Clear();

        _directionalCascadeGroupKeyScratch.Clear();
    }

    private static bool CanDirectionalCascadeJoinGroup(ShadowAtlasAllocation allocation)
        => allocation.IsResident &&
            allocation.SkipReason == SkipReason.None &&
            allocation.AtlasKind == EShadowAtlasKind.Directional &&
            allocation.InnerPixelRect.Width > 0 &&
            allocation.InnerPixelRect.Height > 0;

    private void BuildPointFaceGroups()
    {
        _pointFaceGroups.Clear();
        ClearPointFaceGroupBuildMap();

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            if (request.ProjectionType != EShadowProjectionType.PointFace ||
                GetAtlasKind(request.ProjectionType) != EShadowAtlasKind.Point)
            {
                continue;
            }

            if (!_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation allocation) ||
                !_currentAllocationIndices.TryGetValue(request.Key, out int recordIndex) ||
                !CanPointFaceJoinGroup(allocation))
            {
                _solveDiagnostics.PointGroupCoLocationFailureCount++;
                continue;
            }

            PointFaceGroupKey key = new(
                request.Key.LightId,
                request.Key.Domain,
                request.Encoding,
                allocation.AtlasId,
                allocation.PageIndex);

            if (!_pointFaceGroupMembersByKey.TryGetValue(key, out List<ShadowAtlasGroupedAllocationMember>? members))
            {
                members = new List<ShadowAtlasGroupedAllocationMember>(6);
                _pointFaceGroupMembersByKey.Add(key, members);
            }

            if (members.Count == 0)
                _pointFaceGroupKeyScratch.Add(key);

            members.Add(new ShadowAtlasGroupedAllocationMember(
                request.FaceOrCascadeIndex,
                recordIndex,
                allocation.PixelRect,
                allocation.InnerPixelRect,
                ViewportScissorIndex: 0,
                allocation.UvScaleBias));
            _solveDiagnostics.PointGroupMemberCount++;
        }

        for (int i = 0; i < _pointFaceGroupKeyScratch.Count; i++)
        {
            PointFaceGroupKey key = _pointFaceGroupKeyScratch[i];
            List<ShadowAtlasGroupedAllocationMember> members = _pointFaceGroupMembersByKey[key];
            _solveDiagnostics.PointGroupSeedCount++;
            if (members.Count <= 1)
            {
                continue;
            }

            members.Sort(GroupedAllocationMemberComparer.Instance);
            ShadowAtlasGroupedAllocationMember[] publishedMembers = ShadowAtlasFrameData.RentGroupedMemberArray(members.Count);
            for (int memberIndex = 0; memberIndex < publishedMembers.Length; memberIndex++)
            {
                ShadowAtlasGroupedAllocationMember member = members[memberIndex];
                publishedMembers[memberIndex] = member with { ViewportScissorIndex = memberIndex };
            }

            _pointFaceGroups.Add(new ShadowAtlasGroupedPointFaceAllocation(
                key.LightId,
                key.Domain,
                key.Encoding,
                EShadowAtlasKind.Point,
                key.AtlasId,
                key.PageIndex,
                publishedMembers.Length,
                publishedMembers));
        }
    }

    private void ClearPointFaceGroupBuildMap()
    {
        for (int i = 0; i < _pointFaceGroupKeyScratch.Count; i++)
            if (_pointFaceGroupMembersByKey.TryGetValue(_pointFaceGroupKeyScratch[i], out List<ShadowAtlasGroupedAllocationMember>? members))
                members.Clear();

        _pointFaceGroupKeyScratch.Clear();
    }

    private static bool CanPointFaceJoinGroup(ShadowAtlasAllocation allocation)
        => allocation.IsResident &&
            allocation.SkipReason == SkipReason.None &&
            allocation.AtlasKind == EShadowAtlasKind.Point &&
            allocation.InnerPixelRect.Width > 0 &&
            allocation.InnerPixelRect.Height > 0;

    private bool TryBuildBalancedAllocations(
        ShadowAtlasEncodingState state,
        int entryCount,
        out SkipReason failureReason)
    {
        failureReason = SkipReason.None;
        int maxAttempts = CalculateMaxBalancedSolveAttempts(entryCount);
        bool fallbackDemotionApplied = false;
        int attempt = 0;

        while (true)
        {
            attempt++;
            _solveDiagnostics.BalancedSolveAttemptCount++;
            _solveDiagnostics.PageClearCount += state.PageCount;
            state.BeginFrame();
            ResetBalancedAllocationSolveState(entryCount);
            TryAllocateDirectionalCascadeGroups(state, entryCount);

            bool success = true;
            for (int i = 0; i < entryCount; i++)
            {
                BalancedAllocationEntry entry = _balancedAllocationEntries[i];
                if (entry.AllocationSolved)
                    continue;

                if (!TryAllocateCandidate(state, entry.Request, entry.Resolution, out ShadowAtlasAllocation allocation, out failureReason))
                {
                    _solveDiagnostics.FailedCandidateCount++;
                    RecordSolveFailureReason(failureReason == SkipReason.None ? state.LastFailureReason : failureReason);

                    if (entry.Resolution >= _settings.PageSize)
                    {
                        int fullPageDemotions = TryReduceAllocationsAtResolution(entryCount, entry.Resolution);
                        if (fullPageDemotions > 0)
                        {
                            success = false;
                            break;
                        }
                    }

                    if (!fallbackDemotionApplied && attempt >= maxAttempts)
                    {
                        int fallbackDemotions = ReduceDemotableAllocationsToMinimum(entryCount);
                        if (fallbackDemotions > 0)
                        {
                            _solveDiagnostics.DeterministicFallbackDemotionCount += fallbackDemotions;
                            fallbackDemotionApplied = true;
                            WarnDeterministicFallbackDemotion(fallbackDemotions, attempt, maxAttempts, entryCount);
                            success = false;
                            break;
                        }

                        fallbackDemotionApplied = true;
                    }

                    int demotions = TryReduceBalancedAllocations(entryCount, CalculateBalancedDemotionBatchSize(entryCount));
                    if (demotions > 0)
                    {
                        success = false;
                        break;
                    }

                    if (failureReason == SkipReason.None)
                        failureReason = state.LastFailureReason is SkipReason.None ? SkipReason.AllocationFailed : state.LastFailureReason;

                    entry.Allocation = CreateSkippedAllocation(entry.Request, failureReason);
                    entry.AllocationSolved = true;
                    _balancedAllocationEntries[i] = entry;
                    failureReason = SkipReason.None;
                    continue;
                }

                entry.Allocation = allocation;
                entry.AllocationSolved = true;
                _balancedAllocationEntries[i] = entry;
            }

            if (success)
                return true;
        }
    }

    private static int CalculateMaxBalancedSolveAttempts(int entryCount)
        => Math.Clamp((entryCount * 4) + 8, 8, 128);

    private static int CalculateBalancedDemotionBatchSize(int entryCount)
        => Math.Clamp(entryCount / 16, 1, 8);

    private int TryReduceBalancedAllocations(int entryCount, int maxDemotions)
    {
        int demotions = 0;
        while (demotions < maxDemotions && TryReduceBalancedAllocation(entryCount))
            demotions++;

        return demotions;
    }

    private int TryReduceAllocationsAtResolution(int entryCount, uint resolution)
    {
        int demotions = 0;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (entry.Resolution != resolution ||
                entry.Resolution <= entry.MinimumResolution ||
                entry.Request.EditorPinned)
            {
                continue;
            }

            if (TryReduceAllocationAtIndex(i, entryCount, out _))
                demotions++;
        }

        return demotions;
    }

    private bool TryReduceBalancedAllocation(int entryCount)
    {
        int selectedIndex = -1;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (entry.Resolution <= entry.MinimumResolution || entry.Request.EditorPinned)
                continue;

            if (selectedIndex < 0)
            {
                selectedIndex = i;
                continue;
            }

            BalancedAllocationEntry selected = _balancedAllocationEntries[selectedIndex];
            if (ShouldPreferDemotionCandidate(entry, selected))
                selectedIndex = i;
        }

        if (selectedIndex < 0)
            return false;

        int originalSelectedIndex = selectedIndex;
        selectedIndex = ApplyStickyDemotionTarget(selectedIndex, entryCount);
        if (selectedIndex != originalSelectedIndex)
            _solveDiagnostics.StickyDemotionCount++;

        return TryReduceAllocationAtIndex(selectedIndex, entryCount, out _);
    }

    private bool TryReduceAllocationAtIndex(int selectedIndex, int entryCount, out bool demotedSolvedAllocation)
    {
        demotedSolvedAllocation = false;
        if ((uint)selectedIndex >= (uint)entryCount)
            return false;

        BalancedAllocationEntry selectedEntry = _balancedAllocationEntries[selectedIndex];
        if (selectedEntry.Resolution <= selectedEntry.MinimumResolution || selectedEntry.Request.EditorPinned)
            return false;

        uint next = selectedEntry.Resolution > 1u ? selectedEntry.Resolution >> 1 : 1u;
        if (next < selectedEntry.MinimumResolution)
            next = selectedEntry.MinimumResolution;
        if (next == selectedEntry.Resolution)
            return false;

        if (selectedEntry.Request.ProjectionType == EShadowProjectionType.DirectionalCascade)
        {
            int changed = TryReduceDirectionalCascadeGroup(selectedIndex, next, entryCount, out demotedSolvedAllocation);
            if (changed <= 0)
                return false;

            _solveDiagnostics.DemotionCount += changed;
            _solveDiagnostics.DirectionalGroupDemotionCount += changed;
        }
        else
        {
            demotedSolvedAllocation = selectedEntry.AllocationSolved;
            selectedEntry.Resolution = next;
            _balancedAllocationEntries[selectedIndex] = selectedEntry;
            _demotionStates[selectedEntry.Request.Key] = new ShadowDemotionState(_frameId, selectedEntry.RelevanceScore);
            _solveDiagnostics.DemotionCount++;
        }

        return true;
    }

    private int TryReduceDirectionalCascadeGroup(
        int selectedIndex,
        uint targetResolution,
        int entryCount,
        out bool demotedSolvedAllocation)
    {
        demotedSolvedAllocation = false;
        BalancedAllocationEntry selectedEntry = _balancedAllocationEntries[selectedIndex];
        uint groupResolution = targetResolution;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (!IsSameDirectionalCascadeGroup(selectedEntry.Request, entry.Request))
                continue;

                groupResolution = Math.Max(groupResolution, entry.MinimumResolution);
        }

        int changed = 0;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (!IsSameDirectionalCascadeGroup(selectedEntry.Request, entry.Request))
                continue;

            uint next = Math.Min(entry.Resolution, groupResolution);
            if (next == entry.Resolution)
                continue;

            demotedSolvedAllocation |= entry.AllocationSolved;
            entry.Resolution = next;
            _balancedAllocationEntries[i] = entry;
            _demotionStates[entry.Request.Key] = new ShadowDemotionState(_frameId, entry.RelevanceScore);
            changed++;
        }

        return changed;
    }

    private int ReduceDemotableAllocationsToMinimum(int entryCount)
    {
        int changed = 0;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (entry.Request.EditorPinned || entry.Resolution <= entry.MinimumResolution)
                continue;

            entry.Resolution = entry.MinimumResolution;
            _balancedAllocationEntries[i] = entry;
            _demotionStates[entry.Request.Key] = new ShadowDemotionState(_frameId, entry.RelevanceScore);
            changed++;
        }

        _solveDiagnostics.DemotionCount += changed;
        return changed;
    }

    private void RecordSolveFailureReason(SkipReason reason)
    {
        if (reason != SkipReason.None)
            _solveDiagnostics.LastFailureReason = reason;
    }

    private static bool ShouldPreferDemotionCandidate(
        in BalancedAllocationEntry candidate,
        in BalancedAllocationEntry selected)
    {
        int result = candidate.RelevanceScore.CompareTo(selected.RelevanceScore);
        if (result != 0)
            return result < 0;

        result = candidate.Request.Priority.CompareTo(selected.Request.Priority);
        if (result != 0)
            return result < 0;

        return candidate.Request.Key.CompareTo(selected.Request.Key) < 0;
    }

    private int ApplyStickyDemotionTarget(int selectedIndex, int entryCount)
    {
        BalancedAllocationEntry selected = _balancedAllocationEntries[selectedIndex];
        int stickyIndex = -1;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (entry.Resolution <= entry.MinimumResolution || entry.Request.EditorPinned)
                continue;

            if (!_demotionStates.TryGetValue(entry.Request.Key, out ShadowDemotionState state))
                continue;

            ulong age = _frameId >= state.LastDemotedFrame
                ? _frameId - state.LastDemotedFrame
                : ulong.MaxValue;
            if (age > DemotionPromotionCooldownFrames)
                continue;

            if (stickyIndex < 0 ||
                ShouldPreferDemotionCandidate(entry, _balancedAllocationEntries[stickyIndex]))
            {
                stickyIndex = i;
            }
        }

        if (stickyIndex < 0 || stickyIndex == selectedIndex)
            return selectedIndex;

        BalancedAllocationEntry sticky = _balancedAllocationEntries[stickyIndex];
        float switchThreshold = sticky.RelevanceScore * (1.0f - DemotionSwitchMargin);
        if (selected.RelevanceScore < switchThreshold)
            return selectedIndex;

        return stickyIndex;
    }

    private static float ResolveRelevanceScore(ShadowMapRequest request)
        => MathF.Max(0.0f, request.Priority);

    private static uint ResolveBalancedMinimumResolution(ShadowMapRequest request, uint desired, uint pageSize)
    {
        if (GetAtlasKind(request.ProjectionType) == EShadowAtlasKind.Directional)
            return NormalizeTileResolution(request.MinimumResolution, 1u, desired, pageSize);

        return NormalizeTileResolution(request.MinimumResolution, 1u, desired, pageSize);
    }

    private void ApplyDirectionalCascadeGroupResolutionCaps(int entryCount)
    {
        for (int i = 0; i < entryCount; i++)
        {
            ShadowMapRequest seed = _balancedAllocationEntries[i].Request;
            if (seed.ProjectionType != EShadowProjectionType.DirectionalCascade ||
                HasEarlierDirectionalCascadeGroup(seed, i))
            {
                continue;
            }

            int cascadeCount = CountDirectionalCascadeGroupMembers(seed, entryCount);
            if (cascadeCount <= 1)
                continue;

            uint fairResolution = CalculateDirectionalCascadeGroupResolutionCap(cascadeCount);
            for (int j = i; j < entryCount; j++)
            {
                BalancedAllocationEntry entry = _balancedAllocationEntries[j];
                if (!IsSameDirectionalCascadeGroup(seed, entry.Request))
                    continue;

                uint cappedResolution = Math.Min(entry.Resolution, fairResolution);
                uint cappedMinimum = Math.Min(entry.MinimumResolution, cappedResolution);
                _balancedAllocationEntries[j] = entry with
                {
                    Resolution = cappedResolution,
                    MinimumResolution = cappedMinimum,
                };
            }
        }
    }

    private bool HasEarlierDirectionalCascadeGroup(ShadowMapRequest request, int currentIndex)
    {
        for (int i = 0; i < currentIndex; i++)
        {
            if (IsSameDirectionalCascadeGroup(request, _balancedAllocationEntries[i].Request))
                return true;
        }

        return false;
    }

    private int CountDirectionalCascadeGroupMembers(ShadowMapRequest request, int entryCount)
    {
        int count = 0;
        for (int i = 0; i < entryCount; i++)
            if (IsSameDirectionalCascadeGroup(request, _balancedAllocationEntries[i].Request))
                count++;

        return count;
    }

    private static bool IsSameDirectionalCascadeGroup(ShadowMapRequest x, ShadowMapRequest y)
        => y.ProjectionType == EShadowProjectionType.DirectionalCascade &&
           x.Key.LightId == y.Key.LightId &&
           x.Key.Domain == y.Key.Domain &&
           x.Encoding == y.Encoding;

    private uint CalculateDirectionalCascadeGroupResolutionCap(int cascadeCount)
    {
        int tilesPerAxis = 1;
        while (tilesPerAxis * tilesPerAxis < cascadeCount)
            tilesPerAxis++;

        uint maxByGrid = Math.Max(1u, _settings.PageSize / (uint)tilesPerAxis);
        return LargestPowerOfTwoAtMost(maxByGrid);
    }

    private static uint LargestPowerOfTwoAtMost(uint value)
    {
        value = Math.Max(1u, value);
        uint result = 1u;
        while (result <= value / 2u)
            result <<= 1;
        return result;
    }

    public int RenderScheduledTiles(bool collectVisibleNow = false)
    {
        long frameStart = Stopwatch.GetTimestamp();
        int budget = Math.Max(0, _settings.MaxTilesRenderedPerFrame);
        long startTimestamp = _settings.MaxRenderMilliseconds > 0.0f
            ? Stopwatch.GetTimestamp()
            : 0L;
        int scheduled = 0;
        int checkedTiles = 0;
        int skippedClean = 0;
        int failedRender = 0;
        int deferredByBudget = 0;
        int deferredByTexture = 0;
        int firstDeferredRequestIndex = -1;

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            if (request.ForcedSkipReason != SkipReason.None)
            {
                skippedClean++;
                continue;
            }

            bool criticalDirectionalRefresh = ShouldRenderDirectionalRefreshPastBudget(request);
            bool criticalRefreshPastTimeBudget = criticalDirectionalRefresh || ShouldRenderRefreshPastTimeBudget(request);
            if (scheduled >= budget && !(budget > 0 && criticalDirectionalRefresh))
            {
                deferredByBudget = _requests.Count - i;
                firstDeferredRequestIndex = i;
                break;
            }

            if (scheduled > 0 &&
                !criticalRefreshPastTimeBudget &&
                HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds))
            {
                deferredByBudget = _requests.Count - i;
                firstDeferredRequestIndex = i;
                break;
            }

            if (!_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation allocation))
            {
                LogDirectionalRequestRenderState(request, default, "NoCurrentAllocation", requiresRender: false);
                skippedClean++;
                continue;
            }

            if (TryGetDirectionalCascadeGroupContainingRequest(request, out ShadowAtlasGroupedDirectionalCascadeAllocation group) &&
                TryGetDirectionalCascadeGroupRenderRequirement(group, out bool requiresGroupedRender))
            {
                if (requiresGroupedRender &&
                    !criticalDirectionalRefresh &&
                    RenderWorkBudgetCoordinator.ShouldDeferShadowAtlasLowPriorityTile(request.Priority, request.EditorPinned))
                {
                    deferredByTexture = _requests.Count - i;
                    firstDeferredRequestIndex = i;
                    break;
                }

                bool canRenderGroupedNow =
                    requiresGroupedRender &&
                    (CanRenderGroupedTileSet(scheduled, budget, group.CascadeCount) || (budget > 0 && criticalDirectionalRefresh));
                if (canRenderGroupedNow)
                {
                    bool groupedSucceeded = TryRenderDirectionalCascadeGroup(
                        request,
                        group,
                        collectVisibleNow,
                        out double groupedElapsedMs,
                        out bool usedSequentialFallback);
                    RecordDirectionalGroupedRenderEvent(request, group, groupedElapsedMs, groupedSucceeded, usedSequentialFallback, criticalDirectionalRefresh);
                    if (groupedSucceeded)
                    {
                        int lastGroupRequestIndex = FindLastDirectionalCascadeGroupRequestIndex(group, i);
                        scheduled += group.CascadeCount;
                        checkedTiles += group.CascadeCount;

                        if (HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds) &&
                            !ShouldRenderRefreshPastTimeBudgetAtIndex(lastGroupRequestIndex + 1))
                        {
                            int nextRequestIndex = lastGroupRequestIndex + 1;
                            deferredByBudget = _requests.Count - nextRequestIndex;
                            firstDeferredRequestIndex = nextRequestIndex;
                            break;
                        }

                        i = lastGroupRequestIndex;
                        continue;
                    }

                    checkedTiles += group.CascadeCount;
                    failedRender += group.CascadeCount;
                    deferredByBudget = _requests.Count - i;
                    firstDeferredRequestIndex = i;
                    break;
                }
                else if (requiresGroupedRender)
                {
                    deferredByBudget = _requests.Count - i;
                    firstDeferredRequestIndex = i;
                    break;
                }
            }

            if (TryGetFirstPointFaceGroup(request, out ShadowAtlasGroupedPointFaceAllocation pointGroup) &&
                TryGetPointFaceGroupRenderRequirement(pointGroup, out bool requiresGroupedPointRender))
            {
                if (requiresGroupedPointRender &&
                    RenderWorkBudgetCoordinator.ShouldDeferShadowAtlasLowPriorityTile(request.Priority, request.EditorPinned))
                {
                    deferredByTexture = _requests.Count - i;
                    firstDeferredRequestIndex = i;
                    break;
                }

                if (requiresGroupedPointRender &&
                    CanRenderGroupedTileSet(scheduled, budget, pointGroup.FaceCount) &&
                    TryRenderPointFaceGroup(request, pointGroup, collectVisibleNow))
                {
                    scheduled += pointGroup.FaceCount;
                    checkedTiles += pointGroup.FaceCount;

                    if (HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds) &&
                        !ShouldRenderRefreshPastTimeBudgetAtIndex(i + 1))
                    {
                        int nextRequestIndex = i + 1;
                        deferredByBudget = _requests.Count - nextRequestIndex;
                        firstDeferredRequestIndex = nextRequestIndex;
                        break;
                    }

                    continue;
                }
            }

            bool requiresRender = RequiresTileRender(request, allocation);
            if (!requiresRender)
            {
                LogDirectionalRequestRenderState(request, allocation, "SkippedClean", requiresRender: false);
                skippedClean++;
                continue;
            }

            if (!criticalDirectionalRefresh &&
                RenderWorkBudgetCoordinator.ShouldDeferShadowAtlasLowPriorityTile(request.Priority, request.EditorPinned))
            {
                deferredByTexture = _requests.Count - i;
                firstDeferredRequestIndex = i;
                break;
            }

            bool forceCollectVisible = collectVisibleNow;
            checkedTiles++;

            if (!TryRenderTile(request, allocation, forceCollectVisible, out double tileElapsedMs))
            {
                LogDirectionalRequestRenderState(request, allocation, "RenderFailed", requiresRender: true);
                failedRender++;
                continue;
            }

            RecordDirectionalTileRenderEvent(request, tileElapsedMs, criticalDirectionalRefresh);
            MarkTileRendered(request, allocation);
            if (_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation renderedAllocation))
                LogDirectionalRequestRenderState(request, renderedAllocation, "Rendered", requiresRender: true);
            scheduled++;

            if (HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds) &&
                !ShouldRenderRefreshPastTimeBudgetAtIndex(i + 1))
            {
                int nextRequestIndex = i + 1;
                deferredByBudget = _requests.Count - nextRequestIndex;
                firstDeferredRequestIndex = nextRequestIndex;
                break;
            }
        }

        _tilesScheduledThisFrame = scheduled;
        double elapsedMs = ElapsedMilliseconds(frameStart);
        int deferredTotal = deferredByBudget + deferredByTexture;
        RenderWorkBudgetCoordinator.RecordShadowAtlasQueue(deferredTotal);
        RenderWorkBudgetCoordinator.RecordCompleted(RenderWorkSubsystem.ShadowAtlas, elapsedMs);
        if (deferredByTexture > 0)
        {
            TextureRuntimeDiagnostics.LogRenderWorkBudget(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                "Shadow.DelayedByTexture",
                RenderWorkBudgetCoordinator.GetSnapshot(),
                "urgent visible texture repair deferred low-priority shadow tiles");
        }

        if (deferredTotal > 0)
        {
            RenderWorkBudgetSnapshot budgetSnapshot = RenderWorkBudgetCoordinator.GetSnapshot();
            if (deferredByBudget > 0 && budgetSnapshot.TextureUploadQueueDepth > 0)
            {
                TextureRuntimeDiagnostics.LogRenderWorkBudget(
                    RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                    "Texture.DelayedByShadow",
                    budgetSnapshot,
                    "shadow atlas render budget deferred while texture uploads were queued");
            }

            XREngine.Debug.LightingEvery(
                "ShadowAtlas.RenderBudget.Deferred",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Deferred {0} shadow request(s) after rendering {1}/{2} tile(s) in {3:F2}ms (budget {4:F2}ms, textureDeferred={5}, firstDeferredIndex={6}).",
                deferredTotal,
                scheduled,
                budget,
                elapsedMs,
                _settings.MaxRenderMilliseconds,
                deferredByTexture,
                firstDeferredRequestIndex);
        }

        double slowThresholdMs = Math.Max(16.0, _settings.MaxRenderMilliseconds * 4.0);
        if (scheduled > 0 && elapsedMs > slowThresholdMs)
        {
            XREngine.Debug.LightingWarningEvery(
                "ShadowAtlas.RenderScheduledTiles.Slow",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Shadow tile rendering exceeded its frame budget: rendered={0}, checked={1}, skippedClean={2}, failed={3}, deferred={4}, elapsedMs={5:F2}, budgetMs={6:F2}.",
                scheduled,
                checkedTiles,
                skippedClean,
                failedRender,
                deferredTotal,
                elapsedMs,
                _settings.MaxRenderMilliseconds);
        }

        LogShadowAtlasRenderSummary(
            scheduled,
            checkedTiles,
            skippedClean,
            failedRender,
            deferredTotal,
            firstDeferredRequestIndex,
            elapsedMs,
            budget);

        return scheduled;
    }

    private bool ShouldRenderDirectionalRefreshPastBudgetAtIndex(int requestIndex)
    {
        if ((uint)requestIndex >= (uint)_requests.Count)
            return false;

        return ShouldRenderDirectionalRefreshPastBudget(_requests[requestIndex]);
    }

    private bool ShouldRenderRefreshPastTimeBudgetAtIndex(int requestIndex)
    {
        if ((uint)requestIndex >= (uint)_requests.Count)
            return false;

        return ShouldRenderRefreshPastTimeBudget(_requests[requestIndex]);
    }

    private static bool ShouldRenderDirectionalRefreshPastBudget(ShadowMapRequest request)
    {
        if (!IsDirectionalRequest(request) || !request.IsDirty)
            return false;

        return HasHardBudgetBypassDirtyReason(request);
    }

    private static bool ShouldRenderFreshTileBeforeStale(ShadowMapRequest request)
    {
        if (!request.IsDirty)
            return false;

        if (IsDirectionalRequest(request))
            return HasCriticalDirtyReason(request);

        return request.ProjectionType is EShadowProjectionType.PointFace or EShadowProjectionType.SpotPrimary &&
            (request.DirtyReason & ShadowDirtyReason.ProjectionOrCameraFitChanged) != 0;
    }

    /// <summary>
    /// Local lights (spot/point face) get the same soft-time-budget bypass as directional
    /// when they are dirty for an interactive reason: this prevents single-frame editor
    /// transforms (drags, gizmo updates, dynamic light movement) from being deferred when
    /// directional cascades happened to consume the time budget first. The hard tile
    /// budget (`MaxTilesRenderedPerFrame`) still applies.
    /// </summary>
    private static bool ShouldRenderRefreshPastTimeBudget(ShadowMapRequest request)
    {
        if (!request.IsDirty)
            return false;

        if (IsDirectionalRequest(request))
            return HasHardBudgetBypassDirtyReason(request);

        return request.ProjectionType is EShadowProjectionType.PointFace or EShadowProjectionType.SpotPrimary
            && HasCriticalDirtyReason(request);
    }

    private static bool HasHardBudgetBypassDirtyReason(ShadowMapRequest request)
    {
        const ShadowDirtyReason noUsableTileReasons =
            ShadowDirtyReason.FirstSubmission |
            ShadowDirtyReason.AllocationMissing |
            ShadowDirtyReason.NeverRendered;

        return (request.DirtyReason & noUsableTileReasons) != 0;
    }

    private static bool HasCriticalDirtyReason(ShadowMapRequest request)
    {
        const ShadowDirtyReason criticalReasons =
            ShadowDirtyReason.FirstSubmission |
            ShadowDirtyReason.ContentChanged |
            ShadowDirtyReason.ProjectionOrCameraFitChanged |
            ShadowDirtyReason.DynamicLight |
            ShadowDirtyReason.ReuseDisabled |
            ShadowDirtyReason.NeverRendered;

        return (request.DirtyReason & criticalReasons) != 0;
    }

    private static bool HasRenderBudgetExpired(long startTimestamp, float maxRenderMilliseconds)
    {
        if (startTimestamp == 0L || maxRenderMilliseconds <= 0.0f)
            return false;

        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        return elapsedMilliseconds >= maxRenderMilliseconds;
    }

    private static bool CanRenderGroupedTileSet(int scheduled, int budget, int groupedTileCount)
    {
        if (groupedTileCount <= 0 || budget <= 0)
            return false;

        return groupedTileCount <= budget - scheduled;
    }

    private static double ElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private static string LightName(LightComponent light)
        => light.SceneNode?.Name ?? light.Name ?? light.GetType().Name;

    public void PublishFrameData()
    {
        using var sample = RuntimeEngine.Profiler.Start("ShadowAtlasManager.PublishFrameData");

        bool layoutChanged = HasLayoutChanged();
        if (layoutChanged)
            unchecked { _generation++; }

        BuildPageDescriptors();
        BuildDirectionalAtlasLightDiagnostics();
        ShadowAtlasMetrics metrics = BuildMetrics();
        int writeIndex = 1 - _publishedFrameIndex;
        _frameBuffers[writeIndex].SetData(
            _frameId,
            _generation,
            _frameAllocations,
            _directionalCascadeGroups,
            _pointFaceGroups,
            _directionalAtlasLightDiagnostics,
            _pageDescriptors,
            metrics,
            _lastSolveDiagnostics);
        _publishedFrameIndex = writeIndex;

        UpdateResidentAllocations();
        _previousAllocations.Clear();
        foreach (var pair in _currentAllocations)
            _previousAllocations[pair.Key] = pair.Value;

        TrimResidentAllocations();
        TrimDemotionStates();
        _repackRequested = false;
    }

    private void UpdateResidentAllocations()
    {
        foreach (var pair in _currentAllocations)
        {
            ShadowAtlasAllocation allocation = pair.Value;
            if (!IsUsableResidentAllocation(allocation))
                continue;

            RemoveOverlappingResidentAllocations(pair.Key, allocation);
            _residentAllocations[pair.Key] = new ShadowResidentEntry(allocation, _frameId);
        }
    }

    private void RemoveOverlappingResidentAllocations(ShadowRequestKey key, in ShadowAtlasAllocation allocation)
    {
        _residentRemovalScratch.Clear();
        foreach (var pair in _residentAllocations)
        {
            if (pair.Key == key)
                continue;

            ShadowAtlasAllocation resident = pair.Value.Allocation;
            if (resident.AtlasId != allocation.AtlasId ||
                resident.PageIndex != allocation.PageIndex ||
                !RectanglesOverlap(resident.PixelRect, allocation.PixelRect))
            {
                continue;
            }

            _residentRemovalScratch.Add(pair.Key);
        }

        for (int i = 0; i < _residentRemovalScratch.Count; i++)
            _residentAllocations.Remove(_residentRemovalScratch[i]);
    }

    private void TrimResidentAllocations()
    {
        _residentRemovalScratch.Clear();
        bool overCapacity = _residentAllocations.Count > _settings.MaxRequestsPerFrame;
        foreach (var pair in _residentAllocations)
        {
            ulong age = _frameId >= pair.Value.LastRequestedFrame
                ? _frameId - pair.Value.LastRequestedFrame
                : ulong.MaxValue;
            if (age > ResidentEvictionTtlFrames ||
                (overCapacity && !_currentAllocations.ContainsKey(pair.Key)) ||
                !IsUsableResidentAllocation(pair.Value.Allocation))
            {
                _residentRemovalScratch.Add(pair.Key);
            }
        }

        for (int i = 0; i < _residentRemovalScratch.Count; i++)
            _residentAllocations.Remove(_residentRemovalScratch[i]);
    }

    private static bool IsUsableResidentAllocation(in ShadowAtlasAllocation allocation)
        => allocation.IsResident &&
           allocation.PageIndex >= 0 &&
           allocation.Resolution > 0u &&
           allocation.PixelRect.Width > 0 &&
           allocation.PixelRect.Height > 0;

    private bool TryGetResidentAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
    {
        if (_residentAllocations.TryGetValue(key, out ShadowResidentEntry resident) &&
            IsUsableResidentAllocation(resident.Allocation))
        {
            allocation = resident.Allocation;
            return true;
        }

        if (_previousAllocations.TryGetValue(key, out allocation) &&
            IsUsableResidentAllocation(allocation))
        {
            return true;
        }

        allocation = default;
        return false;
    }

    private void TrimDemotionStates()
    {
        const ulong maxAgeFrames = DemotionPromotionCooldownFrames * 4u;
        _demotionRemovalScratch.Clear();
        foreach (var pair in _demotionStates)
        {
            ulong age = _frameId >= pair.Value.LastDemotedFrame
                ? _frameId - pair.Value.LastDemotedFrame
                : ulong.MaxValue;
            if (age > maxAgeFrames || !_residentAllocations.ContainsKey(pair.Key))
                _demotionRemovalScratch.Add(pair.Key);
        }

        for (int i = 0; i < _demotionRemovalScratch.Count; i++)
            _demotionStates.Remove(_demotionRemovalScratch[i]);
    }

    public bool TryGetAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
        => PublishedFrameData.TryGetAllocation(key, out allocation);

    public bool TryGetPageTexture(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding, int pageIndex, out XRTexture2DArray texture)
    {
        if (GetEncodingState(atlasKind, encoding).TryGetPageResource(pageIndex, out ShadowAtlasPageResource? resource) &&
            resource is not null)
        {
            texture = ShouldSampleRasterDepth(atlasKind, encoding)
                ? resource.RasterDepthTexture
                : resource.Texture;
            return true;
        }

        texture = null!;
        return false;
    }

    private static bool ShouldSampleRasterDepth(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
        => atlasKind == EShadowAtlasKind.Directional &&
           encoding == EShadowMapEncoding.Depth;

    public bool TryGetPageRasterDepthTexture(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding, int pageIndex, out XRTexture2DArray texture)
    {
        if (GetEncodingState(atlasKind, encoding).TryGetPageResource(pageIndex, out ShadowAtlasPageResource? resource) &&
            resource is not null)
        {
            texture = resource.RasterDepthTexture;
            return true;
        }

        texture = null!;
        return false;
    }

    public void Reset()
    {
        _requests.Clear();
        for (int i = 0; i < _requestBuckets.Length; i++)
            _requestBuckets[i].Clear();
        _frameAllocations.Clear();
        _directionalCascadeGroups.Clear();
        ClearDirectionalCascadeGroupBuildMap();
        _pointFaceGroups.Clear();
        ClearPointFaceGroupBuildMap();
        _pageDescriptors.Clear();
        _previousAllocations.Clear();
        _currentAllocations.Clear();
        _currentAllocationIndices.Clear();
        _residentAllocations.Clear();
        _residentRemovalScratch.Clear();
        _lodStates.Clear();
        _demotionStates.Clear();
        _demotionRemovalScratch.Clear();
        _pendingSkippedAllocations.Clear();
        _directionalGroupReservationFailures.Clear();
        _solveDiagnostics.Reset();
        _lastSolveDiagnostics = default;
        _queueOverflowCount = 0;
        _tilesScheduledThisFrame = 0;
        _fallbackFrameId = 0u;
        _generation = 0;
        _repackRequested = false;
        ResetResources();
    }

    public void RequestRepack()
        => _repackRequested = true;

    public static uint NormalizeTileResolution(uint requested, uint minimum, uint maximum, uint pageSize)
    {
        uint min = ClampPowerOfTwo(Math.Max(1u, minimum), pageSize);
        uint max = ClampPowerOfTwo(Math.Max(min, maximum), pageSize);
        uint value = ClampPowerOfTwo(Math.Max(min, requested), pageSize);
        if (value > max)
            value = max;
        if (value < min)
            value = min;
        return value;
    }

    private bool TryAllocateCandidate(
        ShadowAtlasEncodingState state,
        ShadowMapRequest request,
        uint candidate,
        out ShadowAtlasAllocation allocation,
        out SkipReason skipReason)
    {
        int size = checked((int)candidate);
        ShadowAtlasAllocation? previous = TryGetResidentAllocation(request.Key, out ShadowAtlasAllocation previousAllocation)
            ? previousAllocation
            : null;
        if (!_repackRequested &&
            previous is ShadowAtlasAllocation prior &&
            prior.IsResident &&
            prior.AtlasKind == state.AtlasKind)
        {
            if (prior.Resolution == candidate &&
                state.TryReserve(prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, size))
            {
                _solveDiagnostics.PriorReserveHitCount++;
                allocation = CreateResidentAllocation(request, state.AtlasKind, prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, candidate);
                skipReason = SkipReason.None;
                return true;
            }
            else if (prior.Resolution == candidate)
            {
                _solveDiagnostics.PriorReserveMissCount++;
            }

            if (candidate < prior.Resolution &&
                state.TryReserveAlignedSubBlock(
                    prior.PageIndex,
                    prior.PixelRect.X,
                    prior.PixelRect.Y,
                    size,
                    out int shrinkX,
                    out int shrinkY))
            {
                _solveDiagnostics.PriorSubBlockHitCount++;
                allocation = CreateResidentAllocation(request, state.AtlasKind, prior.PageIndex, shrinkX, shrinkY, candidate);
                skipReason = SkipReason.None;
                return true;
            }
            else if (candidate < prior.Resolution)
            {
                _solveDiagnostics.PriorSubBlockMissCount++;
            }

            if (candidate > prior.Resolution)
            {
                if (state.TryReserveAlignedSubBlock(
                    prior.PageIndex,
                    prior.PixelRect.X,
                    prior.PixelRect.Y,
                    size,
                    out int upgradeX,
                    out int upgradeY))
                {
                    _solveDiagnostics.PriorSubBlockHitCount++;
                    allocation = CreateResidentAllocation(request, state.AtlasKind, prior.PageIndex, upgradeX, upgradeY, candidate);
                    skipReason = SkipReason.None;
                    return true;
                }
                _solveDiagnostics.PriorSubBlockMissCount++;

                int priorSize = checked((int)prior.Resolution);
                if (state.TryReserve(prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, priorSize))
                {
                    _solveDiagnostics.PriorReserveHitCount++;
                    allocation = CreateResidentAllocation(request, state.AtlasKind, prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, prior.Resolution);
                    skipReason = SkipReason.None;
                    return true;
                }
                _solveDiagnostics.PriorReserveMissCount++;
            }
        }

        int pageCountBefore = state.PageCount;
        _solveDiagnostics.PageAllocationAttemptCount++;
        if (state.TryAllocate(size, _settings, CurrentResidentBytes(), out int pageIndex, out int x, out int y, out skipReason))
        {
            _solveDiagnostics.PageAllocationSuccessCount++;
            if (state.PageCount > pageCountBefore)
            {
                _solveDiagnostics.PageCreateAttemptCount++;
                _solveDiagnostics.PageCreateSuccessCount++;
            }

            allocation = CreateResidentAllocation(request, state.AtlasKind, pageIndex, x, y, candidate);
            return true;
        }

        RecordSolveFailureReason(skipReason);
        allocation = default;
        return false;
    }

    private ShadowAtlasAllocation CreateResidentAllocation(ShadowMapRequest request, EShadowAtlasKind atlasKind, int pageIndex, int x, int y, uint resolution)
    {
        int size = checked((int)resolution);
        int gutter = Math.Min(CalculateGutterTexels(request), Math.Max(0, (size - 1) / 2));
        BoundingRectangle pixelRect = new(x, y, size, size);
        BoundingRectangle innerRect = new(x + gutter, y + gutter, size - (gutter * 2), size - (gutter * 2));
        Vector4 uv = CreateAtlasUvScaleBias(innerRect);
        int lod = CalculateLodLevel(resolution, _settings.PageSize);
        int atlasId = GetAtlasId(atlasKind, request.Encoding);
        ulong lastRendered = 0u;
        ulong contentVersion = request.ContentHash;
        if (TryGetResidentAllocation(request.Key, out ShadowAtlasAllocation prior) &&
            prior.IsResident &&
            prior.AtlasKind == atlasKind &&
            prior.AtlasId == atlasId &&
            prior.PageIndex == pageIndex &&
            (IsSameRegion(prior, x, y, resolution) || IsContainedInPriorRegion(prior, x, y, resolution)))
        {
            lastRendered = prior.LastRenderedFrame;
            contentVersion = prior.ContentVersion;
        }

        bool requiresFreshRender = request.IsDirty || !request.CanReusePreviousFrame;
        bool hasRenderedTile = lastRendered != 0u;
        bool fallbackCanUseStaleTile = request.Fallback == ShadowFallbackMode.StaleTile;
        bool shouldRefreshBeforeStale = ShouldRenderFreshTileBeforeStale(request);
        bool allowStaleTile = fallbackCanUseStaleTile && !shouldRefreshBeforeStale;
        bool keepDirectionalStaleTileUntilRefresh = fallbackCanUseStaleTile && IsDirectionalRequest(request);
        bool reuseStaleTile = hasRenderedTile && requiresFreshRender && allowStaleTile;
        ShadowFallbackMode activeFallback = ShadowFallbackMode.None;
        SkipReason skipReason = SkipReason.None;

        if (hasRenderedTile && requiresFreshRender)
        {
            if (reuseStaleTile || keepDirectionalStaleTileUntilRefresh)
            {
                activeFallback = ShadowFallbackMode.StaleTile;
                skipReason = SkipReason.StaleTileReused;
            }
            else
            {
                activeFallback = request.Fallback != ShadowFallbackMode.None
                    ? request.Fallback
                    : ShadowFallbackMode.Lit;
                if (activeFallback == ShadowFallbackMode.StaleTile && !allowStaleTile)
                    activeFallback = ShadowFallbackMode.Lit;
            }
        }
        else if (!hasRenderedTile && request.Fallback != ShadowFallbackMode.None)
        {
            activeFallback = request.Fallback;
            if (activeFallback == ShadowFallbackMode.StaleTile && !allowStaleTile)
                activeFallback = ShadowFallbackMode.Lit;
        }

        if (!(hasRenderedTile && requiresFreshRender))
            contentVersion = request.ContentHash;

        return new ShadowAtlasAllocation(
            request.Key,
            AtlasKind: atlasKind,
            AtlasId: atlasId,
            PageIndex: pageIndex,
            PixelRect: pixelRect,
            InnerPixelRect: innerRect,
            UvScaleBias: uv,
            Resolution: resolution,
            LodLevel: lod,
            ContentVersion: contentVersion,
            LastRenderedFrame: lastRendered,
            IsResident: true,
            IsStaticCacheBacked: false,
            ActiveFallback: activeFallback,
            SkipReason: skipReason);
    }

    private Vector4 CreateAtlasUvScaleBias(BoundingRectangle innerRect)
    {
        float invPageSize = 1.0f / _settings.PageSize;
        Vector4 uv = new(
            innerRect.Width / (float)_settings.PageSize,
            innerRect.Height / (float)_settings.PageSize,
            innerRect.X / (float)_settings.PageSize,
            innerRect.Y / (float)_settings.PageSize);

        if (RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.Current.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown)
            uv.W = 1.0f - ((innerRect.Y + innerRect.Height) * invPageSize);

        return uv;
    }

    private static bool IsSameRegion(in ShadowAtlasAllocation prior, int x, int y, uint resolution)
        => prior.PixelRect.X == x &&
           prior.PixelRect.Y == y &&
           prior.Resolution == resolution;

    private static bool IsContainedInPriorRegion(in ShadowAtlasAllocation prior, int x, int y, uint resolution)
    {
        int size = checked((int)resolution);
        return x >= prior.PixelRect.X &&
            y >= prior.PixelRect.Y &&
            x + size <= prior.PixelRect.X + prior.PixelRect.Width &&
            y + size <= prior.PixelRect.Y + prior.PixelRect.Height;
    }

    private void MarkTileRendered(ShadowMapRequest request, ShadowAtlasAllocation allocation)
    {
        ShadowAtlasAllocation rendered = allocation with
        {
            ContentVersion = request.ContentHash,
            LastRenderedFrame = _frameId,
            ActiveFallback = ShadowFallbackMode.None,
            SkipReason = SkipReason.None,
        };
        _currentAllocations[request.Key] = rendered;
        if (_currentAllocationIndices.TryGetValue(request.Key, out int index))
            _frameAllocations[index] = rendered;
    }

    private bool RequiresTileRender(ShadowMapRequest request, ShadowAtlasAllocation allocation)
    {
        if (allocation.LastRenderedFrame == 0u)
            return true;

        if (allocation.LastRenderedFrame == _frameId &&
            allocation.ContentVersion == request.ContentHash)
        {
            return false;
        }

        if (!request.CanReusePreviousFrame)
            return true;

        return RequiresTileRender(
            request,
            allocation.PageIndex,
            allocation.PixelRect.X,
            allocation.PixelRect.Y,
            allocation.Resolution);
    }

    private bool RequiresTileRender(ShadowMapRequest request, int pageIndex, int x, int y, uint resolution)
    {
        if (request.IsDirty)
            return true;

        if (!TryGetResidentAllocation(request.Key, out ShadowAtlasAllocation prior))
            return true;

        return prior.PageIndex != pageIndex ||
            prior.PixelRect.X != x ||
            prior.PixelRect.Y != y ||
            prior.Resolution != resolution ||
            prior.AtlasId != GetAtlasId(GetAtlasKind(request.ProjectionType), request.Encoding) ||
            prior.ContentVersion != request.ContentHash;
    }

    private bool TryGetDirectionalCascadeGroupContainingRequest(
        ShadowMapRequest request,
        out ShadowAtlasGroupedDirectionalCascadeAllocation group)
    {
        if (request.ProjectionType != EShadowProjectionType.DirectionalCascade)
        {
            group = default;
            return false;
        }

        for (int i = 0; i < _directionalCascadeGroups.Count; i++)
        {
            ShadowAtlasGroupedDirectionalCascadeAllocation candidate = _directionalCascadeGroups[i];
            if (candidate.LightId != request.Key.LightId ||
                candidate.Domain != request.Key.Domain ||
                candidate.Encoding != request.Encoding ||
                candidate.Members is null ||
                candidate.Members.Length == 0 ||
                !DirectionalCascadeGroupContainsCascade(candidate, request.FaceOrCascadeIndex))
            {
                continue;
            }

            group = candidate;
            return true;
        }

        group = default;
        return false;
    }

    private int FindLastDirectionalCascadeGroupRequestIndex(
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        int firstRequestIndex)
    {
        int lastRequestIndex = firstRequestIndex;
        for (int i = firstRequestIndex + 1; i < _requests.Count; i++)
        {
            ShadowMapRequest candidate = _requests[i];
            if (candidate.ProjectionType != EShadowProjectionType.DirectionalCascade ||
                candidate.Key.LightId != group.LightId ||
                candidate.Key.Domain != group.Domain ||
                candidate.Encoding != group.Encoding ||
                !DirectionalCascadeGroupContainsCascade(group, candidate.FaceOrCascadeIndex))
            {
                continue;
            }

            lastRequestIndex = i;
        }

        return lastRequestIndex;
    }

    private static bool DirectionalCascadeGroupContainsCascade(
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        int cascadeIndex)
    {
        if (group.Members is null)
            return false;

        int count = Math.Min(group.CascadeCount, group.Members.Length);
        for (int i = 0; i < count; i++)
            if (group.Members[i].CascadeIndex == cascadeIndex)
                return true;

        return false;
    }

    private bool TryGetDirectionalCascadeGroupRenderRequirement(
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        out bool requiresRender)
    {
        requiresRender = false;
        if (group.Members is null)
            return false;

        for (int i = 0; i < group.CascadeCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            if ((uint)member.RecordIndex >= (uint)_frameAllocations.Count)
                return false;

            ShadowAtlasAllocation allocation = _frameAllocations[member.RecordIndex];
            if (!TryFindRequest(allocation.Key, out ShadowMapRequest request))
                return false;

            if (RequiresTileRender(request, allocation))
                requiresRender = true;
        }

        return true;
    }

    private bool TryRenderDirectionalCascadeGroup(
        ShadowMapRequest seedRequest,
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        bool collectVisibleNow,
        out double elapsedMs,
        out bool usedSequentialFallback)
    {
        long start = Stopwatch.GetTimestamp();
        elapsedMs = 0.0;
        usedSequentialFallback = false;
        if (seedRequest.Light is not DirectionalLightComponent light ||
            !GetEncodingState(group.AtlasKind, group.Encoding).TryGetPageResource(group.PageIndex, out ShadowAtlasPageResource? page) ||
            page is null)
        {
            elapsedMs = ElapsedMilliseconds(start);
            return false;
        }

        if (!light.RenderGroupedCascadeShadowAtlasTiles(group, page.FrameBuffer, collectVisibleNow))
        {
            usedSequentialFallback = TryRenderDirectionalCascadeGroupSequentially(light, group, collectVisibleNow);
            if (!usedSequentialFallback)
            {
                elapsedMs = ElapsedMilliseconds(start);
                return false;
            }
        }

        for (int i = 0; i < group.CascadeCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            ShadowAtlasAllocation allocation = _frameAllocations[member.RecordIndex];
            if (TryFindRequest(allocation.Key, out ShadowMapRequest request))
                MarkTileRendered(request, allocation);
        }

        elapsedMs = ElapsedMilliseconds(start);
        double slowThresholdMs = Math.Max(16.0, _settings.MaxRenderMilliseconds * 4.0);
        if (elapsedMs > slowThresholdMs)
        {
            XREngine.Debug.LightingWarningEvery(
                $"ShadowAtlas.GroupedDirectionalCascade.Slow.{seedRequest.Key.LightId}",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Slow grouped directional cascade render: light='{0}', cascades={1}, page={2}, elapsedMs={3:F2}, frameBudgetMs={4:F2}.",
                LightName(seedRequest.Light),
                group.CascadeCount,
                group.PageIndex,
                elapsedMs,
                _settings.MaxRenderMilliseconds);
        }

        return true;
    }

    private bool TryRenderDirectionalCascadeGroupSequentially(
        DirectionalLightComponent light,
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        bool collectVisibleNow)
    {
        if (group.Members is null)
            return false;

        bool renderedAny = false;
        for (int i = 0; i < group.CascadeCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            if ((uint)member.RecordIndex >= (uint)_frameAllocations.Count)
                return false;

            ShadowAtlasAllocation allocation = _frameAllocations[member.RecordIndex];
            if (!TryFindRequest(allocation.Key, out ShadowMapRequest request) ||
                request.Light != light ||
                request.ProjectionType != EShadowProjectionType.DirectionalCascade)
            {
                return false;
            }

            if (!GetEncodingState(allocation.AtlasKind, request.Encoding).TryGetPageResource(allocation.PageIndex, out ShadowAtlasPageResource? page) ||
                page is null)
            {
                return false;
            }

            if (!light.RenderCascadeShadowAtlasTile(request.FaceOrCascadeIndex, page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow))
                return false;

            renderedAny = true;
        }

        return renderedAny;
    }

    private bool TryGetFirstPointFaceGroup(
        ShadowMapRequest request,
        out ShadowAtlasGroupedPointFaceAllocation group)
    {
        if (request.ProjectionType != EShadowProjectionType.PointFace)
        {
            group = default;
            return false;
        }

        for (int i = 0; i < _pointFaceGroups.Count; i++)
        {
            ShadowAtlasGroupedPointFaceAllocation candidate = _pointFaceGroups[i];
            if (candidate.LightId != request.Key.LightId ||
                candidate.Domain != request.Key.Domain ||
                candidate.Encoding != request.Encoding ||
                candidate.Members is null ||
                candidate.Members.Length == 0 ||
                candidate.Members[0].CascadeIndex != request.FaceOrCascadeIndex)
            {
                continue;
            }

            group = candidate;
            return true;
        }

        group = default;
        return false;
    }

    private bool TryGetPointFaceGroupRenderRequirement(
        in ShadowAtlasGroupedPointFaceAllocation group,
        out bool requiresRender)
    {
        requiresRender = false;
        if (group.Members is null)
            return false;

        for (int i = 0; i < group.FaceCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            if ((uint)member.RecordIndex >= (uint)_frameAllocations.Count)
                return false;

            ShadowAtlasAllocation allocation = _frameAllocations[member.RecordIndex];
            if (!TryFindRequest(allocation.Key, out ShadowMapRequest request))
                return false;

            if (RequiresTileRender(request, allocation))
                requiresRender = true;
        }

        return true;
    }

    private void RecordDirectionalGroupedRenderEvent(
        ShadowMapRequest seedRequest,
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        double elapsedMs,
        bool succeeded,
        bool usedSequentialFallback,
        bool criticalBudgetBypassUsed)
    {
        if (seedRequest.Light is not DirectionalLightComponent light)
            return;

        if (succeeded)
        {
            if (usedSequentialFallback)
                _directionalSequentialFallbackFrame = true;
            else
                _directionalGroupedFrame = true;
        }

        if (!succeeded)
        {
            XREngine.Debug.LightingWarningEvery(
                $"ShadowAtlas.GroupedDirectionalCascade.Failed.{seedRequest.Key.LightId}",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Grouped directional cascade render failed for '{0}', cascades={1}, page={2}; sequential fallback also failed, leaving atlas tiles stale.",
                LightName(light),
                group.CascadeCount,
                group.PageIndex);
        }

        if (!RenderDiagnosticsFlags.DirectionalShadowAudit)
            return;

        _directionalAtlasRenderEvents.Add(new DirectionalAtlasRenderEvent(
            LightId: seedRequest.Key.LightId,
            RenderedCascades: succeeded ? group.CascadeCount : 0,
            SequentialCascadeRender: usedSequentialFallback,
            GroupedRenderAttempted: true,
            GroupedRenderSucceeded: succeeded && !usedSequentialFallback,
            CriticalBudgetBypassUsed: criticalBudgetBypassUsed,
            SelectedCascadeBackend: light.EffectiveCascadeShadowRenderBackend,
            FallbackReason: usedSequentialFallback ? "GroupedAtlasRenderFailed" : light.CascadeShadowRenderFallbackReason,
            ElapsedShadowMilliseconds: elapsedMs));
    }

    private void RecordDirectionalTileRenderEvent(
        ShadowMapRequest request,
        double elapsedMs,
        bool criticalBudgetBypassUsed)
    {
        if (request.Light is not DirectionalLightComponent light)
            return;

        bool sequentialCascadeRender = request.ProjectionType == EShadowProjectionType.DirectionalCascade;
        if (sequentialCascadeRender)
        {
            _directionalSequentialFallbackFrame = true;
            WarnIfDirectionalAtlasSequentialFallbackHasLegacyLayeredPath(request, light);
        }

        if (!RenderDiagnosticsFlags.DirectionalShadowAudit)
            return;

        _directionalAtlasRenderEvents.Add(new DirectionalAtlasRenderEvent(
            LightId: request.Key.LightId,
            RenderedCascades: sequentialCascadeRender ? 1 : 0,
            SequentialCascadeRender: sequentialCascadeRender,
            GroupedRenderAttempted: false,
            GroupedRenderSucceeded: false,
            CriticalBudgetBypassUsed: criticalBudgetBypassUsed,
            SelectedCascadeBackend: light.EffectiveCascadeShadowRenderBackend,
            FallbackReason: light.CascadeShadowRenderFallbackReason,
            ElapsedShadowMilliseconds: elapsedMs));
    }

    private static void WarnIfDirectionalAtlasSequentialFallbackHasLegacyLayeredPath(
        ShadowMapRequest request,
        DirectionalLightComponent light)
    {
        int activeCascadeCount = light.ActiveCascadeCount;
        if (activeCascadeCount <= 1 ||
            string.Equals(light.CascadeShadowRenderFallbackReason, "SequentialRequested", StringComparison.Ordinal) ||
            !light.CanUseLegacyLayeredDirectionalCascadeShadowRendering(activeCascadeCount))
        {
            return;
        }

        XREngine.Debug.LightingWarningEvery(
            $"ShadowAtlas.Directional.SequentialFallback.LegacyLayeredAvailable.{request.Key.LightId}",
            TimeSpan.FromSeconds(2.0),
            "[ShadowAtlas] Directional atlas rendered '{0}' cascade {1} sequentially even though the legacy layered cascade path is available. activeCascades={2}, atlasBackend={3}, atlasFallback={4}.",
            LightName(light),
            request.FaceOrCascadeIndex,
            activeCascadeCount,
            light.EffectiveCascadeShadowRenderBackend,
            light.CascadeShadowRenderFallbackReason);
    }

    private bool TryRenderPointFaceGroup(
        ShadowMapRequest seedRequest,
        in ShadowAtlasGroupedPointFaceAllocation group,
        bool collectVisibleNow)
    {
        long start = Stopwatch.GetTimestamp();
        if (seedRequest.Light is not PointLightComponent light ||
            !GetEncodingState(group.AtlasKind, group.Encoding).TryGetPageResource(group.PageIndex, out ShadowAtlasPageResource? page) ||
            page is null)
        {
            return false;
        }

        if (!light.RenderGroupedShadowAtlasFaceTiles(group, page.FrameBuffer, collectVisibleNow))
            return false;

        for (int i = 0; i < group.FaceCount; i++)
        {
            ShadowAtlasGroupedAllocationMember member = group.Members[i];
            ShadowAtlasAllocation allocation = _frameAllocations[member.RecordIndex];
            if (TryFindRequest(allocation.Key, out ShadowMapRequest request))
                MarkTileRendered(request, allocation);
        }

        double elapsedMs = ElapsedMilliseconds(start);
        double slowThresholdMs = Math.Max(16.0, _settings.MaxRenderMilliseconds * 4.0);
        if (elapsedMs > slowThresholdMs)
        {
            XREngine.Debug.LightingWarningEvery(
                $"ShadowAtlas.GroupedPointFace.Slow.{seedRequest.Key.LightId}.{group.PageIndex}",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Slow grouped point-face render: light='{0}', faces={1}, page={2}, elapsedMs={3:F2}, frameBudgetMs={4:F2}.",
                LightName(seedRequest.Light),
                group.FaceCount,
                group.PageIndex,
                elapsedMs,
                _settings.MaxRenderMilliseconds);
        }

        return true;
    }

    private bool TryFindRequest(ShadowRequestKey key, out ShadowMapRequest request)
    {
        for (int i = 0; i < _requests.Count; i++)
        {
            if (_requests[i].Key == key)
            {
                request = _requests[i];
                return true;
            }
        }

        request = default;
        return false;
    }

    private bool TryRenderTile(ShadowMapRequest request, ShadowAtlasAllocation allocation, bool collectVisibleNow, out double elapsedMs)
    {
        long start = Stopwatch.GetTimestamp();
        elapsedMs = 0.0;
        if (!GetEncodingState(allocation.AtlasKind, request.Encoding).TryGetPageResource(allocation.PageIndex, out ShadowAtlasPageResource? page) ||
            page is null)
        {
            LogDirectionalRequestRenderState(request, allocation, "MissingPageResource", requiresRender: true);
            elapsedMs = ElapsedMilliseconds(start);
            return false;
        }

        bool result = request.ProjectionType switch
        {
            EShadowProjectionType.SpotPrimary when request.Light is SpotLightComponent spotLight
                => spotLight.RenderShadowAtlasTile(page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow),
            EShadowProjectionType.DirectionalPrimary when request.Light is DirectionalLightComponent primaryDirectionalLight
                => primaryDirectionalLight.RenderPrimaryShadowAtlasTile(page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow),
            EShadowProjectionType.DirectionalCascade when request.Light is DirectionalLightComponent cascadeDirectionalLight
                => cascadeDirectionalLight.RenderCascadeShadowAtlasTile(request.FaceOrCascadeIndex, page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow),
            EShadowProjectionType.PointFace when request.Light is PointLightComponent pointLight
                => pointLight.RenderShadowAtlasFaceTile(request.FaceOrCascadeIndex, page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow),
            _ => false,
        };

        if (result)
        {
            elapsedMs = ElapsedMilliseconds(start);
            double slowThresholdMs = Math.Max(16.0, _settings.MaxRenderMilliseconds * 4.0);
            if (elapsedMs > slowThresholdMs)
            {
                XREngine.Debug.LightingWarningEvery(
                    $"ShadowAtlas.Tile.Slow.{request.Key}",
                    TimeSpan.FromSeconds(2.0),
                    "[ShadowAtlas] Slow shadow tile render: key={0}, light='{1}', projection={2}, faceOrCascade={3}, elapsedMs={4:F2}, frameBudgetMs={5:F2}.",
                    request.Key,
                    LightName(request.Light),
                    request.ProjectionType,
                    request.FaceOrCascadeIndex,
                    elapsedMs,
                    _settings.MaxRenderMilliseconds);
            }
        }
        else
        {
            elapsedMs = ElapsedMilliseconds(start);
        }

        return result;
    }

    private void LogShadowAtlasRenderSummary(
        int scheduled,
        int checkedTiles,
        int skippedClean,
        int failedRender,
        int deferredByBudget,
        int firstDeferredRequestIndex,
        double elapsedMs,
        int budget)
    {
        if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
            !XREngine.Debug.ShouldLogEvery(
            $"DirectionalShadowAudit.AtlasRenderSummary.{GetHashCode()}",
            TimeSpan.FromSeconds(1.0)))
        {
            return;
        }

        XREngine.Debug.Lighting(
            XREngine.EOutputVerbosity.Normal,
            false,
            "[DirectionalShadowAudit][AtlasRenderSummary] frame={0} requests={1} scheduled={2} checked={3} skippedClean={4} failed={5} deferred={6} firstDeferredIndex={7} elapsedMs={8:F2} budgetTiles={9} budgetMs={10:F2} activeCameras={11} {12}={13} {14}={15}",
            _frameId,
            _requests.Count,
            scheduled,
            checkedTiles,
            skippedClean,
            failedRender,
            deferredByBudget,
            firstDeferredRequestIndex,
            elapsedMs,
            budget,
            _settings.MaxRenderMilliseconds,
            _activeCameraCount,
            DirectionalGroupedFramesCounterName,
            _directionalGroupedFrame ? 1 : 0,
            DirectionalSequentialFallbackFramesCounterName,
            _directionalSequentialFallbackFrame ? 1 : 0);
    }

    private void LogDirectionalAtlasLightDiagnostics()
    {
        if (!RenderDiagnosticsFlags.DirectionalShadowAudit)
            return;

        for (int i = 0; i < _directionalAtlasLightDiagnostics.Count; i++)
        {
            ShadowDirectionalAtlasLightDiagnostic diagnostic = _directionalAtlasLightDiagnostics[i];
            if (!XREngine.Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasLight.{diagnostic.LightId}",
                TimeSpan.FromSeconds(1.0)))
            {
                continue;
            }

            XREngine.Debug.Lighting(
                XREngine.EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][AtlasLight] frame={0} lightId={1} requestedCascades={2} residentCascades={3} dirtyCascades={4} renderedCascades={5} groupedAttempted={6} groupedSucceeded={7} selectedBackend={8} fallbackReason={9} groupReservationFailure={10} elapsedShadowMs={11:F2} criticalBudgetBypass={12} {13}={14} {15}={16}",
                diagnostic.FrameId,
                diagnostic.LightId,
                diagnostic.RequestedCascades,
                diagnostic.ResidentCascades,
                diagnostic.DirtyCascades,
                diagnostic.RenderedCascades,
                diagnostic.GroupedRenderAttempted,
                diagnostic.GroupedRenderSucceeded,
                diagnostic.SelectedCascadeBackend,
                diagnostic.FallbackReason,
                diagnostic.GroupReservationFailureReason,
                diagnostic.ElapsedShadowMilliseconds,
                diagnostic.CriticalBudgetBypassUsed,
                DirectionalGroupedFramesCounterName,
                _directionalGroupedFrame ? 1 : 0,
                DirectionalSequentialFallbackFramesCounterName,
                _directionalSequentialFallbackFrame ? 1 : 0);
        }
    }

    private void ResetBalancedAllocationSolveState(int entryCount)
    {
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            entry.Allocation = default;
            entry.AllocationSolved = false;
            _balancedAllocationEntries[i] = entry;
        }
    }

    private void TryAllocateDirectionalCascadeGroups(ShadowAtlasEncodingState state, int entryCount)
    {
        if (state.AtlasKind != EShadowAtlasKind.Directional)
            return;

        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry seedEntry = _balancedAllocationEntries[i];
            ShadowMapRequest seed = seedEntry.Request;
            if (seed.ProjectionType != EShadowProjectionType.DirectionalCascade ||
                seedEntry.AllocationSolved ||
                HasEarlierDirectionalCascadeGroup(seed, i))
            {
                continue;
            }

            int cascadeCount = CountDirectionalCascadeGroupMembers(seed, entryCount);
            if (cascadeCount <= 1)
                continue;

            if (cascadeCount > 4)
                continue;

            if (!TryGetEqualDirectionalCascadeGroupResolution(seed, entryCount, out uint resolution))
            {
                RecordDirectionalGroupReservationFailure(seed, "MixedResolution");
                _solveDiagnostics.DirectionalGroupCoLocationFailureCount++;
                continue;
            }

            int tileSize = checked((int)resolution);
            int groupSize = checked(tileSize * 2);
            if (groupSize > checked((int)_settings.PageSize))
            {
                RecordDirectionalGroupReservationFailure(seed, "GroupTooLarge");
                _solveDiagnostics.DirectionalGroupCoLocationFailureCount++;
                continue;
            }

            int pageCountBefore = state.PageCount;
            _solveDiagnostics.PageAllocationAttemptCount++;
            if (!state.TryAllocateContiguousGrid(
                tileSize,
                tilesPerAxis: 2,
                _settings,
                CurrentResidentBytes(),
                out int pageIndex,
                out int originX,
                out int originY,
                out SkipReason skipReason))
            {
                RecordDirectionalGroupReservationFailure(seed, skipReason.ToString());
                RecordSolveFailureReason(skipReason);
                _solveDiagnostics.DirectionalGroupCoLocationFailureCount++;
                continue;
            }

            _solveDiagnostics.PageAllocationSuccessCount++;
            if (state.PageCount > pageCountBefore)
            {
                _solveDiagnostics.PageCreateAttemptCount++;
                _solveDiagnostics.PageCreateSuccessCount++;
            }

            int ordinal = 0;
            for (int j = 0; j < entryCount; j++)
            {
                BalancedAllocationEntry entry = _balancedAllocationEntries[j];
                if (!IsSameDirectionalCascadeGroup(seed, entry.Request))
                    continue;

                int x = originX + ((ordinal & 1) * tileSize);
                int y = originY + ((ordinal >> 1) * tileSize);
                entry.Allocation = CreateResidentAllocation(entry.Request, state.AtlasKind, pageIndex, x, y, resolution);
                entry.AllocationSolved = true;
                _balancedAllocationEntries[j] = entry;
                ordinal++;
            }
        }
    }

    private bool TryGetEqualDirectionalCascadeGroupResolution(
        ShadowMapRequest seed,
        int entryCount,
        out uint resolution)
    {
        resolution = 0u;
        for (int i = 0; i < entryCount; i++)
        {
            BalancedAllocationEntry entry = _balancedAllocationEntries[i];
            if (!IsSameDirectionalCascadeGroup(seed, entry.Request))
                continue;

            if (resolution == 0u)
            {
                resolution = entry.Resolution;
                continue;
            }

            if (entry.Resolution != resolution)
            {
                resolution = 0u;
                return false;
            }
        }

        return resolution != 0u;
    }

    private void RecordDirectionalGroupReservationFailure(ShadowMapRequest seed, string reason)
    {
        if (!RenderDiagnosticsFlags.DirectionalShadowAudit)
            return;

        for (int i = 0; i < _directionalGroupReservationFailures.Count; i++)
        {
            DirectionalGroupReservationFailure failure = _directionalGroupReservationFailures[i];
            if (failure.LightId == seed.Key.LightId &&
                failure.Domain == seed.Key.Domain &&
                failure.Encoding == seed.Encoding)
            {
                return;
            }
        }

        _directionalGroupReservationFailures.Add(new DirectionalGroupReservationFailure(
            seed.Key.LightId,
            seed.Key.Domain,
            seed.Encoding,
            reason));
    }

    private static void LogDirectionalRequestRenderState(
        ShadowMapRequest request,
        ShadowAtlasAllocation allocation,
        string state,
        bool requiresRender)
    {
        if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
            !IsDirectionalRequest(request) ||
            !XREngine.Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasRequestRender.{request.Key}.{state}",
                TimeSpan.FromSeconds(1.0)))
        {
            return;
        }

        XREngine.Debug.Lighting(
            XREngine.EOutputVerbosity.Normal,
            false,
            "[DirectionalShadowAudit][AtlasRequestRender] frame={0} state={1} light='{2}' projection={3} cascadeOrFace={4} dirty={5} dirtyReason={6} canReuse={7} requiresRender={8} fallbackRequest={9} allocationResident={10} allocationFallback={11} lastRenderedFrame={12} page={13} rect={14} content={15}",
            RuntimeEngine.Rendering.State.RenderFrameId,
            state,
            LightName(request.Light),
            request.ProjectionType,
            request.FaceOrCascadeIndex,
            request.IsDirty,
            request.DirtyReason,
            request.CanReusePreviousFrame,
            requiresRender,
            request.Fallback,
            allocation.IsResident,
            allocation.ActiveFallback,
            allocation.LastRenderedFrame,
            allocation.PageIndex,
            FormatRect(allocation.InnerPixelRect),
            request.ContentHash);
    }

    private static bool IsDirectionalRequest(ShadowMapRequest request)
        => request.ProjectionType is EShadowProjectionType.DirectionalCascade or EShadowProjectionType.DirectionalPrimary;

    private static string FormatRect(BoundingRectangle rect)
        => $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}";

    private ShadowAtlasAllocation CreateSkippedAllocation(ShadowMapRequest request, SkipReason skipReason)
    {
        EShadowAtlasKind atlasKind = GetAtlasKind(request.ProjectionType);
        int atlasId = GetAtlasId(atlasKind, request.Encoding);
        if (skipReason == SkipReason.NotRelevant &&
            TryGetResidentAllocation(request.Key, out ShadowAtlasAllocation previous) &&
            previous.IsResident &&
            previous.AtlasKind == atlasKind &&
            previous.AtlasId == atlasId &&
            previous.PageIndex >= 0 &&
            previous.InnerPixelRect.Width > 0 &&
            previous.InnerPixelRect.Height > 0)
        {
            return previous with
            {
                ActiveFallback = ShadowFallbackMode.StaleTile,
                SkipReason = SkipReason.NotRelevant,
            };
        }

        return new ShadowAtlasAllocation(
            request.Key,
            AtlasKind: atlasKind,
            AtlasId: atlasId,
            PageIndex: -1,
            PixelRect: BoundingRectangle.Empty,
            InnerPixelRect: BoundingRectangle.Empty,
            UvScaleBias: Vector4.Zero,
            Resolution: 0u,
            LodLevel: -1,
            ContentVersion: request.ContentHash,
            LastRenderedFrame: 0u,
            IsResident: false,
            IsStaticCacheBacked: false,
            ActiveFallback: request.Fallback == ShadowFallbackMode.None ? ShadowFallbackMode.Lit : request.Fallback,
            SkipReason: skipReason);
    }

    private static ShadowAtlasAllocation CreateNonResidentSkippedAllocation(ShadowMapRequest request, SkipReason skipReason)
    {
        EShadowAtlasKind atlasKind = GetAtlasKind(request.ProjectionType);
        return new ShadowAtlasAllocation(
            request.Key,
            AtlasKind: atlasKind,
            AtlasId: GetAtlasId(atlasKind, request.Encoding),
            PageIndex: -1,
            PixelRect: BoundingRectangle.Empty,
            InnerPixelRect: BoundingRectangle.Empty,
            UvScaleBias: Vector4.Zero,
            Resolution: 0u,
            LodLevel: -1,
            ContentVersion: request.ContentHash,
            LastRenderedFrame: 0u,
            IsResident: false,
            IsStaticCacheBacked: false,
            ActiveFallback: request.Fallback == ShadowFallbackMode.None ? ShadowFallbackMode.Lit : request.Fallback,
            SkipReason: skipReason);
    }

    private static bool IsValidRequest(ShadowMapRequest request)
        => request.Light is not null &&
           request.Key.LightId != Guid.Empty &&
           request.DesiredResolution > 0u &&
           request.MinimumResolution > 0u &&
           request.NearPlane >= 0.0f &&
           request.FarPlane > request.NearPlane;

    private static int CalculateGutterTexels(ShadowMapRequest request)
        => request.CasterMode switch
        {
            ShadowCasterFilterMode.TwoSided => 4,
            ShadowCasterFilterMode.AlphaTested => 4,
            _ => 2,
        };

    private static int CalculateLodLevel(uint resolution, uint pageSize)
    {
        int lod = 0;
        uint size = Math.Max(1u, pageSize);
        while (size > resolution)
        {
            size >>= 1;
            lod++;
        }

        return lod;
    }

    private static uint ClampPowerOfTwo(uint value, uint pageSize)
    {
        uint max = Math.Max(1u, pageSize);
        value = Math.Clamp(value, 1u, max);
        uint result = 1u;
        while (result < value && result < max)
            result <<= 1;
        return result > max ? max : result;
    }

    private static EShadowAtlasKind GetAtlasKind(EShadowProjectionType projectionType)
        => projectionType switch
        {
            EShadowProjectionType.DirectionalPrimary or EShadowProjectionType.DirectionalCascade => EShadowAtlasKind.Directional,
            EShadowProjectionType.PointFace => EShadowAtlasKind.Point,
            EShadowProjectionType.SpotPrimary => EShadowAtlasKind.Spot,
            _ => EShadowAtlasKind.Directional,
        };

    private static int GetAtlasId(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
        => ((int)atlasKind << 8) | (int)encoding;

    private static int GetStateIndex(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
        => ((int)atlasKind * ShadowEncodingCount) + (int)encoding;

    private ShadowAtlasEncodingState GetEncodingState(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
        => _encodingStates[GetStateIndex(atlasKind, encoding)];

    private long CurrentResidentBytes()
    {
        long bytes = 0L;
        for (int i = 0; i < _encodingStates.Length; i++)
            bytes += _encodingStates[i].ResidentBytes;
        return bytes;
    }

    private void BuildPageDescriptors()
    {
        _pageDescriptors.Clear();
        for (int i = 0; i < _encodingStates.Length; i++)
            _encodingStates[i].AppendPageDescriptors(_pageDescriptors);
    }

    private void BuildDirectionalAtlasLightDiagnostics()
    {
        _directionalAtlasLightDiagnostics.Clear();
        if (!RenderDiagnosticsFlags.DirectionalShadowAudit)
            return;

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            if (request.ProjectionType != EShadowProjectionType.DirectionalCascade)
                continue;

            TryGetFrameAllocation(request.Key, out ShadowAtlasAllocation allocation);
            int diagnosticIndex = GetOrAddDirectionalAtlasLightDiagnostic(request);
            ShadowDirectionalAtlasLightDiagnostic diagnostic = _directionalAtlasLightDiagnostics[diagnosticIndex];
            _directionalAtlasLightDiagnostics[diagnosticIndex] = diagnostic with
            {
                RequestedCascades = diagnostic.RequestedCascades + 1,
                ResidentCascades = diagnostic.ResidentCascades + (allocation.IsResident ? 1 : 0),
                DirtyCascades = diagnostic.DirtyCascades + (request.IsDirty ? 1 : 0),
                RenderedCascades = diagnostic.RenderedCascades + (allocation.LastRenderedFrame == _frameId ? 1 : 0),
                CriticalBudgetBypassUsed = diagnostic.CriticalBudgetBypassUsed || ShouldRenderDirectionalRefreshPastBudget(request),
            };
        }

        for (int i = 0; i < _directionalAtlasRenderEvents.Count; i++)
        {
            DirectionalAtlasRenderEvent renderEvent = _directionalAtlasRenderEvents[i];
            int diagnosticIndex = GetOrAddDirectionalAtlasLightDiagnostic(renderEvent.LightId);
            ShadowDirectionalAtlasLightDiagnostic diagnostic = _directionalAtlasLightDiagnostics[diagnosticIndex];
            _directionalAtlasLightDiagnostics[diagnosticIndex] = diagnostic with
            {
                GroupedRenderAttempted = diagnostic.GroupedRenderAttempted || renderEvent.GroupedRenderAttempted,
                GroupedRenderSucceeded = diagnostic.GroupedRenderSucceeded || renderEvent.GroupedRenderSucceeded,
                CriticalBudgetBypassUsed = diagnostic.CriticalBudgetBypassUsed || renderEvent.CriticalBudgetBypassUsed,
                RenderedCascades = Math.Max(diagnostic.RenderedCascades, renderEvent.RenderedCascades),
                SelectedCascadeBackend = string.IsNullOrEmpty(renderEvent.SelectedCascadeBackend)
                    ? diagnostic.SelectedCascadeBackend
                    : renderEvent.SelectedCascadeBackend,
                FallbackReason = string.IsNullOrEmpty(renderEvent.FallbackReason)
                    ? diagnostic.FallbackReason
                    : renderEvent.FallbackReason,
                ElapsedShadowMilliseconds = diagnostic.ElapsedShadowMilliseconds + renderEvent.ElapsedShadowMilliseconds,
            };
        }

        for (int i = 0; i < _directionalGroupReservationFailures.Count; i++)
        {
            DirectionalGroupReservationFailure failure = _directionalGroupReservationFailures[i];
            int diagnosticIndex = GetOrAddDirectionalAtlasLightDiagnostic(failure.LightId);
            ShadowDirectionalAtlasLightDiagnostic diagnostic = _directionalAtlasLightDiagnostics[diagnosticIndex];
            _directionalAtlasLightDiagnostics[diagnosticIndex] = diagnostic with
            {
                GroupReservationFailureReason = failure.Reason,
            };
        }

        LogDirectionalAtlasLightDiagnostics();
    }

    private int GetOrAddDirectionalAtlasLightDiagnostic(ShadowMapRequest request)
    {
        string selectedBackend = string.Empty;
        string fallbackReason = string.Empty;
        if (request.Light is DirectionalLightComponent light)
        {
            selectedBackend = light.EffectiveCascadeShadowRenderBackend;
            fallbackReason = light.CascadeShadowRenderFallbackReason;
        }

        return GetOrAddDirectionalAtlasLightDiagnostic(request.Key.LightId, selectedBackend, fallbackReason);
    }

    private int GetOrAddDirectionalAtlasLightDiagnostic(Guid lightId)
        => GetOrAddDirectionalAtlasLightDiagnostic(lightId, string.Empty, string.Empty);

    private int GetOrAddDirectionalAtlasLightDiagnostic(Guid lightId, string selectedBackend, string fallbackReason)
    {
        for (int i = 0; i < _directionalAtlasLightDiagnostics.Count; i++)
            if (_directionalAtlasLightDiagnostics[i].LightId == lightId)
                return i;

        _directionalAtlasLightDiagnostics.Add(new ShadowDirectionalAtlasLightDiagnostic(
            LightId: lightId,
            FrameId: _frameId,
            RequestedCascades: 0,
            ResidentCascades: 0,
            DirtyCascades: 0,
            RenderedCascades: 0,
            GroupedRenderAttempted: false,
            GroupedRenderSucceeded: false,
            CriticalBudgetBypassUsed: false,
            SelectedCascadeBackend: selectedBackend,
            FallbackReason: fallbackReason,
            GroupReservationFailureReason: string.Empty,
            ElapsedShadowMilliseconds: 0.0));

        return _directionalAtlasLightDiagnostics.Count - 1;
    }

    private bool TryGetFrameAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
    {
        if (_currentAllocations.TryGetValue(key, out allocation))
            return true;

        for (int i = 0; i < _frameAllocations.Count; i++)
        {
            allocation = _frameAllocations[i];
            if (allocation.Key == key)
                return true;
        }

        allocation = default;
        return false;
    }

    private ShadowAtlasMetrics BuildMetrics()
    {
        int residentCount = 0;
        int skippedCount = 0;
        int notRelevantSkipCount = 0;
        for (int i = 0; i < _frameAllocations.Count; i++)
        {
            if (_frameAllocations[i].IsResident)
                residentCount++;
            else
                skippedCount++;

            if (_frameAllocations[i].SkipReason == SkipReason.NotRelevant)
                notRelevantSkipCount++;
        }

        int pageCount = 0;
        long residentBytes = 0L;
        int largestFreeRect = 0;
        long freeTexels = 0L;
        for (int i = 0; i < _encodingStates.Length; i++)
        {
            ShadowAtlasEncodingState state = _encodingStates[i];
            pageCount += state.PageCount;
            residentBytes += state.ResidentBytes;
            largestFreeRect = Math.Max(largestFreeRect, state.LargestFreeRect);
            freeTexels += state.FreeTexelCount;
        }

        return new ShadowAtlasMetrics(
            _frameId,
            _generation,
            RequestCount: _requests.Count,
            ResidentTileCount: residentCount,
            SkippedRequestCount: skippedCount,
            PageCount: pageCount,
            ResidentBytes: residentBytes,
            TilesScheduledThisFrame: _tilesScheduledThisFrame,
            QueueOverflowCount: _queueOverflowCount,
            NotRelevantSkipCount: notRelevantSkipCount,
            LargestFreeRect: largestFreeRect,
            FreeTexelCount: freeTexels,
            DirectionalGroupedFrameCount: _directionalGroupedFrame ? 1 : 0,
            DirectionalSequentialFallbackFrameCount: _directionalSequentialFallbackFrame ? 1 : 0);
    }

    private bool HasLayoutChanged()
    {
        if (_repackRequested)
            return true;

        if (_previousAllocations.Count != _currentAllocations.Count)
            return true;

        foreach (var pair in _currentAllocations)
        {
            if (!_previousAllocations.TryGetValue(pair.Key, out ShadowAtlasAllocation previous))
                return true;

            ShadowAtlasAllocation current = pair.Value;
            if (previous.PageIndex != current.PageIndex ||
                previous.AtlasKind != current.AtlasKind ||
                !RectEquals(previous.PixelRect, current.PixelRect) ||
                previous.Resolution != current.Resolution ||
                previous.AtlasId != current.AtlasId)
                return true;
        }

        return false;
    }

    private static bool RectEquals(BoundingRectangle a, BoundingRectangle b)
        => a.X == b.X &&
           a.Y == b.Y &&
           a.Width == b.Width &&
           a.Height == b.Height;

    private static bool RectanglesOverlap(BoundingRectangle a, BoundingRectangle b)
        => a.MinX < b.MaxX &&
           a.MaxX > b.MinX &&
           a.MinY < b.MaxY &&
           a.MaxY > b.MinY;

    private void ResetResources()
    {
        for (int i = 0; i < _encodingStates.Length; i++)
            _encodingStates[i].ResetResources();

        _previousAllocations.Clear();
        _currentAllocations.Clear();
        _currentAllocationIndices.Clear();
        _residentAllocations.Clear();
        _residentRemovalScratch.Clear();
        _lodStates.Clear();
        _demotionStates.Clear();
        _demotionRemovalScratch.Clear();
        _pendingSkippedAllocations.Clear();
        _pointFaceGroups.Clear();
    }

    private uint ApplyLodHysteresis(ShadowMapRequest request, uint desiredResolution, uint minimumResolution)
    {
        if (request.EditorPinned || !TryGetResidentAllocation(request.Key, out ShadowAtlasAllocation previous) || !previous.IsResident)
            return desiredResolution;

        if (!_lodStates.TryGetValue(request.Key, out ShadowLodState state) || state.Resolution == 0u)
            return desiredResolution;

        if (state.Resolution == desiredResolution)
            return desiredResolution;

        bool promotion = desiredResolution > state.Resolution;
        if (promotion && request.IsDirty && (request.DirtyReason & ShadowDirtyReason.AllocationMissing) != 0)
            return desiredResolution;

        ulong framesSinceChange = _frameId >= state.LastChangedFrame
            ? _frameId - state.LastChangedFrame
            : ulong.MaxValue;
        ulong cooldown = state.LastTransitionReason == ShadowLodTransitionReason.ForcedDownsize && promotion
            ? LodDownsizeRePromotionCooldownFrames
            : LodVoluntaryChangeCooldownFrames;
        if (framesSinceChange >= cooldown)
            return desiredResolution;

        return NormalizeTileResolution(
            state.Resolution,
            minimumResolution,
            Math.Min(_settings.MaxTileResolution, _settings.PageSize),
            _settings.PageSize);
    }

    private void UpdateLodState(ShadowRequestKey key, uint resolution)
    {
        if (_lodStates.TryGetValue(key, out ShadowLodState previous) && previous.Resolution == resolution)
            return;

        ShadowLodTransitionReason reason = ShadowLodTransitionReason.Voluntary;
        if (previous.Resolution != 0u)
        {
            reason = resolution < previous.Resolution
                ? ShadowLodTransitionReason.ForcedDownsize
                : ShadowLodTransitionReason.ForcedUpsize;
        }

        _lodStates[key] = new ShadowLodState(resolution, _frameId, reason);
    }

    private static ShadowAtlasManagerSettings NormalizeSettings(ShadowAtlasManagerSettings settings)
    {
        uint pageSize = ClampPowerOfTwo(settings.PageSize, 16384u);
        uint minTile = ClampPowerOfTwo(settings.MinTileResolution, pageSize);
        uint maxTile = ClampPowerOfTwo(settings.MaxTileResolution, pageSize);
        if (maxTile < minTile)
            maxTile = minTile;

        return settings with
        {
            PageSize = pageSize,
            MaxPages = Math.Clamp(settings.MaxPages, 1, 64),
            MaxMemoryBytes = Math.Max(0L, settings.MaxMemoryBytes),
            MaxTilesRenderedPerFrame = Math.Max(0, settings.MaxTilesRenderedPerFrame),
            MaxRenderMilliseconds = MathF.Max(0.0f, settings.MaxRenderMilliseconds),
            MinTileResolution = minTile,
            MaxTileResolution = maxTile,
            MaxRequestsPerFrame = Math.Clamp(settings.MaxRequestsPerFrame, 1, 65536),
        };
    }

    private sealed class ShadowAtlasEncodingState(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding)
    {
        private readonly List<ShadowAtlasPageResource> _pages = new();
        private readonly List<ShadowBuddyPageAllocator> _allocators = new();
        private XRTexture2DArray? _textureArray;
        private XRTexture2DArray? _rasterDepthTextureArray;

        public EShadowAtlasKind AtlasKind { get; } = atlasKind;
        public EShadowMapEncoding Encoding { get; } = encoding;
        public SkipReason LastFailureReason { get; private set; }
        public int PageCount => _pages.Count;
        public long ResidentBytes { get; private set; }
        public int LargestFreeRect { get; private set; }
        public long FreeTexelCount { get; private set; }

        public void BeginFrame()
        {
            LastFailureReason = SkipReason.None;
            LargestFreeRect = 0;
            FreeTexelCount = 0L;

            for (int i = 0; i < _allocators.Count; i++)
                _allocators[i].Reset();
        }

        public bool TryReserve(int pageIndex, int x, int y, int size)
        {
            if ((uint)pageIndex >= (uint)_allocators.Count)
                return false;

            return _allocators[pageIndex].TryReserve(x, y, size);
        }

        public bool TryReserveAlignedSubBlock(
            int pageIndex,
            int x,
            int y,
            int size,
            out int reservedX,
            out int reservedY)
        {
            if ((uint)pageIndex >= (uint)_allocators.Count)
            {
                reservedX = 0;
                reservedY = 0;
                return false;
            }

            return _allocators[pageIndex].TryReserveAlignedSubBlock(x, y, size, out reservedX, out reservedY);
        }

        public bool TryAllocate(
            int size,
            ShadowAtlasManagerSettings settings,
            long currentTotalResidentBytes,
            out int pageIndex,
            out int x,
            out int y,
            out SkipReason skipReason)
        {
            for (int i = 0; i < _allocators.Count; i++)
            {
                if (_allocators[i].TryAllocate(size, out x, out y))
                {
                    pageIndex = i;
                    skipReason = SkipReason.None;
                    return true;
                }
            }

            if (!CanCreatePage(settings, currentTotalResidentBytes, out skipReason))
            {
                LastFailureReason = skipReason;
                pageIndex = -1;
                x = 0;
                y = 0;
                return false;
            }

            CreatePage(settings);
            pageIndex = _allocators.Count - 1;
            if (_allocators[pageIndex].TryAllocate(size, out x, out y))
            {
                skipReason = SkipReason.None;
                return true;
            }

            skipReason = SkipReason.AllocationFailed;
            LastFailureReason = skipReason;
            return false;
        }

        public bool TryAllocateContiguousGrid(
            int tileSize,
            int tilesPerAxis,
            ShadowAtlasManagerSettings settings,
            long currentTotalResidentBytes,
            out int pageIndex,
            out int x,
            out int y,
            out SkipReason skipReason)
        {
            if (tileSize <= 0 || tilesPerAxis <= 0)
            {
                pageIndex = -1;
                x = 0;
                y = 0;
                skipReason = SkipReason.InvalidRequest;
                LastFailureReason = skipReason;
                return false;
            }

            int groupSize = checked(tileSize * tilesPerAxis);
            if (groupSize > checked((int)settings.PageSize))
            {
                pageIndex = -1;
                x = 0;
                y = 0;
                skipReason = SkipReason.AllocationFailed;
                LastFailureReason = skipReason;
                return false;
            }

            for (int i = 0; i < _allocators.Count; i++)
            {
                if (_allocators[i].TryAllocate(groupSize, out x, out y))
                {
                    pageIndex = i;
                    skipReason = SkipReason.None;
                    return true;
                }
            }

            if (!CanCreatePage(settings, currentTotalResidentBytes, out skipReason))
            {
                LastFailureReason = skipReason;
                pageIndex = -1;
                x = 0;
                y = 0;
                return false;
            }

            CreatePage(settings);
            pageIndex = _allocators.Count - 1;
            if (_allocators[pageIndex].TryAllocate(groupSize, out x, out y))
            {
                skipReason = SkipReason.None;
                return true;
            }

            skipReason = SkipReason.AllocationFailed;
            LastFailureReason = skipReason;
            x = 0;
            y = 0;
            return false;
        }

        public void AppendPageDescriptors(List<ShadowAtlasPageDescriptor> output)
        {
            for (int i = 0; i < _pages.Count; i++)
                output.Add(_pages[i].Descriptor);

            LargestFreeRect = 0;
            FreeTexelCount = 0L;
            for (int i = 0; i < _allocators.Count; i++)
            {
                LargestFreeRect = Math.Max(LargestFreeRect, _allocators[i].LargestFreeBlockSize);
                FreeTexelCount += _allocators[i].FreeTexelCount;
            }
        }

        public void ResetResources()
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].FrameBuffer.Destroy();

            _pages.Clear();
            _allocators.Clear();
            _textureArray?.Destroy();
            _textureArray = null;
            _rasterDepthTextureArray?.Destroy();
            _rasterDepthTextureArray = null;
            ResidentBytes = 0L;
            LargestFreeRect = 0;
            FreeTexelCount = 0L;
            LastFailureReason = SkipReason.None;
        }

        private bool CanCreatePage(ShadowAtlasManagerSettings settings, long currentTotalResidentBytes, out SkipReason skipReason)
        {
            int pageLimit = GetPageLimit(settings);
            if (_pages.Count >= pageLimit)
            {
                skipReason = SkipReason.PageBudgetExceeded;
                return false;
            }

            long allocationBytes = _textureArray is null
                ? GetArrayBytes(settings)
                : 0L;
            if (settings.MaxMemoryBytes > 0 && currentTotalResidentBytes + allocationBytes > settings.MaxMemoryBytes)
            {
                skipReason = SkipReason.MemoryBudgetExceeded;
                return false;
            }

            skipReason = SkipReason.None;
            return true;
        }

        private static int GetPageLimit(ShadowAtlasManagerSettings settings)
            => Math.Max(1, settings.MaxPages);

        private void CreatePage(ShadowAtlasManagerSettings settings)
        {
            EnsureTextureArrays(settings);
            ShadowAtlasPageDescriptor descriptor = CreateDescriptor(AtlasKind, Encoding, _pages.Count, settings.PageSize);
            _pages.Add(new ShadowAtlasPageResource(descriptor, _textureArray!, _rasterDepthTextureArray!));
            _allocators.Add(new ShadowBuddyPageAllocator(checked((int)settings.PageSize)));
        }

        public bool TryGetPageResource(int pageIndex, out ShadowAtlasPageResource? resource)
        {
            if ((uint)pageIndex < (uint)_pages.Count)
            {
                resource = _pages[pageIndex];
                return true;
            }

            resource = null;
            return false;
        }

        private long GetArrayBytes(ShadowAtlasManagerSettings settings)
            => checked(CreateDescriptor(AtlasKind, Encoding, 0, settings.PageSize).EstimatedBytes * GetPageLimit(settings));

        private void EnsureTextureArrays(ShadowAtlasManagerSettings settings)
        {
            if (_textureArray is not null && _rasterDepthTextureArray is not null)
                return;

            int pageLimit = GetPageLimit(settings);
            ShadowAtlasPageDescriptor descriptor = CreateDescriptor(AtlasKind, Encoding, 0, settings.PageSize);
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(Encoding);
            ETexMinFilter minFilter = format.RequiresLinearFiltering ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            _textureArray = new XRTexture2DArray(
                checked((uint)pageLimit),
                descriptor.PageSize,
                descriptor.PageSize,
                descriptor.InternalFormat,
                descriptor.PixelFormat,
                descriptor.PixelType,
                allocateData: false)
            {
                SamplerName = $"ShadowAtlas_{AtlasKind}_{Encoding}",
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                MinFilter = minFilter,
                MagFilter = magFilter,
                FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            };
            _rasterDepthTextureArray = new XRTexture2DArray(
                checked((uint)pageLimit),
                descriptor.PageSize,
                descriptor.PageSize,
                EPixelInternalFormat.DepthComponent24,
                EPixelFormat.DepthComponent,
                EPixelType.UnsignedInt,
                allocateData: false)
            {
                SamplerName = $"ShadowAtlasDepth_{AtlasKind}_{Encoding}",
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
            };
            ResidentBytes = GetArrayBytes(settings);
        }

        private static ShadowAtlasPageDescriptor CreateDescriptor(EShadowAtlasKind atlasKind, EShadowMapEncoding encoding, int pageIndex, uint pageSize)
        {
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(encoding);
            long rasterDepthBytes = checked((long)pageSize * pageSize * 4L);
            long bytes = checked((long)pageSize * pageSize * format.BytesPerTexel + rasterDepthBytes);
            return new ShadowAtlasPageDescriptor(atlasKind, encoding, pageIndex, pageSize, format.InternalFormat, format.PixelFormat, format.PixelType, bytes);
        }
    }

    private readonly record struct ShadowBlock(int X, int Y, int Size)
    {
        public bool Contains(int x, int y, int size)
            => x >= X && y >= Y && x + size <= X + Size && y + size <= Y + Size;
    }

    private sealed class ShadowBuddyPageAllocator
    {
        private readonly int _pageSize;
        private readonly int[] _levelSizes;
        private readonly List<ShadowBlock>[] _freeBlocksByLevel;

        public ShadowBuddyPageAllocator(int pageSize)
        {
            _pageSize = pageSize;
            int levelCount = CalculateLevelCount(pageSize);
            _levelSizes = new int[levelCount];
            _freeBlocksByLevel = new List<ShadowBlock>[levelCount];
            int size = pageSize;
            for (int level = 0; level < levelCount; level++)
            {
                _levelSizes[level] = size;
                _freeBlocksByLevel[level] = new List<ShadowBlock>();
                size = Math.Max(1, size >> 1);
            }

            Reset();
        }

        public int LargestFreeBlockSize { get; private set; }
        public long FreeTexelCount { get; private set; }

        public void Reset()
        {
            for (int i = 0; i < _freeBlocksByLevel.Length; i++)
                _freeBlocksByLevel[i].Clear();

            LargestFreeBlockSize = 0;
            FreeTexelCount = 0L;
            AddFreeBlock(new ShadowBlock(0, 0, _pageSize));
        }

        public bool TryAllocate(int requestedSize, out int x, out int y)
        {
            int size = NormalizeBlockSize(requestedSize);
            int targetLevel = GetLevelForSize(size);
            if (!TryTakeFreeBlockAtOrAbove(targetLevel, out ShadowBlock block))
            {
                x = 0;
                y = 0;
                return false;
            }

            ShadowBlock allocated = SplitToSize(block, size, targetX: block.X, targetY: block.Y);
            x = allocated.X;
            y = allocated.Y;
            return true;
        }

        public bool TryReserve(int x, int y, int requestedSize)
        {
            int size = NormalizeBlockSize(requestedSize);
            if (!IsValidAlignedRegion(x, y, size))
                return false;

            int targetLevel = GetLevelForSize(size);
            for (int searchLevel = targetLevel; searchLevel >= 0; searchLevel--)
            {
                int ancestorSize = _levelSizes[searchLevel];
                int ancestorX = AlignDown(x, ancestorSize);
                int ancestorY = AlignDown(y, ancestorSize);
                if (!TryTakeExactFreeBlock(searchLevel, ancestorX, ancestorY, out ShadowBlock foundBlock))
                    continue;

                ShadowBlock reserved = SplitToSize(foundBlock, size, x, y);
                if (reserved.X != x || reserved.Y != y || reserved.Size != size)
                    throw new InvalidOperationException("Buddy reservation produced an unexpected block.");

                return true;
            }

            return false;
        }

        public bool TryReserveAlignedSubBlock(
            int x,
            int y,
            int requestedSize,
            out int reservedX,
            out int reservedY)
        {
            int size = NormalizeBlockSize(requestedSize);
            reservedX = AlignDown(x, size);
            reservedY = AlignDown(y, size);
            if (!IsValidAlignedRegion(reservedX, reservedY, size))
            {
                reservedX = 0;
                reservedY = 0;
                return false;
            }

            if (TryReserve(reservedX, reservedY, size))
                return true;

            reservedX = 0;
            reservedY = 0;
            return false;
        }

        private ShadowBlock SplitToSize(ShadowBlock block, int size, int targetX, int targetY)
        {
            ShadowBlock current = block;
            while (current.Size > size)
            {
                int half = current.Size / 2;
                ShadowBlock bottomLeft = new(current.X, current.Y, half);
                ShadowBlock bottomRight = new(current.X + half, current.Y, half);
                ShadowBlock topLeft = new(current.X, current.Y + half, half);
                ShadowBlock topRight = new(current.X + half, current.Y + half, half);

                if (bottomLeft.Contains(targetX, targetY, size))
                {
                    current = bottomLeft;
                    AddFreeBlock(bottomRight);
                    AddFreeBlock(topLeft);
                    AddFreeBlock(topRight);
                }
                else if (bottomRight.Contains(targetX, targetY, size))
                {
                    current = bottomRight;
                    AddFreeBlock(bottomLeft);
                    AddFreeBlock(topLeft);
                    AddFreeBlock(topRight);
                }
                else if (topLeft.Contains(targetX, targetY, size))
                {
                    current = topLeft;
                    AddFreeBlock(bottomLeft);
                    AddFreeBlock(bottomRight);
                    AddFreeBlock(topRight);
                }
                else
                {
                    current = topRight;
                    AddFreeBlock(bottomLeft);
                    AddFreeBlock(bottomRight);
                    AddFreeBlock(topLeft);
                }
            }

            return current;
        }

        private bool TryTakeFreeBlockAtOrAbove(int targetLevel, out ShadowBlock block)
        {
            for (int level = targetLevel; level >= 0; level--)
            {
                List<ShadowBlock> blocks = _freeBlocksByLevel[level];
                if (blocks.Count == 0)
                    continue;

                block = blocks[0];
                RemoveFreeBlockAt(level, 0);
                return true;
            }

            block = default;
            return false;
        }

        private bool TryTakeExactFreeBlock(int level, int x, int y, out ShadowBlock block)
        {
            List<ShadowBlock> blocks = _freeBlocksByLevel[level];
            for (int i = 0; i < blocks.Count; i++)
            {
                ShadowBlock candidate = blocks[i];
                if (candidate.X != x || candidate.Y != y)
                    continue;

                RemoveFreeBlockAt(level, i);
                block = candidate;
                return true;
            }

            block = default;
            return false;
        }

        private void AddFreeBlock(ShadowBlock block)
        {
            int level = GetLevelForSize(block.Size);
            InsertFreeBlockSorted(_freeBlocksByLevel[level], block);
            FreeTexelCount += (long)block.Size * block.Size;
            if (block.Size > LargestFreeBlockSize)
                LargestFreeBlockSize = block.Size;
        }

        private static void InsertFreeBlockSorted(List<ShadowBlock> blocks, ShadowBlock block)
        {
            int insertIndex = blocks.Count;
            for (int i = 0; i < blocks.Count; i++)
            {
                ShadowBlock candidate = blocks[i];
                if (block.Y < candidate.Y ||
                    (block.Y == candidate.Y && block.X < candidate.X))
                {
                    insertIndex = i;
                    break;
                }
            }

            blocks.Insert(insertIndex, block);
        }

        private void RemoveFreeBlockAt(int level, int index)
        {
            List<ShadowBlock> blocks = _freeBlocksByLevel[level];
            ShadowBlock block = blocks[index];
            blocks.RemoveAt(index);
            FreeTexelCount -= (long)block.Size * block.Size;
            if (block.Size == LargestFreeBlockSize && blocks.Count == 0)
                LargestFreeBlockSize = FindLargestFreeBlockSize();
        }

        private int NormalizeBlockSize(int requestedSize)
        {
            int size = 1;
            requestedSize = Math.Clamp(requestedSize, 1, _pageSize);
            while (size < requestedSize)
                size <<= 1;
            return Math.Min(size, _pageSize);
        }

        private int GetLevelForSize(int size)
        {
            for (int i = 0; i < _levelSizes.Length; i++)
                if (_levelSizes[i] == size)
                    return i;

            throw new InvalidOperationException($"Shadow buddy allocator cannot represent block size {size}.");
        }

        private int FindLargestFreeBlockSize()
        {
            for (int i = 0; i < _freeBlocksByLevel.Length; i++)
                if (_freeBlocksByLevel[i].Count > 0)
                    return _levelSizes[i];

            return 0;
        }

        private bool IsValidAlignedRegion(int x, int y, int size)
            => size > 0 &&
               x >= 0 &&
               y >= 0 &&
               x + size <= _pageSize &&
               y + size <= _pageSize &&
               x % size == 0 &&
               y % size == 0;

        private static int AlignDown(int value, int alignment)
            => alignment <= 1 ? value : value - (value % alignment);

        private static int CalculateLevelCount(int pageSize)
        {
            int levels = 1;
            int size = Math.Max(1, pageSize);
            while (size > 1)
            {
                levels++;
                size >>= 1;
            }

            return levels;
        }
    }
}
