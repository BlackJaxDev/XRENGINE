using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Decides which geometry pages are resident in the <see cref="AdvancedGeometryPagePool"/>. The
/// GPU writes page requests and slot touch bits each frame. The backend copies them to a readback
/// ring, and when a ring slot completes, it hands the data to <see cref="SubmitFeedback"/>. Once
/// per publication, <see cref="Update"/> adds the missing dependency chain of each requested page,
/// loads pages in band and priority order within the upload budget, and evicts old pages when no
/// slot is free. The pool records each change in its page table, and the next captured publication
/// draws with it.
/// </summary>
/// <remarks>
/// <para>
/// Residency is closed toward the roots: a page loads only after every page in its dependency list
/// is resident (or loads first in the same update), and a page with resident children is not
/// evicted. The root pages of a geometry load at registration and stay resident until the geometry
/// is removed. With these rules, the cut draws exactly one cluster over each point of a mesh.
/// </para>
/// <para>
/// An evicted slot does not become free at once. The pool retires it until every publication that
/// can name it is reclaimed. A load that finds no free slot therefore waits, and the manager evicts
/// only as many pages as the waiting loads need beyond the slots that are already retiring.
/// </para>
/// <para>
/// The manager is not thread-safe. The publisher thread registers geometries and runs the updates.
/// The update path does not allocate after its arrays have grown to the working size.
/// </para>
/// </remarks>
public sealed class MeshletPageStreamingManager
{
    private const int MaxDependencyDepth = 64;
    private const uint ConsumedCandidate = uint.MaxValue;

    private readonly AdvancedGeometryPagePool _pool;
    private uint[] _requested = new uint[256];
    private ulong[] _requestOrderKeys = new ulong[256];
    private int _requestedCount;
    private uint[] _candidates = new uint[256];
    private long[] _candidateKeys = new long[256];
    private int _candidateCount;
    private int _candidateCursor;
    private bool _candidatesBuilt;
    private long _updateIndex;
    private bool _feedbackOpen;

    // State of the update that runs now.
    private long _budgetBytes;
    private bool _budgetExhausted;
    private int _loadsThisUpdate;
    private int _waitingLoads;
    private int _evictionsThisUpdate;
    private int _evictionLimit;

    /// <summary>Creates a manager for one page pool.</summary>
    public MeshletPageStreamingManager(AdvancedGeometryPagePool pool, MeshletPageResidencyPolicy? policy = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        Policy = policy ?? new MeshletPageResidencyPolicy();
    }

    /// <summary>The budgets and eviction rules.</summary>
    public MeshletPageResidencyPolicy Policy { get; }

    /// <summary>The residency records of the streamed pages.</summary>
    public MeshletPageRegistry Registry { get; } = new();

    /// <summary>The counters of the feedback and of the last update.</summary>
    public MeshletPageStreamingTelemetry Telemetry { get; } = new();

    /// <summary>
    /// Adds a geometry whose page table range the pool reserved, and records the pages that the
    /// pool made resident at reservation (normally the root pages).
    /// </summary>
    /// <param name="handle">The geometry handle.</param>
    /// <param name="range">The page table range from <see cref="AdvancedGeometryPagePool.TryReserve(MeshletPageSet, int, out AdvancedGeometryPagePool.PageRange)"/>.</param>
    /// <param name="pages">The page set. The manager loads later pages from its images.</param>
    /// <param name="frame">The current frame. The resident pages count as touched in it.</param>
    public void RegisterGeometry(AdvancedGpuHandle handle, AdvancedGeometryPagePool.PageRange range, MeshletPageSet pages, long frame)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (range.PageCount != pages.PageImages.Length)
            throw new ArgumentException("The page range does not match the page set.", nameof(range));

        Registry.AddGeometry(handle, range.FirstPage, pages, _pool.SlotCount);
        ++Telemetry.RegisteredGeometries;
        ++Telemetry.TotalGeometryRegistrations;
        for (int page = 0; page < range.PageCount; ++page)
        {
            uint globalPage = range.FirstPage + (uint)page;
            uint slot = _pool.GetSlot(globalPage);
            if (slot == AdvancedGeometryPagePool.NotResident)
                continue;
            Registry.MarkResident(globalPage, (int)slot, EMeshletPageRequestBand.Foreground, frame);
            ++Telemetry.ResidentPages;
            Telemetry.ResidentBytes += pages.PageImages[page].Length;
        }
    }

    /// <summary>
    /// Removes a geometry. Call it when the pool retires or releases the geometry's range, which
    /// also retires its slots. Returns false when the geometry is not registered.
    /// </summary>
    public bool UnregisterGeometry(AdvancedGpuHandle handle)
    {
        int index = Registry.FindGeometry(handle);
        if (index == MeshletPageRegistry.NoGeometry)
            return false;

        MeshletPageRegistry.Geometry geometry = Registry.GetGeometry(index);
        for (int page = 0; page < geometry.PageCount; ++page)
        {
            ref MeshletPageRegistry.Page record = ref Registry.GetPage(geometry.FirstPage + (uint)page);
            if (record.State == EMeshletPageResidencyState.Resident)
            {
                --Telemetry.ResidentPages;
                Telemetry.ResidentBytes -= geometry.Pages.PageImages[page].Length;
                ++Telemetry.TotalReleasedPages;
            }
            else if (record.State == EMeshletPageResidencyState.Failed)
                --Telemetry.FailedPages;
        }
        // Removed pages stay in the candidate list of this update; a removed entry fails the
        // state check there.
        Registry.RemoveGeometry(handle);
        --Telemetry.RegisteredGeometries;
        ++Telemetry.TotalGeometryRemovals;
        return true;
    }

    /// <summary>
    /// Merges the GPU feedback of one completed frame: its page requests, its request overflow
    /// count, and its slot touch bits. Call it once for each completed readback ring slot before
    /// <see cref="Update"/>. A request for a page that is resident or failed by now is ignored. The
    /// touch bits are indexed by slot, and each set bit moves the last-touch frame of the page in
    /// that slot to <paramref name="feedbackFrame"/>.
    /// </summary>
    /// <param name="requests">The written requests (at most the request buffer capacity).</param>
    /// <param name="overflowCount">The requests that did not fit in the request buffer.</param>
    /// <param name="touchWords">The touch bits: bit <c>s % 32</c> of word <c>s / 32</c> is slot <c>s</c>.</param>
    /// <param name="feedbackFrame">The frame that wrote the feedback.</param>
    public void SubmitFeedback(ReadOnlySpan<MeshletPageRequest> requests, long overflowCount, ReadOnlySpan<uint> touchWords, long feedbackFrame)
    {
        if (!_feedbackOpen)
        {
            Telemetry.ResetFeedback();
            _feedbackOpen = true;
        }
        Telemetry.LastRequestOverflows += overflowCount;
        Telemetry.TotalRequestOverflows += overflowCount;

        for (int word = 0; word < touchWords.Length; ++word)
        {
            uint bits = touchWords[word];
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int page = Registry.GetSlotPage(word * 32 + bit);
                if (page == MeshletPageRegistry.NoPage)
                    continue;
                ref MeshletPageRegistry.Page record = ref Registry.GetPage((uint)page);
                record.LastTouchFrame = Math.Max(record.LastTouchFrame, feedbackFrame);
            }
        }

        foreach (MeshletPageRequest request in requests)
        {
            if (!Registry.IsRegistered(request.GlobalPage))
                continue;
            if (Registry.GetPage(request.GlobalPage).State != EMeshletPageResidencyState.NotResident)
                continue;
            if (MergeRequest(request.GlobalPage, request.Band, request.Priority, request.PixelScale))
            {
                ++Telemetry.LastRequestedPages;
                Telemetry.CountRequest(request.Band);
            }
        }
    }

    /// <summary>
    /// Runs one residency update over the merged feedback: adds the missing dependency chains,
    /// loads pages within the upload budget, and evicts pages for loads that find no free slot.
    /// Call it on the publisher thread before the publication captures the page table.
    /// </summary>
    /// <param name="frame">The current frame. Pages touched in it are not evicted.</param>
    /// <param name="pixelThreshold">The pixel error threshold of the cut.</param>
    /// <param name="publicationGeneration">The generation of the publication that will capture the changes. Evicted slots retire until it is reclaimed.</param>
    public void Update(long frame, float pixelThreshold, ulong publicationGeneration)
    {
        Telemetry.ResetUpdate();
        if (!_feedbackOpen)
            Telemetry.ResetFeedback();
        _budgetBytes = Math.Max(0L, Policy.UploadBytesPerUpdate);
        _budgetExhausted = false;
        _loadsThisUpdate = 0;
        _waitingLoads = 0;
        _evictionsThisUpdate = 0;
        _evictionLimit = (int)Math.Max(1L, Policy.UploadBytesPerUpdate / AdvancedGeometryPagePool.SlotBytes);
        _candidatesBuilt = false;

        AddDependencyChains(frame, pixelThreshold);
        SortRequests();

        for (int index = 0; index < _requestedCount && !_budgetExhausted; ++index)
        {
            uint page = _requested[index];
            ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
            if (record.GeometryIndex == MeshletPageRegistry.NoGeometry || record.State != EMeshletPageResidencyState.NotResident)
                continue;
            TryLoadChain(page, record.RequestBand, frame, publicationGeneration, 0);
        }

        TotalsAfterUpdate();
        _requestedCount = 0;
        _feedbackOpen = false;
        ++_updateIndex;
    }

    /// <summary>
    /// Adds a request for a page to the request list of this update, or raises the band, priority
    /// and pixel scale of the request that is already there. Returns true for a new entry.
    /// </summary>
    private bool MergeRequest(uint page, EMeshletPageRequestBand band, ushort priority, float pixelScale)
    {
        ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
        if (record.RequestUpdate == _updateIndex)
        {
            if (band > record.RequestBand)
                record.RequestBand = band;
            if (priority > record.RequestPriority)
                record.RequestPriority = priority;
            record.RequestPixelScale = MathF.Max(record.RequestPixelScale, pixelScale);
            return false;
        }

        record.RequestUpdate = _updateIndex;
        record.RequestBand = band;
        record.RequestPriority = priority;
        record.RequestPixelScale = pixelScale;
        if (_requestedCount == _requested.Length)
        {
            Array.Resize(ref _requested, _requested.Length * 2);
            Array.Resize(ref _requestOrderKeys, _requested.Length);
        }
        _requested[_requestedCount++] = page;
        return true;
    }

    /// <summary>
    /// Adds each missing page on the dependency chain of each requested page, so one round trip
    /// loads the whole chain down to the ideal cut. An added page takes at least the priority of
    /// the request, and the foreground band when its largest group error times the request's pixel
    /// scale is above the threshold: the cover that draws while it is missing shows a visible error.
    /// The list grows while the loop runs, so the chain of an added page is added too. A resident
    /// dependency counts as touched in this frame: its clusters draw until the requested page
    /// arrives, so a load in this update must not evict it.
    /// </summary>
    private void AddDependencyChains(long frame, float pixelThreshold)
    {
        for (int index = 0; index < _requestedCount; ++index)
        {
            uint page = _requested[index];
            ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
            if (record.GeometryIndex == MeshletPageRegistry.NoGeometry)
                continue;

            EMeshletPageRequestBand band = record.RequestBand;
            ushort priority = record.RequestPriority;
            float pixelScale = record.RequestPixelScale;
            MeshletPageRegistry.Geometry geometry = Registry.GetGeometry(record.GeometryIndex);
            foreach (uint dependency in geometry.Pages.Table.GetDependencies(record.LocalPage))
            {
                uint dependencyPage = geometry.FirstPage + dependency;
                ref MeshletPageRegistry.Page dependencyRecord = ref Registry.GetPage(dependencyPage);
                if (dependencyRecord.State == EMeshletPageResidencyState.Resident)
                {
                    dependencyRecord.LastTouchFrame = Math.Max(dependencyRecord.LastTouchFrame, frame);
                    continue;
                }
                if (dependencyRecord.State != EMeshletPageResidencyState.NotResident)
                    continue;
                EMeshletPageRequestBand dependencyBand = dependencyRecord.MaxGroupError * pixelScale > pixelThreshold
                    ? EMeshletPageRequestBand.Foreground
                    : band;
                MergeRequest(dependencyPage, dependencyBand > band ? dependencyBand : band, priority, pixelScale);
            }
        }
    }

    /// <summary>Orders the request list by band, then by priority, both from high to low.</summary>
    private void SortRequests()
    {
        for (int index = 0; index < _requestedCount; ++index)
        {
            ref MeshletPageRegistry.Page record = ref Registry.GetPage(_requested[index]);
            ulong bandKey = (ulong)(EMeshletPageRequestBand.Foreground - record.RequestBand);
            ulong priorityKey = (ulong)(ushort.MaxValue - record.RequestPriority);
            _requestOrderKeys[index] = (bandKey << 48) | (priorityKey << 32) | (uint)index;
        }
        _requestOrderKeys.AsSpan(0, _requestedCount).Sort(_requested.AsSpan(0, _requestedCount));
    }

    /// <summary>
    /// Loads a page after its dependencies, loading missing dependencies first. Returns true when
    /// the page is resident at the end. A failed or waiting dependency stops the chain, and its
    /// parents keep drawing.
    /// </summary>
    private bool TryLoadChain(uint page, EMeshletPageRequestBand band, long frame, ulong publicationGeneration, int depth)
    {
        if (depth > MaxDependencyDepth || _budgetExhausted)
            return false;

        ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
        if (record.State == EMeshletPageResidencyState.Resident)
            return true;
        if (record.State == EMeshletPageResidencyState.Failed || record.GeometryIndex == MeshletPageRegistry.NoGeometry)
            return false;

        MeshletPageRegistry.Geometry geometry = Registry.GetGeometry(record.GeometryIndex);
        int localPage = record.LocalPage;
        foreach (uint dependency in geometry.Pages.Table.GetDependencies(localPage))
            if (!TryLoadChain(geometry.FirstPage + dependency, band, frame, publicationGeneration, depth + 1))
                return false;

        return TryLoadPage(page, geometry, localPage, band, frame, publicationGeneration);
    }

    private bool TryLoadPage(uint page, MeshletPageRegistry.Geometry geometry, int localPage, EMeshletPageRequestBand band, long frame, ulong publicationGeneration)
    {
        byte[]? image = geometry.Pages.PageImages[localPage];
        if (image is not { Length: > 0 and <= AdvancedGeometryPagePool.SlotBytes })
        {
            MarkFailed(page, geometry, localPage);
            return false;
        }
        if (_loadsThisUpdate > 0 && image.Length > _budgetBytes)
        {
            _budgetExhausted = true;
            return false;
        }
        if (_pool.FreeSlotCount == 0)
        {
            // The load waits for a slot. Evict only when the slots that are already retiring do
            // not cover the loads that wait in this update.
            ++_waitingLoads;
            ++Telemetry.LastLoadsWithoutSlot;
            if (_waitingLoads > _pool.RetiringSlotCount)
                TryEvictFor(band, frame, publicationGeneration);
            return false;
        }
        if (!_pool.TryLoadPage(page, image, out int slot))
            return false;

        Registry.MarkResident(page, slot, band, frame);
        _budgetBytes -= image.Length;
        ++_loadsThisUpdate;
        ++Telemetry.LastLoads;
        Telemetry.LastLoadedBytes += image.Length;
        ++Telemetry.ResidentPages;
        Telemetry.ResidentBytes += image.Length;
        return true;
    }

    private void MarkFailed(uint page, MeshletPageRegistry.Geometry geometry, int localPage)
    {
        ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
        record.State = EMeshletPageResidencyState.Failed;
        ++Telemetry.FailedPages;
        Debug.RenderingWarning($"Geometry page {localPage} of geometry {geometry.Handle.Index}:{geometry.Handle.Generation} has no valid page image. It is not requested again, and its parent pages keep drawing.");
    }

    /// <summary>
    /// Evicts the oldest candidate that a load of <paramref name="loadBand"/> can evict. Returns
    /// false when no candidate is old enough or every old candidate was loaded for a higher band.
    /// </summary>
    private bool TryEvictFor(EMeshletPageRequestBand loadBand, long frame, ulong publicationGeneration)
    {
        if (_evictionsThisUpdate >= _evictionLimit)
            return false;
        if (!_candidatesBuilt)
            BuildEvictionCandidates(frame);

        int minimumAge = Policy.MinimumEvictionAge(loadBand);
        for (int index = _candidateCursor; index < _candidateCount; ++index)
        {
            uint page = _candidates[index];
            if (page == ConsumedCandidate)
                continue;

            ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
            if (record.GeometryIndex == MeshletPageRegistry.NoGeometry ||
                record.State != EMeshletPageResidencyState.Resident ||
                record.ResidentChildCount != 0 ||
                record.LastTouchFrame >= frame)
            {
                ConsumeCandidate(index);
                continue;
            }

            long age = frame - record.LastTouchFrame;
            // The candidates are sorted by last-touch frame, so every later candidate is younger.
            if (age < minimumAge)
                return false;
            if (!MeshletPageResidencyPolicy.MayEvict(record.ResidentBand, loadBand))
                continue;

            Evict(page, record, publicationGeneration, age < Policy.EvictionCooldownFrames);
            ConsumeCandidate(index);
            return true;
        }
        return false;
    }

    private void Evict(uint page, in MeshletPageRegistry.Page record, ulong publicationGeneration, bool underForegroundFloor)
    {
        MeshletPageRegistry.Geometry geometry = Registry.GetGeometry(record.GeometryIndex);
        int imageBytes = geometry.Pages.PageImages[record.LocalPage]?.Length ?? 0;
        if (!_pool.EvictPage(page, publicationGeneration))
            return;

        Registry.MarkEvicted(page);
        ++_evictionsThisUpdate;
        ++Telemetry.LastEvictions;
        if (underForegroundFloor)
            ++Telemetry.LastForegroundEvictions;
        --Telemetry.ResidentPages;
        Telemetry.ResidentBytes -= imageBytes;
    }

    private void ConsumeCandidate(int index)
    {
        _candidates[index] = ConsumedCandidate;
        while (_candidateCursor < _candidateCount && _candidates[_candidateCursor] == ConsumedCandidate)
            ++_candidateCursor;
    }

    /// <summary>
    /// Collects the eviction candidates once per update: resident pages that are not root pages,
    /// have no resident children, and were not touched in this frame. Sorts them by last-touch
    /// frame, oldest first.
    /// </summary>
    private void BuildEvictionCandidates(long frame)
    {
        _candidatesBuilt = true;
        _candidateCount = 0;
        _candidateCursor = 0;
        for (int geometryIndex = 0; geometryIndex < Registry.GeometryListLength; ++geometryIndex)
        {
            if (!Registry.TryGetGeometry(geometryIndex, out MeshletPageRegistry.Geometry? geometry))
                continue;

            for (int localPage = geometry.RootPageCount; localPage < geometry.PageCount; ++localPage)
            {
                uint page = geometry.FirstPage + (uint)localPage;
                ref MeshletPageRegistry.Page record = ref Registry.GetPage(page);
                if (record.State != EMeshletPageResidencyState.Resident || record.ResidentChildCount != 0 || record.LastTouchFrame >= frame)
                    continue;

                if (_candidateCount == _candidates.Length)
                {
                    Array.Resize(ref _candidates, _candidates.Length * 2);
                    Array.Resize(ref _candidateKeys, _candidates.Length);
                }
                _candidates[_candidateCount] = page;
                _candidateKeys[_candidateCount] = record.LastTouchFrame;
                ++_candidateCount;
            }
        }
        _candidateKeys.AsSpan(0, _candidateCount).Sort(_candidates.AsSpan(0, _candidateCount));
    }

    private void TotalsAfterUpdate()
    {
        Telemetry.TotalLoads += Telemetry.LastLoads;
        Telemetry.TotalEvictions += Telemetry.LastEvictions;
    }
}
