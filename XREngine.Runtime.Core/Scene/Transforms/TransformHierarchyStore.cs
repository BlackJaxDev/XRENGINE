using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Transforms;

/// <summary>
/// World-owned matrix arrays with generational identities, contiguous depth-first subtree ranges,
/// and coherent sequence-locked matrix reads. Hierarchy changes are serialized with traversal.
/// </summary>
public sealed partial class TransformHierarchyStore : IDisposable
{
    private readonly object _gate = new();
    private readonly object _passGate = new();
    private TransformBase?[] _owners = [];
    private uint[] _generations = [];
    private int[] _parent = [], _order = [], _position = [], _end = [], _first = [], _next = [], _stack = [];
    private Matrix4x4[] _local = [], _world = [], _render = [];
    private int[] _notificationInterest = [], _notifications = [];
    // One bit for each hierarchy position with a pending notification. The bits are a hint for the dispatch scan:
    // _notifications holds the pending masks. Reorder rebuilds the bits when positions change.
    private ulong[] _pendingNotificationPositions = [];
    private bool _hasPendingNotificationPositions;
    private bool[] _publication = [], _worldOverride = [], _localDirty = [], _worldDirty = [];
    private ulong[] _dirty = [], _batch = [];
    private readonly Stack<int> _free = new();
    private readonly List<(int Start, int End)> _ranges = [];
    private readonly List<(TransformBase Owner, int Mask)> _changed = [];
    private ITransformPropagationWorkerPool? _workers;
    private bool _processing;
    private int _used, _count, _sequence;
    private bool _orderDirty;
    private bool _publishing;
    private ITransformPublicationSink? _publicationSink;
    private int[] _publicationSlots = [];
    private long _dirtyLocal, _dirtyWorld, _propagated, _published, _events;
    private long _propagationAllocated, _publicationAllocated;
    private double _propagationMs, _publicationMs;
    [ThreadStatic] private static TransformHierarchyStore? _evaluating;
    private static readonly long[] s_contendedReads = new long[3];
    private static readonly long[] s_oddSequenceRetries = new long[3];
    private static readonly long[] s_changedSequenceRetries = new long[3];
    private static readonly long[] s_retryTicks = new long[3];
    private static readonly int[] s_maxSpinCount = new int[3];

    internal static void BeginEvaluation(TransformHierarchyStore store) => _evaluating = store;
    internal static void EndEvaluation() => _evaluating = null;

    public TransformHierarchyCounters Counters => new(_count,
        Interlocked.Read(ref _dirtyLocal), Interlocked.Read(ref _dirtyWorld),
        Interlocked.Read(ref _propagated), Interlocked.Read(ref _published), Interlocked.Read(ref _events),
        _propagationAllocated, _publicationAllocated, _propagationMs, _publicationMs);

    /// <summary>Gets cumulative sequence-read contention for local, world, and render matrices.</summary>
    public static TransformHierarchyReadContentionTelemetrySnapshot GetReadContentionTelemetrySnapshot() => new(
        RuntimeWorldTickTelemetry.Enabled,
        Stopwatch.Frequency,
        Interlocked.Read(ref s_contendedReads[0]), Interlocked.Read(ref s_oddSequenceRetries[0]),
        Interlocked.Read(ref s_changedSequenceRetries[0]), Interlocked.Read(ref s_retryTicks[0]),
        Volatile.Read(ref s_maxSpinCount[0]),
        Interlocked.Read(ref s_contendedReads[1]), Interlocked.Read(ref s_oddSequenceRetries[1]),
        Interlocked.Read(ref s_changedSequenceRetries[1]), Interlocked.Read(ref s_retryTicks[1]),
        Volatile.Read(ref s_maxSpinCount[1]),
        Interlocked.Read(ref s_contendedReads[2]), Interlocked.Read(ref s_oddSequenceRetries[2]),
        Interlocked.Read(ref s_changedSequenceRetries[2]), Interlocked.Read(ref s_retryTicks[2]),
        Volatile.Read(ref s_maxSpinCount[2]));

    /// <summary>
    /// Gets the cumulative pass-gate wait and hold counters for each measured entry site, summed over all stores.
    /// The counters are always active.
    /// </summary>
    public static TransformHierarchyPassGateTelemetrySnapshot GetPassGateTelemetrySnapshot()
        => TransformHierarchyPassGateTelemetry.Capture();

    private static void RecordReadContention(int space, int oddRetries, int changedRetries, int spinCount, long started)
    {
        int index = (uint)space < 2u ? space : 2;
        Interlocked.Increment(ref s_contendedReads[index]);
        if (oddRetries != 0)
            Interlocked.Add(ref s_oddSequenceRetries[index], oddRetries);
        if (changedRetries != 0)
            Interlocked.Add(ref s_changedSequenceRetries[index], changedRetries);
        Interlocked.Add(ref s_retryTicks[index], Stopwatch.GetTimestamp() - started);
        int observed = Volatile.Read(ref s_maxSpinCount[index]);
        while (spinCount > observed)
        {
            int previous = Interlocked.CompareExchange(ref s_maxSpinCount[index], spinCount, observed);
            if (previous == observed)
                break;
            observed = previous;
        }
    }

    internal TransformHandle Attach(TransformBase owner, Matrix4x4 local, Matrix4x4 world, Matrix4x4 render)
    {
        RejectHierarchyMutationDuringEvaluation();
        lock (_passGate)
        lock (_gate)
        {
            BeginWrite();
            try
            {
                int index = _free.Count != 0 ? _free.Pop() : _used++;
                EnsureCapacity(_used);
                _owners[index] = owner;
                if (_generations[index] == 0) _generations[index] = 1;
                _localDirty[index] = owner.IsLocalMatrixDirty;
                _worldDirty[index] = true;
                _local[index] = local;
                _world[index] = world;
                _render[index] = render;
                _parent[index] = -1;
                _worldOverride[index] = owner.UsesCustomWorldMatrix;
                _notificationInterest[index] = owner.MatrixNotificationInterest;
                _publication[index] = true;
                _dirty[index >> 6] |= 1UL << (index & 63);
                _count++;
                _orderDirty = true;
                return new(index, _generations[index]);
            }
            finally { EndWrite(); }
        }
    }

    internal DetachedTransformMatrices Detach(TransformHandle handle)
    {
        RejectHierarchyMutationDuringEvaluation();
        lock (_passGate)
        lock (_gate)
        {
            int i = Require(handle);
            var detached = new DetachedTransformMatrices { Local = _local[i], World = _world[i], Render = _render[i] };
            BeginWrite();
            try
            {
                _owners[i] = null;
                _generations[i] = unchecked(_generations[i] + 1);
                if (_generations[i] == 0) _generations[i] = 1;
                _dirty[i >> 6] &= ~(1UL << (i & 63));
                _publication[i] = false;
                _notifications[i] = 0;
                _free.Push(i);
                _count--;
                _orderDirty = true;
            }
            finally { EndWrite(); }
            return detached;
        }
    }

    public bool IsAlive(TransformHandle handle)
    {
        lock (_gate) return Alive(handle);
    }

    private bool Alive(TransformHandle h) => h.Generation != 0 && (uint)h.Index < (uint)_used
        && _generations[h.Index] == h.Generation && _owners[h.Index] is not null;
    private int Require(TransformHandle h) => Alive(h) ? h.Index
        : throw new InvalidOperationException($"Stale transform handle {h.Index}:{h.Generation}; the transform detached or moved worlds.");

    internal Matrix4x4 Read(TransformHandle h, int space)
    {
        SpinWait spin = default;
        bool telemetryEnabled = RuntimeWorldTickTelemetry.Enabled;
        long retryStart = 0;
        int oddRetries = 0;
        int changedRetries = 0;
        while (true)
        {
            int before = Volatile.Read(ref _sequence);
            if ((before & 1) != 0)
            {
                if (telemetryEnabled)
                {
                    if (oddRetries + changedRetries == 0)
                        retryStart = Stopwatch.GetTimestamp();
                    oddRetries++;
                }
                spin.SpinOnce();
                continue;
            }
            var owners = _owners;
            var generations = _generations;
            var matrices = space == 0 ? _local : space == 1 ? _world : _render;
            bool alive = h.Generation != 0 && (uint)h.Index < (uint)owners.Length
                && (uint)h.Index < (uint)generations.Length && (uint)h.Index < (uint)matrices.Length
                && generations[h.Index] == h.Generation && owners[h.Index] is not null;
            Matrix4x4 value = alive ? matrices[h.Index] : default;
            Thread.MemoryBarrier();
            if (before != Volatile.Read(ref _sequence))
            {
                if (telemetryEnabled)
                {
                    if (oddRetries + changedRetries == 0)
                        retryStart = Stopwatch.GetTimestamp();
                    changedRetries++;
                }
                spin.SpinOnce();
                continue;
            }
            if (telemetryEnabled && oddRetries + changedRetries != 0)
                RecordReadContention(space, oddRetries, changedRetries, spin.Count, retryStart);
            if (!alive) throw new InvalidOperationException($"Stale transform handle {h.Index}:{h.Generation}; the transform detached or moved worlds.");
            return value;
        }
    }

    internal void Write(TransformHandle h, int space, Matrix4x4 value)
    {
        lock (_gate)
        {
            int i = Require(h);
            BeginWrite();
            try
            {
                if (space == 0) _local[i] = value;
                else if (space == 1) { _world[i] = value; if (!TransformBase.IsDiagnosticEvaluationActive) _publication[i] = true; }
                else { _render[i] = value; _publication[i] = false; }
            }
            finally { EndWrite(); }
        }
    }

    internal Matrix4x4 ComposeWorld(TransformHandle handle)
    {
        // Immediate editor/physics reads can occur before the next hierarchy reorder.
        TransformBase? parent;
        Matrix4x4 local;
        lock (_gate)
        {
            int i = Require(handle);
            local = _local[i];
            if (!_orderDirty)
            {
                int p = _parent[i];
                if (p >= 0) return local * _world[p];
            }
            parent = _owners[i]!.Parent;
        }
        return parent is null ? local : local * parent.WorldMatrix;
    }

    internal bool IsDirty(TransformHandle handle, int space)
    {
        lock (_gate) { int i = Require(handle); return space == 0 ? _localDirty[i] : _worldDirty[i]; }
    }

    /// <summary>Reads both dirty flags from one hierarchy state.</summary>
    internal void ReadDirtyPair(TransformHandle handle, out bool localDirty, out bool worldDirty)
    {
        lock (_gate)
        {
            int i = Require(handle);
            localDirty = _localDirty[i];
            worldDirty = _worldDirty[i];
        }
    }

    /// <summary>Reads and composes an ordinary world matrix under one hierarchy gate.</summary>
    internal bool TryRecalculateOrdinaryWorld(
        TransformHandle handle, bool forceWorldRecalc, out bool recalcWorld)
    {
        recalcWorld = false;
        lock (_gate)
        {
            int i = Require(handle);
            if (_localDirty[i] || _orderDirty || _worldOverride[i])
                return false;

            int parentIndex = _parent[i];
            if ((uint)parentIndex >= (uint)_used || _owners[parentIndex] is null)
                return false;

            recalcWorld = _worldDirty[i] || forceWorldRecalc;
            if (!recalcWorld)
                return true;

            Matrix4x4 matrix = _local[i] * _world[parentIndex];
            BeginWrite();
            try
            {
                _world[i] = matrix;
                if (!TransformBase.IsDiagnosticEvaluationActive)
                    _publication[i] = true;
                _worldDirty[i] = false;
            }
            finally { EndWrite(); }
            return true;
        }
    }

    /// <summary>Publishes one local matrix and clears its dirty flag before callbacks run.</summary>
    internal void WriteLocalAndClearDirty(TransformHandle handle, Matrix4x4 matrix)
    {
        lock (_gate)
        {
            int i = Require(handle);
            BeginWrite();
            try
            {
                _local[i] = matrix;
                _localDirty[i] = false;
            }
            finally { EndWrite(); }
        }
    }

    /// <summary>
    /// Composes an ordinary world matrix from a valid cached parent and commits it
    /// under one gate. The caller uses the existing path when the order is stale.
    /// </summary>
    internal bool TryComposeAndWriteWorld(TransformHandle handle)
    {
        lock (_gate)
        {
            int i = Require(handle);
            if (_orderDirty || _worldOverride[i])
                return false;

            int parentIndex = _parent[i];
            if ((uint)parentIndex >= (uint)_used || _owners[parentIndex] is null)
                return false;
            Matrix4x4 matrix = _local[i] * _world[parentIndex];

            BeginWrite();
            try
            {
                _world[i] = matrix;
                if (!TransformBase.IsDiagnosticEvaluationActive)
                    _publication[i] = true;
                _worldDirty[i] = false;
            }
            finally { EndWrite(); }
            return true;
        }
    }

    /// <summary>Publishes an externally composed world matrix before callbacks run.</summary>
    internal void WriteWorldAndClearDirty(TransformHandle handle, Matrix4x4 matrix)
    {
        lock (_gate)
        {
            int i = Require(handle);
            BeginWrite();
            try
            {
                _world[i] = matrix;
                if (!TransformBase.IsDiagnosticEvaluationActive)
                    _publication[i] = true;
                _worldDirty[i] = false;
            }
            finally { EndWrite(); }
        }
    }
    internal void SetDirty(TransformHandle handle, int space, bool dirty)
    {
        lock (_gate)
        {
            int i = Require(handle);
            if (space == 0) _localDirty[i] = dirty;
            else _worldDirty[i] = dirty;
        }
    }

    internal void MarkDirty(TransformHandle h, bool local)
    {
        lock (_gate)
        {
            int i = Require(h);
            _dirty[i >> 6] |= 1UL << (i & 63);
            Interlocked.Increment(ref _dirtyWorld);
        }
    }

    internal void RecordLocalInvalidation() => Interlocked.Increment(ref _dirtyLocal);
    internal void RejectHierarchyMutationDuringEvaluation()
    {
        if (_evaluating == this)
            throw new InvalidOperationException("Hierarchy mutations must be deferred until matrix evaluation completes.");
    }
    internal void HierarchyChanged() { RejectHierarchyMutationDuringEvaluation(); lock (_gate) _orderDirty = true; }
    /// <summary>Sets the notification bits that have an effect on the owner of <paramref name="h"/>.</summary>
    internal void SetNotificationInterest(TransformHandle h, int interest)
    {
        lock (_gate) _notificationInterest[Require(h)] = interest;
    }

    internal void RecordEvent() => Interlocked.Increment(ref _events);

    /// <summary>
    /// Queues a notification during matrix evaluation. Bits that have no effect on the owner are dropped,
    /// so a transform with no handlers and no overridden change method gets no dispatch.
    /// </summary>
    /// <returns>False when this thread does not evaluate this store, so the caller must dispatch now.</returns>
    internal bool DeferNotification(TransformHandle h, int mask)
    {
        if (_evaluating != this) return false;
        lock (_gate)
        {
            int i = Require(h);
            FlagNotification(i, mask & _notificationInterest[i]);
        }
        return true;
    }

    /// <summary>Adds pending notification bits for row <paramref name="i"/>. The caller holds <see cref="_gate"/>.</summary>
    private void FlagNotification(int i, int mask)
    {
        if (mask == 0) return;
        _notifications[i] |= mask;
        int position = _position[i];
        _pendingNotificationPositions[position >> 6] |= 1UL << (position & 63);
        _hasPendingNotificationPositions = true;
    }

    /// <summary>Rebuilds contiguous subtree ranges after a batch of hierarchy edits.</summary>
    public void FlushHierarchy()
    {
        bool passGateTaken = false, passGateHolder = false;
        try
        {
            passGateHolder = EnterPassGate(ETransformHierarchyPassGateSite.FlushHierarchy, ref passGateTaken);
            lock (_gate) Reorder();
        }
        finally { ExitPassGate(passGateTaken, passGateHolder); }
    }

    private void Reorder()
    {
        if (!_orderDirty) return;
        Array.Fill(_first, -1);
        Array.Fill(_next, -1);
        int roots = 0;
        for (int i = _used - 1; i >= 0; i--)
        {
            TransformBase? owner = _owners[i];
            if (owner is null) continue;
            TransformBase? parent = owner.Parent;
            int p = parent?.HierarchyStore == this && Alive(parent.HierarchyHandle) ? parent.HierarchyHandle.Index : -1;
            _parent[i] = p;
            if (p >= 0) { _next[i] = _first[p]; _first[p] = i; }
            else _stack[roots++] = i;
        }
        int cursor = 0, top = roots;
        while (top != 0)
        {
            int i = _stack[--top];
            _position[i] = cursor;
            _order[cursor++] = i;
            // Exit markers close a subtree after all descendants, without recursive stack growth.
            _stack[top++] = ~i;
            for (int child = _first[i]; child >= 0; child = _next[child]) _stack[top++] = child;
            while (top != 0 && _stack[top - 1] < 0) _end[~_stack[--top]] = cursor;
        }
        if (cursor != _count) throw new InvalidOperationException("Transform hierarchy contains a parenting cycle.");
        _orderDirty = false;
        if (_hasPendingNotificationPositions)
            RebuildPendingNotificationPositions();
    }

    /// <summary>Sets the pending-notification bits again from the new positions. The caller holds <see cref="_gate"/>.</summary>
    private void RebuildPendingNotificationPositions()
    {
        Array.Clear(_pendingNotificationPositions);
        _hasPendingNotificationPositions = false;
        for (int i = 0; i < _used; i++)
            if (_notifications[i] != 0 && _owners[i] is not null)
                FlagNotification(i, _notifications[i]);
    }

    /// <summary>Propagates disjoint dirty subtree ranges, then dispatches matrix notifications.</summary>
    public void Process(ELoopType loopType)
    {
        // The same enter and release pattern as a lock statement, with wait and hold counters.
        bool passGateTaken = false, passGateHolder = false;
        try
        {
            passGateHolder = EnterPassGate(ETransformHierarchyPassGateSite.ProcessPass, ref passGateTaken);
            if (_processing || _publishing)
            {
                TransformHierarchyPassGateTelemetry.CountReentrantSkip(ETransformHierarchyPassGateSite.ProcessPass);
                return;
            }
            _processing = true;
            long workerAllocated = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            try
            {
              lock (_gate)
              {
                Reorder();
                Array.Copy(_dirty, _batch, _dirty.Length);
                Array.Clear(_dirty);
                _ranges.Clear();
                for (int p = 0; p < _count; p++)
                {
                    int i = _order[p];
                    if ((_batch[i >> 6] & (1UL << (i & 63))) == 0) continue;
                    _ranges.Add((p, _end[i]));
                    p = _end[i] - 1;
                }
              }
            }
            catch { _processing = false; throw; }
            try
            {
                bool independent = true;
                foreach (var range in _ranges)
                    for (int p = range.Start; p < range.End; p++)
                        if (_worldOverride[_order[p]]) { independent = false; break; }
                if (loopType != ELoopType.Sequential && independent && _ranges.Count > 1)
                {
                    _workers ??= TransformPropagationWorkerServices.GetRequiredFactory().Create(this, ProcessRange)
                        ?? throw new InvalidOperationException("The transform propagation worker factory returned no pool.");
                    workerAllocated = _workers.Run(_ranges.Count);
                }
                else
                {
                    _evaluating = this;
                    for (int i = 0; i < _ranges.Count; i++) ProcessRange(i);
                }
            }
            catch
            {
                lock (_gate)
                    for (int i = 0; i < _dirty.Length; i++) _dirty[i] |= _batch[i];
                throw;
            }
            finally
            {
                _evaluating = null;
                long notifyStarted = Stopwatch.GetTimestamp();
                int notified = 0;
                try { notified = DispatchNotifications(); }
                finally
                {
                    _processing = false;
                    _propagationAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated + workerAllocated;
                    long ended = Stopwatch.GetTimestamp();
                    _propagationMs = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
                    TransformHierarchyPassGateTelemetry.RecordHold(
                        ETransformHierarchyPassGateSite.ProcessPass, ended - started, ended - notifyStarted, 0, notified);
                }
            }
        }
        finally { ExitPassGate(passGateTaken, passGateHolder); }
    }

    private void ProcessRange(int rangeIndex)
    {
        var range = _ranges[rangeIndex];
        for (int p = range.Start; p < range.End; p++)
        {
            TransformBase? owner = _owners[_order[p]];
            if (owner is null || owner.ForceManualRecalc) continue;
            owner.RecalculateMatrices(true, false);
            Interlocked.Increment(ref _propagated);
        }
    }

    /// <summary>Runs the pending matrix notifications in hierarchy order.</summary>
    /// <remarks>The scan visits only the positions with a pending-notification bit, 64 positions for each word.</remarks>
    /// <returns>The number of transforms that got notifications.</returns>
    private int DispatchNotifications()
    {
        // Snapshot identities before callbacks, which may detach, destroy or reparent entries.
        _changed.Clear();
        lock (_gate)
        {
            if (_hasPendingNotificationPositions)
            {
                ulong[] pending = _pendingNotificationPositions;
                for (int word = 0; word < pending.Length; word++)
                {
                    ulong bits = pending[word];
                    if (bits == 0) continue;
                    pending[word] = 0;
                    do
                    {
                        int position = (word << 6) + BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (position >= _order.Length) continue;
                        int i = _order[position];
                        int mask = _notifications[i];
                        if (mask == 0) continue;
                        _notifications[i] = 0;
                        if (_owners[i] is { } owner) _changed.Add((owner, mask));
                    }
                    while (bits != 0);
                }
                _hasPendingNotificationPositions = false;
            }
        }
        int notified = _changed.Count;
        for (int i = 0; i < _changed.Count; i++)
        {
            var change = _changed[i];
            try
            {
                if (change.Owner.HierarchyStore == this) change.Owner.DispatchMatrixNotifications(change.Mask);
            }
            catch
            {
                lock (_gate)
                    for (int pending = i + 1; pending < _changed.Count; pending++)
                    {
                        var retry = _changed[pending];
                        if (retry.Owner.HierarchyStore == this)
                            FlagNotification(Require(retry.Owner.HierarchyHandle), retry.Mask);
                    }
                throw;
            }
        }
        _changed.Clear();
        return notified;
    }

    /// <summary>Publishes pending world matrices as one array transaction before render collection.</summary>
    /// <remarks>The pass-gate telemetry counts this call as <see cref="ETransformHierarchyPassGateSite.PublishOther"/>.</remarks>
    public int PublishRenderMatrices()
        => PublishRenderMatrices(ETransformHierarchyPassGateSite.PublishOther);

    /// <summary>
    /// Publishes pending world matrices as one array transaction, and records the pass-gate wait and hold
    /// for the publication site of the caller.
    /// </summary>
    /// <param name="site">
    /// <see cref="ETransformHierarchyPassGateSite.PublishPreCollect"/>, <see cref="ETransformHierarchyPassGateSite.PublishSwap"/>,
    /// or <see cref="ETransformHierarchyPassGateSite.PublishOther"/>.
    /// </param>
    /// <returns>The number of published rows, or 0 when a pass or a publication is active on the same thread.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="site"/> is not a publication site.</exception>
    internal int PublishRenderMatrices(ETransformHierarchyPassGateSite site)
    {
        if (!TransformHierarchyPassGateTelemetry.IsPublicationSite(site))
            throw new ArgumentOutOfRangeException(nameof(site), site, "The site must be a render-matrix publication site.");

        // The same enter and release pattern as a lock statement, with wait and hold counters.
        bool passGateTaken = false, passGateHolder = false;
        try
        {
            passGateHolder = EnterPassGate(site, ref passGateTaken);
            if (_publishing || _processing)
            {
                TransformHierarchyPassGateTelemetry.CountReentrantSkip(site);
                return 0;
            }
            _publishing = true;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            int published = 0;
            try
            {
              lock (_gate)
              {
                Reorder();
                BeginWrite();
                try
                {
                    for (int p = 0; p < _count; p++)
                    {
                        int i = _order[p];
                        if (!_publication[i]) continue;
                        _publication[i] = false;
                        bool changed = !_render[i].Equals(_world[i]);
                        _render[i] = _world[i];
                        if (changed)
                            FlagNotification(i, TransformBase.RenderMatrixNotification & _notificationInterest[i]);
                        _publicationSlots[published++] = i;
                    }
                }
                finally { EndWrite(); }
              }
            }
            catch { _publishing = false; throw; }
            long notifyStarted = 0;
            int notified = 0;
            try
            {
                _publicationSink?.Publish(_publicationSlots.AsSpan(0, published), _render, _generations);
                notifyStarted = Stopwatch.GetTimestamp();
                notified = DispatchNotifications();
                Interlocked.Add(ref _published, published);
                return published;
            }
            finally
            {
                _publishing = false;
                _publicationAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                long ended = Stopwatch.GetTimestamp();
                _publicationMs = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
                // notifyStarted stays 0 when the sink throws before the notifications start.
                TransformHierarchyPassGateTelemetry.RecordHold(
                    site, ended - started, notifyStarted == 0 ? 0 : ended - notifyStarted, published, notified);
            }
        }
        finally { ExitPassGate(passGateTaken, passGateHolder); }
    }

    /// <summary>Installs the rendering owner's record-table writer and schedules initial population.</summary>
    public void SetPublicationSink(ITransformPublicationSink? sink)
    {
        lock (_passGate)
        lock (_gate)
        {
            _publicationSink = sink;
            for (int i = 0; i < _used; i++)
                if (_owners[i] is not null) _publication[i] = true;
        }
    }

    /// <summary>Clears queued recalculations when the world's simulation resets.</summary>
    public void ClearDirty() { lock (_gate) Array.Clear(_dirty); }
    private void BeginWrite() => Interlocked.Increment(ref _sequence);
    private void EndWrite() => Interlocked.Increment(ref _sequence);
    private void EnsureCapacity(int required)
    {
        if (_owners.Length >= required) return;
        int capacity = Math.Max(256, Math.Max(required, _owners.Length * 2));
        Array.Resize(ref _owners, capacity); Array.Resize(ref _generations, capacity);
        Array.Resize(ref _parent, capacity); Array.Resize(ref _order, capacity); Array.Resize(ref _position, capacity);
        Array.Resize(ref _end, capacity); Array.Resize(ref _first, capacity); Array.Resize(ref _next, capacity);
        Array.Resize(ref _stack, capacity * 2);
        Array.Resize(ref _local, capacity); Array.Resize(ref _world, capacity); Array.Resize(ref _render, capacity);
        Array.Resize(ref _notificationInterest, capacity); Array.Resize(ref _notifications, capacity);
        Array.Resize(ref _localDirty, capacity); Array.Resize(ref _worldDirty, capacity);
        Array.Resize(ref _publicationSlots, capacity);
        Array.Resize(ref _publication, capacity); Array.Resize(ref _worldOverride, capacity);
        Array.Resize(ref _dirty, (capacity + 63) / 64); Array.Resize(ref _batch, (capacity + 63) / 64);
        Array.Resize(ref _pendingNotificationPositions, (capacity + 63) / 64);
        _ranges.EnsureCapacity(capacity);
        _changed.EnsureCapacity(capacity);
    }

    public void Dispose()
    {
        lock (_passGate) { _workers?.Dispose(); _workers = null; }
    }
}
