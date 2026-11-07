using XREngine.Rendering.Residency;

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
public sealed partial class CutsceneResidencyPlanner
{
    /// <summary>The default look-ahead at rate 1: 5 seconds. The look-ahead scales with the absolute rate.</summary>
    public const double DefaultLookAheadSeconds = 5.0;

    /// <summary>The default window behind the playhead: 0.5 seconds.</summary>
    public const double DefaultLookBehindSeconds = 0.5;

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

    private readonly CutsceneResidencyBucketCursor _cursor;
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

    private CutsceneTime _lookAhead = CutsceneTime.FromSeconds(DefaultLookAheadSeconds);
    private CutsceneTime _lookBehind = CutsceneTime.FromSeconds(DefaultLookBehindSeconds);

    /// <summary>Creates a planner for one open cutscene.</summary>
    /// <param name="manifest">
    /// The residency manifest of the cutscene. The constructor calls <see cref="CutsceneResidencyManifest.Validate"/>.
    /// Do not change its arrays after this call.
    /// </param>
    /// <param name="backend">The backend that loads and releases the levels.</param>
    public CutsceneResidencyPlanner(CutsceneResidencyManifest manifest, IResidencyBackend backend)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(backend);
        manifest.Validate();

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

        _cursor = new CutsceneResidencyBucketCursor(manifest);
        Array.Fill(_firstNeeded, long.MaxValue);
        for (int b = 0; b < bucketCount; b++)
        {
            _cursor.MoveTo(b);
            ReadOnlySpan<int> members = _cursor.Members;
            for (int e = 0; e < members.Length; e++)
            {
                int asset = members[e];
                if (_firstNeeded[asset] == long.MaxValue)
                    _firstNeeded[asset] = _bucketStarts[b];
                float coverage = _cursor.GetCoverage(asset);
                if (coverage > _maxCoverage[asset])
                    _maxCoverage[asset] = coverage;
            }
        }
        Array.Copy(_firstNeeded, _lastNeeded, assetCount);

        // Each asset has at most one T0 ticket and one pending ticket in the map at a time.
        // Late completions of cancelled tickets can also arrive, so the lists have the same capacity.
        _ticketAssets = new Dictionary<long, int>(assetCount * 2);
        _incoming = new List<ResidencyTicket>(assetCount * 2);
        _draining = new List<ResidencyTicket>(assetCount * 2);

        // Cache the delegates one time, so that a sort does not allocate.
        _compareRequests = CompareRequests;
        _compareProxyRequests = CompareProxyRequests;
        _compareEviction = CompareEviction;
    }

    /// <summary>
    /// Occurs in <see cref="Update"/> for each completed load that the planner still tracks: a T0 load, or T1 and T2 work
    /// of the current generation. Work that a jump, the window, or the dwell gate cancelled does not raise it.
    /// The arguments are the asset and the level that is resident now. It does not occur after <see cref="Close"/>.
    /// </summary>
    public event Action<Guid, int>? LoadCompleted;

    /// <summary>The manifest that the planner uses.</summary>
    public CutsceneResidencyManifest Manifest { get; }

    /// <summary>
    /// The look-ahead in the direction of travel at rate 1. The window uses this value multiplied by the absolute rate.
    /// At rate 0 (a pause), the window uses the forward window of rate 1, so a pause keeps the prefetch. It must not be negative.
    /// </summary>
    public CutsceneTime LookAhead
    {
        get => _lookAhead;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Flicks, nameof(value));
            _lookAhead = value;
        }
    }

    /// <summary>The window behind the playhead, opposite to the direction of travel. The rate does not scale it. It must not be negative.</summary>
    public CutsceneTime LookBehind
    {
        get => _lookBehind;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Flicks, nameof(value));
            _lookBehind = value;
        }
    }

    /// <summary>True after <see cref="Open"/> and before <see cref="Close"/>.</summary>
    public bool IsOpen => _opened && !IsClosed;

    /// <summary>True after <see cref="Close"/>. Any thread can read it.</summary>
    public bool IsClosed => Volatile.Read(ref _closed);

    /// <summary>The generation of new requests. Each jump increments it.</summary>
    public int Generation => _generation;

    /// <summary>The number of T0 loads that did not complete yet. At zero, every asset has its proxy.</summary>
    public int PendingProxyCount => _pendingProxyCount;

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
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), "The frame time must be a finite value of zero or more.");

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
    /// Cancels all work, releases all levels, including T0, and stops the playback wait.
    /// The order is: running work, then held T1 and T2 data, then T0 data.
    /// After this call, no completion runs a callback, and <see cref="Update"/> does nothing.
    /// A second call does nothing.
    /// </summary>
    public void Close()
    {
        lock (_completionLock)
        {
            if (_closed)
                return;

            Volatile.Write(ref _closed, true);
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

    private int CompareProxyRequests(int left, int right)
    {
        int result = _firstNeeded[left].CompareTo(_firstNeeded[right]);
        if (result != 0)
            return result;

        result = _maxCoverage[right].CompareTo(_maxCoverage[left]);
        return result != 0 ? result : left.CompareTo(right);
    }
}
