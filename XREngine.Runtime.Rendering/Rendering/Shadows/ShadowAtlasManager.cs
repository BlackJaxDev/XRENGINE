using System.Diagnostics;
using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasManager
{
    private sealed class RequestComparer : IComparer<ShadowMapRequest>
    {
        public static readonly RequestComparer Instance = new();

        public int Compare(ShadowMapRequest x, ShadowMapRequest y)
        {
            int result = y.Priority.CompareTo(x.Priority);
            if (result != 0)
                return result;

            result = y.EditorPinned.CompareTo(x.EditorPinned);
            return result != 0 ? result : x.Key.CompareTo(y.Key);
        }
    }

    private readonly List<ShadowMapRequest> _requests;
    private readonly List<ShadowAtlasAllocation> _frameAllocations;
    private readonly List<ShadowAtlasPageDescriptor> _pageDescriptors;
    private readonly ShadowAtlasEncodingState[] _encodingStates;
    private readonly object _submitSync = new();
    private Dictionary<ShadowRequestKey, ShadowAtlasAllocation> _previousAllocations = new();
    private readonly Dictionary<ShadowRequestKey, ShadowAtlasAllocation> _currentAllocations = new();
    private readonly Dictionary<ShadowRequestKey, int> _currentAllocationIndices = new();
    private readonly ShadowAtlasFrameData[] _frameBuffers = [new(), new()];
    private int _publishedFrameIndex;
    private ShadowAtlasManagerSettings _settings;
    private ulong _frameId;
    private ulong _generation;
    private int _queueOverflowCount;
    private int _tilesScheduledThisFrame;
    private int _activeCameraCount;

    public ShadowAtlasManager()
        : this(ShadowAtlasManagerSettings.Default)
    {
    }

    public ShadowAtlasManager(ShadowAtlasManagerSettings settings)
    {
        _settings = NormalizeSettings(settings);
        _requests = new(_settings.MaxRequestsPerFrame);
        _frameAllocations = new(_settings.MaxRequestsPerFrame);
        _pageDescriptors = new();
        _encodingStates =
        [
            new(EShadowMapEncoding.Depth),
            new(EShadowMapEncoding.Variance2),
            new(EShadowMapEncoding.ExponentialVariance2),
            new(EShadowMapEncoding.ExponentialVariance4),
        ];

        Configure(_settings);
    }

    public ShadowAtlasManagerSettings Settings => _settings;
    public ShadowAtlasFrameData PublishedFrameData => _frameBuffers[_publishedFrameIndex];
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

        if (resourceShapeChanged)
            ResetResources();
    }

    public void ConfigureFromEngineSettings()
        => Configure(ShadowAtlasManagerSettings.FromCurrentRuntimeSettings());

    public void BeginFrame(IRuntimeRenderWorld world, ReadOnlySpan<XRCamera> activeCameras)
    {
        ConfigureFromEngineSettings();
        BeginFrameCore(Engine.Rendering.State.RenderFrameId, activeCameras.Length);
    }

    public void BeginFrame(ulong frameId, int activeCameraCount = 0)
        => BeginFrameCore(frameId, activeCameraCount);

    private void BeginFrameCore(ulong frameId, int activeCameraCount)
    {
        _frameId = frameId;
        _activeCameraCount = Math.Max(0, activeCameraCount);
        _queueOverflowCount = 0;
        _tilesScheduledThisFrame = 0;
        _requests.Clear();
        _frameAllocations.Clear();
        _currentAllocations.Clear();
        _currentAllocationIndices.Clear();

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
        using var sample = Engine.Profiler.Start("ShadowAtlasManager.SolveAllocations");

        _requests.Sort(RequestComparer.Instance);

        for (int i = 0; i < _requests.Count; i++)
        {
            ShadowMapRequest request = _requests[i];
            if (!request.Light.CastsShadows || !request.Light.IsActiveInHierarchy)
            {
                _frameAllocations.Add(CreateSkippedAllocation(request, SkipReason.DisabledByLight));
                continue;
            }

            ShadowAtlasAllocation allocation = AllocateRequest(request, out _);
            if (!allocation.IsResident)
            {
                _frameAllocations.Add(allocation);
                continue;
            }

            _currentAllocations[request.Key] = allocation;
            _currentAllocationIndices[request.Key] = _frameAllocations.Count;
            _frameAllocations.Add(allocation);
        }
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
        int firstDeferredRequestIndex = -1;

        for (int i = 0; i < _requests.Count && scheduled < budget; i++)
        {
            if (scheduled > 0 && HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds))
            {
                deferredByBudget = _requests.Count - i;
                firstDeferredRequestIndex = i;
                break;
            }

            ShadowMapRequest request = _requests[i];
            if (!_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation allocation))
            {
                LogDirectionalRequestRenderState(request, default, "NoCurrentAllocation", requiresRender: false);
                skippedClean++;
                continue;
            }

            bool requiresRender = RequiresTileRender(request, allocation);
            if (!requiresRender)
            {
                LogDirectionalRequestRenderState(request, allocation, "SkippedClean", requiresRender: false);
                skippedClean++;
                continue;
            }

            bool forceCollectVisible = collectVisibleNow;
            checkedTiles++;

            if (!TryRenderTile(request, allocation, forceCollectVisible))
            {
                LogDirectionalRequestRenderState(request, allocation, "RenderFailed", requiresRender: true);
                failedRender++;
                continue;
            }

            MarkTileRendered(request.Key, allocation);
            if (_currentAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation renderedAllocation))
                LogDirectionalRequestRenderState(request, renderedAllocation, "Rendered", requiresRender: true);
            scheduled++;

            if (HasRenderBudgetExpired(startTimestamp, _settings.MaxRenderMilliseconds))
            {
                deferredByBudget = _requests.Count - i - 1;
                firstDeferredRequestIndex = i + 1;
                break;
            }
        }

        _tilesScheduledThisFrame = scheduled;
        double elapsedMs = ElapsedMilliseconds(frameStart);
        if (deferredByBudget > 0)
        {
            XREngine.Debug.LightingEvery(
                "ShadowAtlas.RenderBudget.Deferred",
                TimeSpan.FromSeconds(2.0),
                "[ShadowAtlas] Deferred {0} shadow request(s) after rendering {1}/{2} tile(s) in {3:F2}ms (budget {4:F2}ms, firstDeferredIndex={5}).",
                deferredByBudget,
                scheduled,
                budget,
                elapsedMs,
                _settings.MaxRenderMilliseconds,
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
                deferredByBudget,
                elapsedMs,
                _settings.MaxRenderMilliseconds);
        }

        LogShadowAtlasRenderSummary(
            scheduled,
            checkedTiles,
            skippedClean,
            failedRender,
            deferredByBudget,
            firstDeferredRequestIndex,
            elapsedMs,
            budget);

        return scheduled;
    }

    private static bool HasRenderBudgetExpired(long startTimestamp, float maxRenderMilliseconds)
    {
        if (startTimestamp == 0L || maxRenderMilliseconds <= 0.0f)
            return false;

        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        return elapsedMilliseconds >= maxRenderMilliseconds;
    }

    private static double ElapsedMilliseconds(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private static string LightName(LightComponent light)
        => light.SceneNode?.Name ?? light.Name ?? light.GetType().Name;

    public void PublishFrameData()
    {
        using var sample = Engine.Profiler.Start("ShadowAtlasManager.PublishFrameData");

        bool layoutChanged = HasLayoutChanged();
        if (layoutChanged)
            unchecked { _generation++; }

        BuildPageDescriptors();
        ShadowAtlasMetrics metrics = BuildMetrics();
        int writeIndex = 1 - _publishedFrameIndex;
        _frameBuffers[writeIndex].SetData(_frameId, _generation, _frameAllocations, _pageDescriptors, metrics);
        _publishedFrameIndex = writeIndex;

        _previousAllocations.Clear();
        foreach (var pair in _currentAllocations)
            _previousAllocations[pair.Key] = pair.Value;
    }

    public bool TryGetAllocation(ShadowRequestKey key, out ShadowAtlasAllocation allocation)
        => PublishedFrameData.TryGetAllocation(key, out allocation);

    public bool TryGetPageTexture(EShadowMapEncoding encoding, int pageIndex, out XRTexture2D texture)
    {
        if (GetEncodingState(encoding).TryGetPageResource(pageIndex, out ShadowAtlasPageResource? resource) &&
            resource is not null)
        {
            texture = resource.Texture;
            return true;
        }

        texture = null!;
        return false;
    }

    public bool TryGetPageRasterDepthTexture(EShadowMapEncoding encoding, int pageIndex, out XRTexture2D texture)
    {
        if (GetEncodingState(encoding).TryGetPageResource(pageIndex, out ShadowAtlasPageResource? resource) &&
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
        _frameAllocations.Clear();
        _pageDescriptors.Clear();
        _previousAllocations.Clear();
        _currentAllocations.Clear();
        _currentAllocationIndices.Clear();
        _queueOverflowCount = 0;
        _tilesScheduledThisFrame = 0;
        _generation = 0;
        ResetResources();
    }

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

    private ShadowAtlasAllocation AllocateRequest(ShadowMapRequest request, out SkipReason skipReason)
    {
        ShadowAtlasEncodingState state = GetEncodingState(request.Encoding);
        uint desired = NormalizeTileResolution(
            request.DesiredResolution,
            Math.Max(request.MinimumResolution, _settings.MinTileResolution),
            Math.Min(_settings.MaxTileResolution, _settings.PageSize),
            _settings.PageSize);
        uint minimum = NormalizeTileResolution(
            request.MinimumResolution,
            _settings.MinTileResolution,
            desired,
            _settings.PageSize);

        ShadowAtlasAllocation? previous = _previousAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation prior)
            ? prior
            : null;

        for (uint candidate = desired; candidate >= minimum; candidate >>= 1)
        {
            if (TryAllocateCandidate(state, request, candidate, previous, out ShadowAtlasAllocation allocation, out skipReason))
                return allocation;

            if (candidate == 1u)
                break;
        }

        skipReason = state.LastFailureReason is SkipReason.None ? SkipReason.AllocationFailed : state.LastFailureReason;
        return CreateSkippedAllocation(request, skipReason);
    }

    private bool TryAllocateCandidate(
        ShadowAtlasEncodingState state,
        ShadowMapRequest request,
        uint candidate,
        ShadowAtlasAllocation? previous,
        out ShadowAtlasAllocation allocation,
        out SkipReason skipReason)
    {
        int size = checked((int)candidate);
        if (previous is ShadowAtlasAllocation prior &&
            prior.IsResident &&
            prior.Resolution == candidate &&
            state.TryReserve(prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, size))
        {
            allocation = CreateResidentAllocation(request, prior.PageIndex, prior.PixelRect.X, prior.PixelRect.Y, candidate);
            skipReason = SkipReason.None;
            return true;
        }

        if (state.TryAllocate(size, _settings, CurrentResidentBytes(), out int pageIndex, out int x, out int y, out skipReason))
        {
            allocation = CreateResidentAllocation(request, pageIndex, x, y, candidate);
            return true;
        }

        allocation = default;
        return false;
    }

    private ShadowAtlasAllocation CreateResidentAllocation(ShadowMapRequest request, int pageIndex, int x, int y, uint resolution)
    {
        int size = checked((int)resolution);
        int gutter = Math.Min(CalculateGutterTexels(request), Math.Max(0, (size - 1) / 2));
        BoundingRectangle pixelRect = new(x, y, size, size);
        BoundingRectangle innerRect = new(x + gutter, y + gutter, size - (gutter * 2), size - (gutter * 2));
        Vector4 uv = new(
            innerRect.Width / (float)_settings.PageSize,
            innerRect.Height / (float)_settings.PageSize,
            innerRect.X / (float)_settings.PageSize,
            innerRect.Y / (float)_settings.PageSize);
        int lod = CalculateLodLevel(resolution, _settings.PageSize);
        ulong lastRendered = _previousAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation prior)
            ? prior.LastRenderedFrame
            : 0u;
        bool requiresFreshRenderBeforeSampling = lastRendered == 0u ||
            request.IsDirty ||
            !request.CanReusePreviousFrame;
        ShadowFallbackMode activeFallback = requiresFreshRenderBeforeSampling && request.Fallback != ShadowFallbackMode.None
            ? request.Fallback
            : ShadowFallbackMode.None;

        return new ShadowAtlasAllocation(
            request.Key,
            AtlasId: (int)request.Encoding,
            PageIndex: pageIndex,
            PixelRect: pixelRect,
            InnerPixelRect: innerRect,
            UvScaleBias: uv,
            Resolution: resolution,
            LodLevel: lod,
            ContentVersion: request.ContentHash,
            LastRenderedFrame: lastRendered,
            IsResident: true,
            IsStaticCacheBacked: false,
            ActiveFallback: activeFallback,
            SkipReason: SkipReason.None);
    }

    private void MarkTileRendered(ShadowRequestKey key, ShadowAtlasAllocation allocation)
    {
        ulong renderedFrameId = _frameId != 0u ? _frameId : 1u;
        ShadowAtlasAllocation rendered = allocation with
        {
            LastRenderedFrame = renderedFrameId,
            ActiveFallback = ShadowFallbackMode.None,
        };
        _currentAllocations[key] = rendered;
        if (_currentAllocationIndices.TryGetValue(key, out int index))
            _frameAllocations[index] = rendered;
    }

    private bool RequiresTileRender(ShadowMapRequest request, ShadowAtlasAllocation allocation)
    {
        if (allocation.LastRenderedFrame == 0u)
            return true;

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

        if (!_previousAllocations.TryGetValue(request.Key, out ShadowAtlasAllocation prior))
            return true;

        return prior.PageIndex != pageIndex ||
            prior.PixelRect.X != x ||
            prior.PixelRect.Y != y ||
            prior.Resolution != resolution ||
            prior.AtlasId != (int)request.Encoding ||
            prior.ContentVersion != request.ContentHash;
    }

    private bool TryRenderTile(ShadowMapRequest request, ShadowAtlasAllocation allocation, bool collectVisibleNow)
    {
        long start = Stopwatch.GetTimestamp();
        if (request.Encoding != EShadowMapEncoding.Depth ||
            !GetEncodingState(request.Encoding).TryGetPageResource(allocation.PageIndex, out ShadowAtlasPageResource? page) ||
            page is null)
        {
            LogDirectionalRequestRenderState(request, allocation, "MissingPageResource", requiresRender: true);
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
            _ => false,
        };

        if (result)
        {
            double elapsedMs = ElapsedMilliseconds(start);
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
        if (!XREngine.Debug.ShouldLogEvery(
            $"DirectionalShadowAudit.AtlasRenderSummary.{GetHashCode()}",
            TimeSpan.FromSeconds(1.0)))
        {
            return;
        }

        XREngine.Debug.Lighting(
            XREngine.EOutputVerbosity.Normal,
            false,
            "[DirectionalShadowAudit][AtlasRenderSummary] frame={0} requests={1} scheduled={2} checked={3} skippedClean={4} failed={5} deferred={6} firstDeferredIndex={7} elapsedMs={8:F2} budgetTiles={9} budgetMs={10:F2} activeCameras={11}",
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
            _activeCameraCount);
    }

    private static void LogDirectionalRequestRenderState(
        ShadowMapRequest request,
        ShadowAtlasAllocation allocation,
        string state,
        bool requiresRender)
    {
        if (!IsDirectionalRequest(request) ||
            !XREngine.Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.AtlasRequestRender.{request.Key}.{state}",
                TimeSpan.FromSeconds(1.0)))
        {
            return;
        }

        XREngine.Debug.Lighting(
            XREngine.EOutputVerbosity.Normal,
            false,
            "[DirectionalShadowAudit][AtlasRequestRender] frame={0} state={1} light='{2}' projection={3} cascadeOrFace={4} dirty={5} canReuse={6} requiresRender={7} fallbackRequest={8} allocationResident={9} allocationFallback={10} lastRenderedFrame={11} page={12} rect={13} content={14}",
            Engine.Rendering.State.RenderFrameId,
            state,
            LightName(request.Light),
            request.ProjectionType,
            request.FaceOrCascadeIndex,
            request.IsDirty,
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
        => new(
            request.Key,
            AtlasId: (int)request.Encoding,
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

    private ShadowAtlasEncodingState GetEncodingState(EShadowMapEncoding encoding)
        => _encodingStates[(int)encoding];

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

    private ShadowAtlasMetrics BuildMetrics()
    {
        int residentCount = 0;
        int skippedCount = 0;
        for (int i = 0; i < _frameAllocations.Count; i++)
        {
            if (_frameAllocations[i].IsResident)
                residentCount++;
            else
                skippedCount++;
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
            LargestFreeRect: largestFreeRect,
            FreeTexelCount: freeTexels);
    }

    private bool HasLayoutChanged()
    {
        if (_previousAllocations.Count != _currentAllocations.Count)
            return true;

        foreach (var pair in _currentAllocations)
        {
            if (!_previousAllocations.TryGetValue(pair.Key, out ShadowAtlasAllocation previous))
                return true;

            ShadowAtlasAllocation current = pair.Value;
            if (previous.PageIndex != current.PageIndex ||
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

    private void ResetResources()
    {
        for (int i = 0; i < _encodingStates.Length; i++)
            _encodingStates[i].ResetResources();

        _previousAllocations.Clear();
        _currentAllocations.Clear();
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

    private sealed class ShadowAtlasEncodingState(EShadowMapEncoding encoding)
    {
        private readonly List<ShadowAtlasPageResource> _pages = new();
        private readonly List<ShadowBuddyPageAllocator> _allocators = new();

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

            CreatePage(settings.PageSize);
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
            {
                _pages[i].FrameBuffer.Destroy();
                _pages[i].Texture.Destroy();
                _pages[i].RasterDepthTexture.Destroy();
            }

            _pages.Clear();
            _allocators.Clear();
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

            long pageBytes = GetPageBytes(settings.PageSize);
            if (settings.MaxMemoryBytes > 0 && currentTotalResidentBytes + pageBytes > settings.MaxMemoryBytes)
            {
                skipReason = SkipReason.MemoryBudgetExceeded;
                return false;
            }

            skipReason = SkipReason.None;
            return true;
        }

        private int GetPageLimit(ShadowAtlasManagerSettings settings)
            => Encoding == EShadowMapEncoding.Depth
                ? settings.MaxPages
                : Math.Max(1, Math.Min(settings.MaxPages, 1));

        private void CreatePage(uint pageSize)
        {
            ShadowAtlasPageDescriptor descriptor = CreateDescriptor(Encoding, _pages.Count, pageSize);
            _pages.Add(new ShadowAtlasPageResource(descriptor));
            _allocators.Add(new ShadowBuddyPageAllocator(checked((int)pageSize)));
            ResidentBytes += descriptor.EstimatedBytes;
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

        private long GetPageBytes(uint pageSize)
            => CreateDescriptor(Encoding, _pages.Count, pageSize).EstimatedBytes;

        private static ShadowAtlasPageDescriptor CreateDescriptor(EShadowMapEncoding encoding, int pageIndex, uint pageSize)
        {
            ShadowMapFormatDescriptor format = ShadowMapResourceFactory.GetPreferredFormat(encoding);
            long rasterDepthBytes = checked((long)pageSize * pageSize * 4L);
            long bytes = checked((long)pageSize * pageSize * format.BytesPerTexel + rasterDepthBytes);
            return new ShadowAtlasPageDescriptor(encoding, pageIndex, pageSize, format.InternalFormat, format.PixelFormat, format.PixelType, bytes);
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
        private readonly SortedDictionary<int, List<ShadowBlock>> _freeBlocks = new();

        public ShadowBuddyPageAllocator(int pageSize)
        {
            _pageSize = pageSize;
            Reset();
        }

        public int LargestFreeBlockSize { get; private set; }
        public long FreeTexelCount { get; private set; }

        public void Reset()
        {
            _freeBlocks.Clear();
            AddFreeBlock(new ShadowBlock(0, 0, _pageSize));
            RecalculateFreeStats();
        }

        public bool TryAllocate(int requestedSize, out int x, out int y)
        {
            int size = NormalizeBlockSize(requestedSize);
            if (!TryTakeFreeBlockAtLeast(size, out ShadowBlock block))
            {
                x = 0;
                y = 0;
                return false;
            }

            ShadowBlock allocated = SplitToSize(block, size, targetX: block.X, targetY: block.Y);
            x = allocated.X;
            y = allocated.Y;
            RecalculateFreeStats();
            return true;
        }

        public bool TryReserve(int x, int y, int requestedSize)
        {
            int size = NormalizeBlockSize(requestedSize);
            int foundKey = 0;
            int foundIndex = -1;
            ShadowBlock foundBlock = default;
            foreach (var entry in _freeBlocks)
            {
                if (entry.Key < size)
                    continue;

                List<ShadowBlock> blocks = entry.Value;
                for (int i = 0; i < blocks.Count; i++)
                {
                    ShadowBlock block = blocks[i];
                    if (!block.Contains(x, y, size))
                        continue;

                    foundKey = entry.Key;
                    foundIndex = i;
                    foundBlock = block;
                    break;
                }

                if (foundIndex >= 0)
                    break;
            }

            if (foundIndex < 0)
                return false;

            List<ShadowBlock> foundBlocks = _freeBlocks[foundKey];
            foundBlocks.RemoveAt(foundIndex);
            if (foundBlocks.Count == 0)
                _freeBlocks.Remove(foundKey);

            ShadowBlock reserved = SplitToSize(foundBlock, size, x, y);
            if (reserved.X != x || reserved.Y != y || reserved.Size != size)
                throw new InvalidOperationException("Buddy reservation produced an unexpected block.");

            RecalculateFreeStats();
            return true;
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

        private bool TryTakeFreeBlockAtLeast(int size, out ShadowBlock block)
        {
            int foundKey = 0;
            int foundIndex = -1;
            ShadowBlock foundBlock = default;
            foreach (var entry in _freeBlocks)
            {
                if (entry.Key < size || entry.Value.Count == 0)
                    continue;

                List<ShadowBlock> blocks = entry.Value;
                int bestIndex = 0;
                ShadowBlock best = blocks[0];
                for (int i = 1; i < blocks.Count; i++)
                {
                    ShadowBlock candidate = blocks[i];
                    if (candidate.Y < best.Y || (candidate.Y == best.Y && candidate.X < best.X))
                    {
                        best = candidate;
                        bestIndex = i;
                    }
                }

                foundKey = entry.Key;
                foundIndex = bestIndex;
                foundBlock = best;
                break;
            }

            if (foundIndex < 0)
            {
                block = default;
                return false;
            }

            List<ShadowBlock> foundBlocks = _freeBlocks[foundKey];
            foundBlocks.RemoveAt(foundIndex);
            if (foundBlocks.Count == 0)
                _freeBlocks.Remove(foundKey);

            block = foundBlock;
            return true;
        }

        private void AddFreeBlock(ShadowBlock block)
        {
            if (!_freeBlocks.TryGetValue(block.Size, out List<ShadowBlock>? list))
            {
                list = new List<ShadowBlock>();
                _freeBlocks.Add(block.Size, list);
            }

            list.Add(block);
        }

        private int NormalizeBlockSize(int requestedSize)
        {
            int size = 1;
            requestedSize = Math.Clamp(requestedSize, 1, _pageSize);
            while (size < requestedSize)
                size <<= 1;
            return Math.Min(size, _pageSize);
        }

        private void RecalculateFreeStats()
        {
            LargestFreeBlockSize = 0;
            FreeTexelCount = 0L;
            foreach (var entry in _freeBlocks)
            {
                if (entry.Value.Count == 0)
                    continue;

                LargestFreeBlockSize = Math.Max(LargestFreeBlockSize, entry.Key);
                long blockTexels = (long)entry.Key * entry.Key;
                FreeTexelCount += blockTexels * entry.Value.Count;
            }
        }
    }
}
