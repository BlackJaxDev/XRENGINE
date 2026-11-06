namespace XREngine.Cutscenes;

/// <summary>
/// Decides which levels of detail of the cutscene assets must be resident, and by which time,
/// around the playhead. It sends the requests to an <see cref="IResidencyBackend"/> in priority order.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Open"/> one time. It requests T0 for each asset of the manifest, and the T0 data stays
/// resident until <see cref="Close"/>. Then call <see cref="Update"/> one time in each frame on the update thread.
/// An update plans again only when the playhead goes into another bucket, the request window covers other buckets,
/// the rate changes, a seek occurs, the dwell gate changes, or a load completes. Otherwise it does a constant
/// amount of work and allocates no memory.
/// </para>
/// <para>
/// The planner requests one level above the resident level of an asset at a time. When that load completes,
/// the next update requests the level after it, until the asset has the level that the request window needs.
/// Requests sort by level, then by the time until the playhead needs them, then by screen coverage in
/// descending order.
/// </para>
/// </remarks>
public sealed class CutsceneResidencyPlanner
{
    /// <summary>The default time that a scrub must stay in one place before T1 and T2 work starts: 120 ms.</summary>
    public const double DefaultDwellSeconds = 0.120;

    /// <summary>The default look-ahead at rate 1: 5 seconds. The look-ahead scales with the absolute rate.</summary>
    public const double DefaultLookAheadSeconds = 5.0;

    /// <summary>The default window behind the playhead: 0.5 seconds.</summary>
    public const double DefaultLookBehindSeconds = 0.5;

    /// <summary>The default distance that a scrub can move and still count as one place for the dwell gate: 50 ms.</summary>
    public const double DefaultDwellMoveThresholdSeconds = 0.050;

    /// <summary>The default maximum playback wait at a camera cut: 1 second.</summary>
    public const double DefaultPlaybackWaitMaximumSeconds = 1.0;

    /// <summary>The largest window span in flicks. It keeps the window ends away from an overflow at a very large rate.</summary>
    private const long MaxWindowSpan = long.MaxValue / 4;

    private readonly IResidencyBackend _backend;
    private readonly Guid[] _assets;
    private readonly ResidencyBucket[] _buckets;
    private readonly long[] _bucketStarts;
    private readonly long[] _bucketEnds;

    private readonly long[] _firstNeeded;
    private readonly float[] _maxCoverage;
    private readonly long[] _lastNeeded;

    private readonly ResidencyTicket[] _proxyTickets;
    private readonly ResidencyTicket[] _pendingTickets;
    private readonly int[] _pendingLevels;
    private readonly int[] _pendingGenerations;
    private readonly ResidencyTicket[] _heldTickets;
    private readonly int[] _heldLevels;

    private readonly int[] _desiredLevels;
    private readonly long[] _untilNeeded;
    private readonly long[] _deadlines;
    private readonly float[] _coverages;
    private readonly int[] _requestLevels;
    private readonly int[] _order;

    private readonly Dictionary<long, int> _ticketAssets;
    private readonly Comparison<int> _compareRequests;
    private readonly Comparison<int> _compareProxyRequests;
    private readonly Comparison<int> _compareEviction;

    private readonly Lock _completionLock = new();
    private List<ResidencyTicket> _incoming;
    private List<ResidencyTicket> _draining;
    private int _incomingCount;

    private bool _opened;
    private bool _closed;
    private bool _dirty;
    private bool _hasPosition;
    private long _position;
    private double _rate = double.NaN;
    private int _generation;
    private int _pendingProxyCount;

    private int _currentBucket = -1;
    private int _windowFirst;
    private int _windowLast = -1;

    private bool _scrubbing;
    private bool _dwellOpen;
    private long _dwellAnchor;
    private double _dwellSeconds;

    private bool _holding;
    private int _holdBucket = -1;
    private int _releasedCutBucket = -1;
    private double _holdSeconds;
    private int _playbackWaitTimeouts;

    private double _dwellTimeSeconds = DefaultDwellSeconds;
    private double _playbackWaitMaximumSeconds = DefaultPlaybackWaitMaximumSeconds;

    /// <summary>Creates a planner for one open cutscene.</summary>
    /// <param name="manifest">The residency manifest of the cutscene.</param>
    /// <param name="backend">The backend that loads and releases the levels.</param>
    public CutsceneResidencyPlanner(CutsceneResidencyManifest manifest, IResidencyBackend backend)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(backend);

        Manifest = manifest;
        _backend = backend;
        _assets = manifest.Assets;
        _buckets = manifest.Buckets;

        int bucketCount = _buckets.Length;
        _bucketStarts = new long[bucketCount];
        _bucketEnds = new long[bucketCount];
        for (int b = 0; b < bucketCount; b++)
        {
            _bucketStarts[b] = _buckets[b].Start.Flicks;
            _bucketEnds[b] = _buckets[b].End.Flicks;
        }

        int assetCount = _assets.Length;
        _firstNeeded = new long[assetCount];
        _maxCoverage = new float[assetCount];
        _lastNeeded = new long[assetCount];
        _proxyTickets = new ResidencyTicket[assetCount];
        _pendingTickets = new ResidencyTicket[assetCount];
        _pendingLevels = new int[assetCount];
        _pendingGenerations = new int[assetCount];
        _heldTickets = new ResidencyTicket[assetCount];
        _heldLevels = new int[assetCount];
        _desiredLevels = new int[assetCount];
        _untilNeeded = new long[assetCount];
        _deadlines = new long[assetCount];
        _coverages = new float[assetCount];
        _requestLevels = new int[assetCount];
        _order = new int[assetCount];

        Array.Fill(_firstNeeded, long.MaxValue);
        for (int b = bucketCount - 1; b >= 0; b--)
        {
            ResidencyEntry[] entries = _buckets[b].Entries;
            for (int e = 0; e < entries.Length; e++)
            {
                ResidencyEntry entry = entries[e];
                _firstNeeded[entry.AssetIndex] = _bucketStarts[b];
                if (entry.ScreenCoverage > _maxCoverage[entry.AssetIndex])
                    _maxCoverage[entry.AssetIndex] = entry.ScreenCoverage;
            }
        }
        Array.Copy(_firstNeeded, _lastNeeded, assetCount);

        // Each asset has at most one T0 ticket and one pending ticket in the map at a time.
        _ticketAssets = new Dictionary<long, int>(assetCount * 2);
        _incoming = new List<ResidencyTicket>(assetCount);
        _draining = new List<ResidencyTicket>(assetCount);

        // Cache the delegates one time, so that a sort does not allocate.
        _compareRequests = CompareRequests;
        _compareProxyRequests = CompareProxyRequests;
        _compareEviction = CompareEviction;
    }

    /// <summary>
    /// Occurs in <see cref="Update"/> for each load of the current generation that completed.
    /// The arguments are the asset and the level that is resident now. It does not occur after <see cref="Close"/>.
    /// </summary>
    public event Action<Guid, int>? LoadCompleted;

    /// <summary>The manifest that the planner uses.</summary>
    public CutsceneResidencyManifest Manifest { get; }

    /// <summary>
    /// The time in seconds that a scrub must stay within <see cref="DwellMoveThreshold"/> before T1 and T2 work starts.
    /// A jump does not use this gate.
    /// </summary>
    public double DwellSeconds
    {
        get => _dwellTimeSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _dwellTimeSeconds = value;
        }
    }

    /// <summary>The distance that a scrub can move and still count as one place for the dwell gate.</summary>
    public CutsceneTime DwellMoveThreshold { get; set; } = CutsceneTime.FromSeconds(DefaultDwellMoveThresholdSeconds);

    /// <summary>The look-ahead in the direction of travel at rate 1. The window uses this value multiplied by the absolute rate.</summary>
    public CutsceneTime LookAhead { get; set; } = CutsceneTime.FromSeconds(DefaultLookAheadSeconds);

    /// <summary>The window behind the playhead, opposite to the direction of travel. The rate does not scale it.</summary>
    public CutsceneTime LookBehind { get; set; } = CutsceneTime.FromSeconds(DefaultLookBehindSeconds);

    /// <summary>
    /// True if forward playback holds at a camera cut until the next shot has its target levels.
    /// The default is false. A jump or a scrub never holds.
    /// </summary>
    public bool PlaybackWaitEnabled { get; set; }

    /// <summary>The maximum time in seconds that one playback wait holds. It must be greater than zero.</summary>
    public double PlaybackWaitMaximumSeconds
    {
        get => _playbackWaitMaximumSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _playbackWaitMaximumSeconds = value;
        }
    }

    /// <summary>True after <see cref="Open"/> and before <see cref="Close"/>.</summary>
    public bool IsOpen => _opened && !_closed;

    /// <summary>True after <see cref="Close"/>.</summary>
    public bool IsClosed => _closed;

    /// <summary>The generation of new requests. Each jump increments it.</summary>
    public int Generation => _generation;

    /// <summary>The number of T0 loads that did not complete yet. At zero, every asset has its proxy.</summary>
    public int PendingProxyCount => _pendingProxyCount;

    /// <summary>
    /// True if the playback wait holds now. The caller must keep the clock, and the audio with it,
    /// at <see cref="PlaybackHoldTime"/> while this value is true.
    /// </summary>
    public bool IsHoldingPlayback => _holding;

    /// <summary>The time of the camera cut at which the playback wait holds. It is zero when no wait holds.</summary>
    public CutsceneTime PlaybackHoldTime => _holding ? _buckets[_holdBucket].Start : CutsceneTime.Zero;

    /// <summary>The number of playback waits that ended at <see cref="PlaybackWaitMaximumSeconds"/> before the targets were resident.</summary>
    public int PlaybackWaitTimeoutCount => _playbackWaitTimeouts;

    /// <summary>
    /// Requests T0 for each asset of the manifest, in the order of the first time that the playhead needs it.
    /// The T0 data stays resident until <see cref="Close"/>, and <see cref="Evict"/> never removes it.
    /// </summary>
    public void Open()
    {
        if (_closed)
            throw new InvalidOperationException("A closed residency planner cannot open again. Create a new planner.");
        if (_opened)
            throw new InvalidOperationException("The residency planner is already open.");

        _opened = true;
        _dirty = true;

        int count = _assets.Length;
        for (int i = 0; i < count; i++)
            _order[i] = i;
        _order.AsSpan(0, count).Sort(_compareProxyRequests);

        for (int k = 0; k < count && !_closed; k++)
        {
            int asset = _order[k];
            ResidencyRequest request = new(_assets[asset], (int)EResidencyLevel.Proxy, new CutsceneTime(_firstNeeded[asset]), _maxCoverage[asset], _generation);
            ResidencyTicket ticket = _backend.Request(in request);
            if (!ticket.IsValid)
                continue;

            _proxyTickets[asset] = ticket;
            _ticketAssets[ticket.Id] = asset;
            _pendingProxyCount++;
        }
    }

    /// <summary>
    /// Updates the plan for the playhead. Call it one time in each frame on the update thread.
    /// After <see cref="Close"/>, this method does nothing.
    /// </summary>
    /// <param name="position">The playhead position after the clock step and any seek of this frame.</param>
    /// <param name="rate">The playback rate. A negative rate plays backward. Zero is a pause.</param>
    /// <param name="seek">The seek that moved the playhead in this frame, or null if no seek occurred.</param>
    /// <param name="deltaSeconds">The wall time of this frame in seconds. The dwell gate and the playback wait use it.</param>
    public void Update(CutsceneTime position, double rate, ESeekIntent? seek, double deltaSeconds)
    {
        if (_closed)
            return;
        if (!_opened)
            throw new InvalidOperationException("Call Open before Update.");
        if (double.IsNaN(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), "The rate must be a number.");
        if (!(deltaSeconds >= 0.0))
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "The frame time must be zero or more.");

        DrainCompletions();
        if (_closed)
            return;

        long previous = _position;
        bool hadPosition = _hasPosition;
        long now = position.Flicks;
        _position = now;
        _hasPosition = true;

        if (seek == ESeekIntent.Jump)
        {
            _generation++;
            CancelStalePending();
            _scrubbing = false;
            ReleaseHold();
            _releasedCutBucket = -1;
            _dirty = true;
        }
        else if (seek == ESeekIntent.Scrub)
        {
            ReleaseHold();
            _releasedCutBucket = -1;
            if (_scrubbing)
                UpdateDwell(now, deltaSeconds);
            else
                BeginScrub(now);
        }
        else if (_scrubbing)
        {
            if (rate != 0.0)
            {
                _scrubbing = false;
                _dirty = true;
            }
            else
                UpdateDwell(now, deltaSeconds);
        }

        if (rate != _rate)
        {
            _rate = rate;
            _dirty = true;
        }

        int current = FindBucket(now);
        if (seek is null && !_scrubbing && hadPosition)
            UpdatePlaybackWait(previous, now, rate, _currentBucket, current, deltaSeconds);

        if (current != _currentBucket)
        {
            _currentBucket = current;
            _dirty = true;
        }

        FindWindow(now, rate, out int first, out int last);
        if (first != _windowFirst || last != _windowLast)
        {
            _windowFirst = first;
            _windowLast = last;
            _dirty = true;
        }

        if (_dirty)
            Replan();
    }

    /// <summary>
    /// Records that the work of a ticket is complete. Call it from any thread.
    /// The next <see cref="Update"/> applies the result. After <see cref="Close"/>, the call does nothing.
    /// </summary>
    /// <param name="ticket">The ticket of the complete work.</param>
    public void NotifyCompleted(ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        lock (_completionLock)
        {
            if (_closed)
                return;

            _incoming.Add(ticket);
            Volatile.Write(ref _incomingCount, _incoming.Count);
        }
    }

    /// <summary>
    /// Releases T1 and T2 data under memory pressure, starting with the data that the playhead needs
    /// farthest from its position in time. T0 data never leaves while the planner is open.
    /// A later plan can request the released levels again.
    /// </summary>
    /// <param name="maxCount">The maximum number of assets to release.</param>
    /// <returns>The number of assets that released a level.</returns>
    public int Evict(int maxCount)
    {
        if (!IsOpen || maxCount <= 0)
            return 0;

        int count = 0;
        for (int i = 0; i < _assets.Length; i++)
            if (_heldTickets[i].IsValid)
                _order[count++] = i;

        _order.AsSpan(0, count).Sort(_compareEviction);

        int evicted = Math.Min(count, maxCount);
        for (int k = 0; k < evicted; k++)
        {
            int asset = _order[k];
            ResidencyTicket held = _heldTickets[asset];
            _heldTickets[asset] = ResidencyTicket.None;
            _heldLevels[asset] = 0;
            _backend.Cancel(held);
        }
        return evicted;
    }

    /// <summary>
    /// Cancels all work, releases all levels, including T0, and stops the playback wait.
    /// After this call, no completion runs a callback, and <see cref="Update"/> does nothing.
    /// A second call does nothing.
    /// </summary>
    public void Close()
    {
        lock (_completionLock)
        {
            if (_closed)
                return;

            _closed = true;
            _incoming.Clear();
            _draining.Clear();
            Volatile.Write(ref _incomingCount, 0);
        }

        _holding = false;
        _holdBucket = -1;
        _ticketAssets.Clear();

        // Cancel the running work first, so that the backend does not complete it after the release of the data.
        for (int i = 0; i < _assets.Length; i++)
            CancelAndClear(ref _pendingTickets[i]);
        for (int i = 0; i < _assets.Length; i++)
        {
            CancelAndClear(ref _heldTickets[i]);
            _heldLevels[i] = 0;
        }
        for (int i = 0; i < _assets.Length; i++)
            CancelAndClear(ref _proxyTickets[i]);

        _pendingProxyCount = 0;
    }

    private void CancelAndClear(ref ResidencyTicket ticket)
    {
        if (!ticket.IsValid)
            return;

        ResidencyTicket cancelled = ticket;
        ticket = ResidencyTicket.None;
        _backend.Cancel(cancelled);
    }

    private void DrainCompletions()
    {
        if (Volatile.Read(ref _incomingCount) == 0)
            return;

        List<ResidencyTicket> batch;
        lock (_completionLock)
        {
            batch = _incoming;
            _incoming = _draining;
            _draining = batch;
            Volatile.Write(ref _incomingCount, 0);
        }

        // A handler can call Close. Close clears the batch, so the loop reads the count each time.
        for (int k = 0; k < batch.Count && !_closed; k++)
            ApplyCompletion(batch[k]);
        batch.Clear();
    }

    private void ApplyCompletion(ResidencyTicket ticket)
    {
        // A ticket that is not in the map was cancelled, for example by a jump. Its data can stay in a
        // backend cache, but it does not change the plan.
        if (!_ticketAssets.Remove(ticket.Id, out int asset))
            return;

        if (_proxyTickets[asset] == ticket)
        {
            _pendingProxyCount--;
            LoadCompleted?.Invoke(_assets[asset], (int)EResidencyLevel.Proxy);
            return;
        }

        if (_pendingTickets[asset] != ticket)
            return;

        int level = _pendingLevels[asset];
        _pendingTickets[asset] = ResidencyTicket.None;

        // The new hold covers the lower level, so the lower hold is released.
        CancelAndClear(ref _heldTickets[asset]);
        _heldTickets[asset] = ticket;
        _heldLevels[asset] = level;
        _dirty = true;
        LoadCompleted?.Invoke(_assets[asset], level);
    }

    private void CancelStalePending()
    {
        for (int i = 0; i < _assets.Length; i++)
            if (_pendingTickets[i].IsValid && _pendingGenerations[i] < _generation)
                CancelPending(i);
    }

    private void CancelPending(int asset)
    {
        ResidencyTicket ticket = _pendingTickets[asset];
        if (!ticket.IsValid)
            return;

        _pendingTickets[asset] = ResidencyTicket.None;
        _ticketAssets.Remove(ticket.Id);
        _backend.Cancel(ticket);
    }

    private void BeginScrub(long position)
    {
        _scrubbing = true;
        _dwellOpen = false;
        _dwellAnchor = position;
        _dwellSeconds = 0.0;
        _dirty = true;
    }

    private void UpdateDwell(long position, double deltaSeconds)
    {
        if (Math.Abs(position - _dwellAnchor) > DwellMoveThreshold.Flicks)
        {
            _dwellAnchor = position;
            _dwellSeconds = 0.0;
            if (_dwellOpen)
            {
                _dwellOpen = false;
                _dirty = true;
            }
            return;
        }

        if (_dwellOpen)
            return;

        _dwellSeconds += deltaSeconds;
        if (_dwellSeconds >= _dwellTimeSeconds)
        {
            _dwellOpen = true;
            _dirty = true;
        }
    }

    private void UpdatePlaybackWait(long previous, long position, double rate, int previousBucket, int currentBucket, double deltaSeconds)
    {
        if (_holding)
        {
            if (!PlaybackWaitEnabled || rate <= 0.0 || TargetsResident(_holdBucket))
            {
                ReleaseHold();
                return;
            }

            _holdSeconds += deltaSeconds;
            if (_holdSeconds >= _playbackWaitMaximumSeconds)
            {
                _playbackWaitTimeouts++;
                ReleaseHold();
            }
            return;
        }

        if (!PlaybackWaitEnabled || rate <= 0.0 || currentBucket <= previousBucket)
            return;

        for (int b = previousBucket + 1; b <= currentBucket; b++)
        {
            if (!_buckets[b].StartsAtCut || b == _releasedCutBucket)
                continue;

            long cut = _bucketStarts[b];
            if (cut <= previous || cut > position || TargetsResident(b))
                continue;

            _holding = true;
            _holdBucket = b;
            _holdSeconds = 0.0;
            return;
        }
    }

    private void ReleaseHold()
    {
        if (!_holding)
            return;

        _holding = false;
        _releasedCutBucket = _holdBucket;
        _holdBucket = -1;
    }

    private bool TargetsResident(int bucket)
    {
        ResidencyEntry[] entries = _buckets[bucket].Entries;
        for (int e = 0; e < entries.Length; e++)
        {
            ResidencyEntry entry = entries[e];
            if ((int)_backend.GetLevel(_assets[entry.AssetIndex]) < entry.TargetLevel)
                return false;
        }
        return true;
    }

    private int FindBucket(long position)
    {
        int index = Array.BinarySearch(_bucketStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    private void FindWindow(long position, double rate, out int first, out int last)
    {
        double scaled = LookAhead.Flicks * Math.Abs(rate);
        long ahead = scaled >= MaxWindowSpan ? MaxWindowSpan : (long)scaled;
        long behind = Math.Min(LookBehind.Flicks, MaxWindowSpan);

        long low;
        long high;
        if (rate < 0.0)
        {
            low = position - ahead;
            high = position + behind;
        }
        else
        {
            low = position - behind;
            high = position + ahead;
        }

        // The first bucket that ends after the low edge. A bucket end is not part of the bucket.
        int firstIndex = Array.BinarySearch(_bucketEnds, low);
        first = firstIndex >= 0 ? firstIndex + 1 : ~firstIndex;

        // The last bucket that starts at or before the high edge.
        int lastIndex = Array.BinarySearch(_bucketStarts, high);
        last = lastIndex >= 0 ? lastIndex : ~lastIndex - 1;

        if (first > last)
        {
            first = 0;
            last = -1;
        }
    }

    private void Replan()
    {
        _dirty = false;

        int assetCount = _assets.Length;
        Array.Clear(_desiredLevels);
        Array.Fill(_untilNeeded, long.MaxValue);
        Array.Clear(_coverages);

        long position = _position;
        bool upperAllowed = !_scrubbing || _dwellOpen;
        if (upperAllowed)
        {
            for (int b = _windowFirst; b <= _windowLast; b++)
            {
                // The nearest time of the bucket to the playhead. It is the playhead itself for the current bucket.
                long deadline = Math.Clamp(position, _bucketStarts[b], _bucketEnds[b] - 1);
                long until = Math.Abs(deadline - position);

                ResidencyEntry[] entries = _buckets[b].Entries;
                for (int e = 0; e < entries.Length; e++)
                {
                    ResidencyEntry entry = entries[e];
                    int asset = entry.AssetIndex;
                    if (entry.TargetLevel > _desiredLevels[asset])
                        _desiredLevels[asset] = entry.TargetLevel;
                    if (until < _untilNeeded[asset])
                    {
                        _untilNeeded[asset] = until;
                        _deadlines[asset] = deadline;
                    }
                    if (entry.ScreenCoverage > _coverages[asset])
                        _coverages[asset] = entry.ScreenCoverage;
                }
            }
        }

        int count = 0;
        for (int i = 0; i < assetCount; i++)
        {
            int desired = _desiredLevels[i];
            if (desired <= 0)
            {
                CancelPending(i);
                continue;
            }

            _lastNeeded[i] = _deadlines[i];
            int resident = Math.Max((int)_backend.GetLevel(_assets[i]), _heldLevels[i]);
            if (desired <= resident)
            {
                CancelPending(i);
                continue;
            }

            int next = Math.Max(resident + 1, 1);
            if (_pendingTickets[i].IsValid && _pendingLevels[i] == next && _pendingGenerations[i] == _generation)
                continue;

            CancelPending(i);
            _requestLevels[i] = next;
            _order[count++] = i;
        }

        _order.AsSpan(0, count).Sort(_compareRequests);

        for (int k = 0; k < count && !_closed; k++)
        {
            int asset = _order[k];
            ResidencyRequest request = new(_assets[asset], _requestLevels[asset], new CutsceneTime(_deadlines[asset]), _coverages[asset], _generation);
            ResidencyTicket ticket = _backend.Request(in request);
            if (!ticket.IsValid)
                continue;

            _pendingTickets[asset] = ticket;
            _pendingLevels[asset] = request.TargetLevel;
            _pendingGenerations[asset] = _generation;
            _ticketAssets[ticket.Id] = asset;
        }
    }

    private int CompareRequests(int left, int right)
    {
        int result = _requestLevels[left].CompareTo(_requestLevels[right]);
        if (result != 0)
            return result;

        result = _untilNeeded[left].CompareTo(_untilNeeded[right]);
        if (result != 0)
            return result;

        result = _coverages[right].CompareTo(_coverages[left]);
        return result != 0 ? result : left.CompareTo(right);
    }

    private int CompareProxyRequests(int left, int right)
    {
        int result = _firstNeeded[left].CompareTo(_firstNeeded[right]);
        if (result != 0)
            return result;

        result = _maxCoverage[right].CompareTo(_maxCoverage[left]);
        return result != 0 ? result : left.CompareTo(right);
    }

    private int CompareEviction(int left, int right)
    {
        long leftDistance = DistanceFromPlayhead(_lastNeeded[left]);
        long rightDistance = DistanceFromPlayhead(_lastNeeded[right]);
        int result = rightDistance.CompareTo(leftDistance);
        return result != 0 ? result : left.CompareTo(right);
    }

    private long DistanceFromPlayhead(long time)
    {
        // An asset that no bucket needs has the time long.MaxValue. A subtraction must not overflow.
        if (time == long.MaxValue)
            return long.MaxValue;

        long distance = time - _position;
        return distance < 0 ? -distance : distance;
    }
}
