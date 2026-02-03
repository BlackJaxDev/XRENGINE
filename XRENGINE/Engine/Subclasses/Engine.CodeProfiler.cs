using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Event-based code profiler with near-zero overhead on the calling thread.
        /// Start() only captures a timestamp and pushes a lightweight event to a queue.
        /// All tree reconstruction and processing happens on a dedicated stats thread.
        /// </summary>
        public class CodeProfiler : XRBase
        {
#if DEBUG
            private bool _enableFrameLogging = true;
#else
            private bool _enableFrameLogging = false;
#endif
            public bool EnableFrameLogging
            {
                get => _enableFrameLogging;
                set
                {
                    if (!SetField(ref _enableFrameLogging, value))
                        return;

                    if (value)
                        StartStatsThread();
                    else
                        StopStatsThread(waitForExit: true);
                }
            }

            private float _debugOutputMinElapsedMs = 1.0f;
            public float DebugOutputMinElapsedMs
            {
                get => _debugOutputMinElapsedMs;
                set => SetField(ref _debugOutputMinElapsedMs, value);
            }

            // ============ EVENT-BASED ARCHITECTURE ============
            // Events are lightweight structs pushed to a queue - no allocations, no locks on Start/Stop
            
            private enum ProfilerEventType : byte { Start, Stop }

            private readonly struct ProfilerEvent
            {
                public readonly ProfilerEventType Type;
                public readonly float Timestamp;
                public readonly int ThreadId;
                public readonly int CorrelationId;
                public readonly string? MethodName; // Only set for Start events

                public ProfilerEvent(ProfilerEventType type, float timestamp, int threadId, int correlationId, string? methodName)
                {
                    Type = type;
                    Timestamp = timestamp;
                    ThreadId = threadId;
                    CorrelationId = correlationId;
                    MethodName = methodName;
                }
            }

            // Thread-local correlation ID counter - no contention, no atomic operations needed
            [ThreadStatic]
            private static int _tlsCorrelationId;

            // Main event queue - all profiler events go here
            private readonly ConcurrentQueue<ProfilerEvent> _eventQueue = new();

            private readonly object _statsThreadLock = new();
            private Thread? _statsThread;
            private CancellationTokenSource? _statsThreadCts;

            // Lock-free snapshot access using volatile references
            private volatile ProfilerFrameSnapshot? _readySnapshot;
            private volatile Dictionary<int, float[]> _readyHistorySnapshot = [];
            
            // Stats thread owns these - no locking needed
            private readonly Dictionary<int, Queue<float>> _threadFrameHistory = [];
            private const int ThreadHistoryCapacity = 240;

            // Stats thread working buffers - reused to avoid allocations
            private readonly Dictionary<int, ThreadBuildState> _threadBuildStates = [];
            private readonly List<ProfilerThreadSnapshot> _threadSnapshotsBuffer = new(8);

            // Accumulated roots per thread - survives across processing cycles until snapshot is built
            private readonly Dictionary<int, List<BuiltTimer>> _accumulatedRoots = [];

            // Snapshot timing - build snapshots at fixed intervals, not every processing cycle
            private const int StatsThreadIntervalMs = 4; // ~250Hz event processing (fast drain)
            private const int SnapshotIntervalMs = 33; // ~30Hz snapshot publishing
            private float _lastSnapshotTime = float.NegativeInfinity;

            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            /// <summary>
            /// Lightweight scope returned by Start() that captures only a correlation ID.
            /// Dispose() pushes a Stop event to the queue.
            /// </summary>
            public struct ProfilerScope : IDisposable
            {
                private readonly CodeProfiler? _profiler;
                private readonly int _correlationId;
                private readonly int _threadId;

                internal ProfilerScope(CodeProfiler? profiler, int correlationId, int threadId)
                {
                    _profiler = profiler;
                    _correlationId = correlationId;
                    _threadId = threadId;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Dispose()
                {
                    if (_profiler is null)
                        return;

                    // Push stop event - just timestamp and correlation ID
                    _profiler._eventQueue.Enqueue(new ProfilerEvent(
                        ProfilerEventType.Stop,
                        Time.Timer.Time(),
                        _threadId,
                        _correlationId,
                        null));
                }
            }

            public CodeProfiler()
            {
                if (_enableFrameLogging)
                    StartStatsThread();
            }

            ~CodeProfiler()
            {
                StopStatsThread(waitForExit: false);
            }

            // ============ BUILD STATE FOR STATS THREAD ============
            // These classes are only used on the stats thread

            private sealed class PendingTimer
            {
                public string Name = string.Empty;
                public float StartTime;
                public int CorrelationId;
                public PendingTimer? Parent;
                public List<BuiltTimer> Children = new(4);

                public void Reset()
                {
                    Name = string.Empty;
                    StartTime = 0f;
                    CorrelationId = 0;
                    Parent = null;
                    Children.Clear();
                }
            }

            private sealed class BuiltTimer
            {
                public string Name = string.Empty;
                public float ElapsedMs;
                public List<BuiltTimer> Children = new(4);

                public void Reset()
                {
                    Name = string.Empty;
                    ElapsedMs = 0f;
                    Children.Clear();
                }
            }

            private sealed class ThreadBuildState
            {
                public Stack<PendingTimer> ActiveStack = new(32);
                public List<BuiltTimer> CompletedRoots = new(16);
                public Queue<PendingTimer> PendingTimerPool = new(64);
                public Queue<BuiltTimer> BuiltTimerPool = new(64);
                public Dictionary<int, PendingTimer> CorrelationMap = new(64);

                public PendingTimer RentPending()
                {
                    if (PendingTimerPool.Count > 0)
                        return PendingTimerPool.Dequeue();
                    return new PendingTimer();
                }

                public void ReturnPending(PendingTimer timer)
                {
                    timer.Reset();
                    PendingTimerPool.Enqueue(timer);
                }

                public BuiltTimer RentBuilt()
                {
                    if (BuiltTimerPool.Count > 0)
                        return BuiltTimerPool.Dequeue();
                    return new BuiltTimer();
                }

                public void ReturnBuilt(BuiltTimer timer)
                {
                    timer.Reset();
                    BuiltTimerPool.Enqueue(timer);
                }

                public void Clear()
                {
                    while (ActiveStack.Count > 0)
                    {
                        var pending = ActiveStack.Pop();
                        ReturnPending(pending);
                    }
                    foreach (var root in CompletedRoots)
                        ReturnBuiltRecursive(root);
                    CompletedRoots.Clear();
                    CorrelationMap.Clear();
                }

                public void ReturnBuiltRecursive(BuiltTimer timer)
                {
                    foreach (var child in timer.Children)
                        ReturnBuiltRecursive(child);
                    ReturnBuilt(timer);
                }
            }

            // Legacy types for API compatibility
            public class CodeProfilerTimer(string? name = null) : IPoolable
            {
                public float StartTime { get; private set; }
                public float EndTime { get; private set; }
                public float ElapsedMs { get; private set; }
                public float ElapsedSec => ElapsedMs * 0.001f;
                public string Name { get; private set; } = name ?? string.Empty;
                public int ThreadId { get; private set; }
                public int Depth { get; private set; }

                public void OnPoolableDestroyed() { }
                public void OnPoolableReleased() { }
                public void OnPoolableReset()
                {
                    Depth = 0;
                    ThreadId = 0;
                    Name = string.Empty;
                    StartTime = 0f;
                    EndTime = 0f;
                    ElapsedMs = 0f;
                }
            }

            // Legacy - kept for API compatibility but not used internally
            public ConcurrentDictionary<int, CodeProfilerTimer> RootEntriesPerThread { get; } = [];


            // ============ STATS THREAD ============

            private void StartStatsThread()
            {
                lock (_statsThreadLock)
                {
                    if (_statsThread is { IsAlive: true })
                        return;

                    _statsThreadCts = new CancellationTokenSource();
                    _statsThread = new Thread(StatsThreadMain)
                    {
                        IsBackground = true,
                        Name = "XREngine.ProfilerStats",
                        Priority = ThreadPriority.BelowNormal
                    };
                    _statsThread.Start(_statsThreadCts.Token);
                }
            }

            private void StopStatsThread(bool waitForExit)
            {
                Thread? threadToJoin = null;
                lock (_statsThreadLock)
                {
                    if (_statsThread is null)
                        return;

                    _statsThreadCts?.Cancel();
                    threadToJoin = _statsThread;
                    _statsThread = null;
                }

                if (waitForExit && threadToJoin is not null)
                    threadToJoin.Join(TimeSpan.FromMilliseconds(250));

                // Drain any remaining events
                while (_eventQueue.TryDequeue(out _)) { }
            }

            private void StatsThreadMain(object? state)
            {
                if (state is not CancellationToken token)
                    return;

                // Continuous processing loop - no waiting for signals
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Process events - this drains the queue and accumulates completed roots
                        ProcessEventQueue();

                        // Check if it's time to publish a snapshot
                        float now = Time.Timer.Time();
                        float elapsed = (now - _lastSnapshotTime) * 1000.0f; // Convert to ms
                        if (elapsed >= SnapshotIntervalMs || _lastSnapshotTime < 0)
                        {
                            BuildFrameSnapshot(now);
                            _lastSnapshotTime = now;
                        }
                        
                        // Small sleep to prevent busy-spinning while still being responsive
                        Thread.Sleep(StatsThreadIntervalMs);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }

            private void ProcessEventQueue()
            {
                // Process all pending events - don't wait, just drain what's available
                int eventsProcessed = 0;
                const int maxEventsPerBatch = 10000; // Prevent infinite loop if events come faster than we process
                
                while (eventsProcessed < maxEventsPerBatch && _eventQueue.TryDequeue(out var evt))
                {
                    eventsProcessed++;
                    
                    if (!_threadBuildStates.TryGetValue(evt.ThreadId, out var state))
                    {
                        state = new ThreadBuildState();
                        _threadBuildStates[evt.ThreadId] = state;
                    }

                    if (evt.Type == ProfilerEventType.Start)
                    {
                        var pending = state.RentPending();
                        pending.Name = evt.MethodName ?? string.Empty;
                        pending.StartTime = evt.Timestamp;
                        pending.CorrelationId = evt.CorrelationId;
                        pending.Parent = state.ActiveStack.Count > 0 ? state.ActiveStack.Peek() : null;

                        state.ActiveStack.Push(pending);
                        state.CorrelationMap[evt.CorrelationId] = pending;
                    }
                    else // Stop
                    {
                        if (state.CorrelationMap.TryGetValue(evt.CorrelationId, out var pending))
                        {
                            state.CorrelationMap.Remove(evt.CorrelationId);

                            // Pop from stack (may be out of order)
                            if (state.ActiveStack.Count > 0 && ReferenceEquals(state.ActiveStack.Peek(), pending))
                            {
                                state.ActiveStack.Pop();
                            }

                            // Build completed timer
                            var built = state.RentBuilt();
                            built.Name = pending.Name;
                            built.ElapsedMs = (evt.Timestamp - pending.StartTime) * 1000.0f;

                            // Move children from pending to built
                            foreach (var child in pending.Children)
                                built.Children.Add(child);

                            if (pending.Parent is not null)
                            {
                                // Add to parent's children
                                pending.Parent.Children.Add(built);
                            }
                            else
                            {
                                // Root timer - accumulate for next snapshot
                                if (!_accumulatedRoots.TryGetValue(evt.ThreadId, out var roots))
                                {
                                    roots = new List<BuiltTimer>(32);
                                    _accumulatedRoots[evt.ThreadId] = roots;
                                }
                                roots.Add(built);
                            }

                            state.ReturnPending(pending);
                        }
                    }
                }
            }

            private void BuildFrameSnapshot(float frameTime)
            {
                _threadSnapshotsBuffer.Clear();

                // Build snapshots from accumulated roots (gathered since last snapshot)
                foreach (var kvp in _accumulatedRoots)
                {
                    var threadId = kvp.Key;
                    var roots = kvp.Value;
                    
                    if (roots.Count > 0)
                    {
                        var rootSnapshots = new ProfilerNodeSnapshot[roots.Count];
                        for (int i = 0; i < roots.Count; i++)
                        {
                            rootSnapshots[i] = BuildSnapshotFromBuilt(roots[i]);
                            
                            // Return to pool if we have the build state
                            if (_threadBuildStates.TryGetValue(threadId, out var state))
                                state.ReturnBuiltRecursive(roots[i]);
                        }
                        roots.Clear();

                        _threadSnapshotsBuffer.Add(new ProfilerThreadSnapshot(threadId, rootSnapshots));
                    }
                }

                ProfilerFrameSnapshot? frameSnapshot = null;
                if (_threadSnapshotsBuffer.Count > 0)
                    frameSnapshot = new ProfilerFrameSnapshot(frameTime, _threadSnapshotsBuffer.ToArray());

                // Update history
                if (frameSnapshot is not null)
                {
                    foreach (var threadSnapshot in frameSnapshot.Threads)
                    {
                        if (!_threadFrameHistory.TryGetValue(threadSnapshot.ThreadId, out var history))
                            history = _threadFrameHistory[threadSnapshot.ThreadId] = new Queue<float>(ThreadHistoryCapacity);

                        history.Enqueue(threadSnapshot.TotalTimeMs);
                        while (history.Count > ThreadHistoryCapacity)
                            history.Dequeue();
                    }
                }

                Dictionary<int, float[]> historySnapshot = [];
                if (_threadFrameHistory.Count > 0)
                {
                    historySnapshot = new Dictionary<int, float[]>(_threadFrameHistory.Count);
                    foreach (var kvp in _threadFrameHistory)
                        historySnapshot[kvp.Key] = kvp.Value.ToArray();
                }

                // Lock-free publish - atomic reference writes
                _readyHistorySnapshot = historySnapshot;
                _readySnapshot = frameSnapshot;
            }

            private static ProfilerNodeSnapshot BuildSnapshotFromBuilt(BuiltTimer timer)
            {
                var children = timer.Children;
                int childCount = children.Count;
                ProfilerNodeSnapshot[] childSnapshots = childCount > 0 ? new ProfilerNodeSnapshot[childCount] : [];

                for (int i = 0; i < childCount; ++i)
                    childSnapshots[i] = BuildSnapshotFromBuilt(children[i]);

                return new ProfilerNodeSnapshot(timer.Name, timer.ElapsedMs, childSnapshots);
            }

            // ============ PUBLIC API ============

            /// <summary>
            /// Clears the frame log - no longer needed with continuous processing.
            /// Kept for API compatibility.
            /// </summary>
            public void ClearFrameLog()
            {
                // No-op - stats thread processes continuously
            }

            /// <summary>
            /// Gets the latest available snapshot. Completely non-blocking.
            /// Snapshots are built asynchronously by the stats thread.
            /// </summary>
            public bool TryGetSnapshot(out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
            {
                // Lock-free reads of volatile references
                frameSnapshot = _readySnapshot;
                history = _readyHistorySnapshot;
                return frameSnapshot is not null;
            }

            /// <summary>
            /// Legacy API - just calls TryGetSnapshot. minIntervalSeconds is ignored.
            /// </summary>
            public bool TryRequestSnapshot(float minIntervalSeconds, out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
            {
                return TryGetSnapshot(out frameSnapshot, out history);
            }

            /// <summary>
            /// Starts a profiler timer. Near-zero overhead - just captures timestamp and pushes event to queue.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start(DelTimerCallback? callback, [CallerMemberName] string? methodName = null)
            {
                // Ignore callback in event-based model - callbacks would require blocking
                return Start(methodName);
            }

            /// <summary>
            /// Starts a profiler timer. Near-zero overhead - just captures timestamp and pushes event to queue.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start([CallerMemberName] string? methodName = null)
            {
                if (!EnableFrameLogging)
                    return default;

                // Thread-local correlation ID - no contention
                int correlationId = ++_tlsCorrelationId;
                int threadId = Environment.CurrentManagedThreadId;
                float timestamp = Time.Timer.Time();

                // Just push an event - no pool operations, no stack operations, no dictionary lookups
                _eventQueue.Enqueue(new ProfilerEvent(
                    ProfilerEventType.Start,
                    timestamp,
                    threadId,
                    correlationId,
                    methodName));

                return new ProfilerScope(this, correlationId, threadId);
            }

            /// <summary>
            /// Starts an async timer and returns the id of the timer.
            /// </summary>
            public Guid StartAsync(DelTimerCallback? callback = null, [CallerMemberName] string? methodName = null)
            {
                Guid id = Guid.NewGuid();
                if (!EnableFrameLogging)
                    return id;

                // Use correlation ID as guid's hash for async timers
                int correlationId = id.GetHashCode();
                int threadId = Environment.CurrentManagedThreadId;
                float timestamp = Time.Timer.Time();

                _eventQueue.Enqueue(new ProfilerEvent(
                    ProfilerEventType.Start,
                    timestamp,
                    threadId,
                    correlationId,
                    methodName));

                return id;
            }

            /// <summary>
            /// Stops an async timer by id and returns the elapsed time in milliseconds.
            /// Note: In event-based model, elapsed time is not available immediately.
            /// </summary>
            public float StopAsync(Guid id, out string? methodName)
            {
                methodName = string.Empty;
                if (!EnableFrameLogging)
                    return 0.0f;

                int correlationId = id.GetHashCode();
                int threadId = Environment.CurrentManagedThreadId;
                float timestamp = Time.Timer.Time();

                _eventQueue.Enqueue(new ProfilerEvent(
                    ProfilerEventType.Stop,
                    timestamp,
                    threadId,
                    correlationId,
                    null));

                // In event-based model we can't return elapsed time immediately
                return 0.0f;
            }

            /// <summary>
            /// Stops an async timer by id.
            /// </summary>
            public float StopAsync(Guid id)
                => StopAsync(id, out _);

            public ProfilerFrameSnapshot? GetLastFrameSnapshot()
                => _readySnapshot;

            public Dictionary<int, float[]> GetThreadHistorySnapshot()
                => _readyHistorySnapshot;

            // ============ SNAPSHOT TYPES ============

            public sealed class ProfilerFrameSnapshot(float frameTime, IReadOnlyList<ProfilerThreadSnapshot> threads)
            {
                public float FrameTime { get; } = frameTime;
                public IReadOnlyList<ProfilerThreadSnapshot> Threads { get; } = threads;
            }

            public sealed class ProfilerThreadSnapshot
            {
                public int ThreadId { get; }
                public IReadOnlyList<ProfilerNodeSnapshot> RootNodes { get; }
                public float TotalTimeMs { get; }

                public ProfilerThreadSnapshot(int threadId, IReadOnlyList<ProfilerNodeSnapshot> rootNodes)
                {
                    ThreadId = threadId;
                    RootNodes = rootNodes;
                    float total = 0f;
                    for (int i = 0; i < rootNodes.Count; i++)
                        total += rootNodes[i].ElapsedMs;
                    TotalTimeMs = total;
                }
            }

            public sealed class ProfilerNodeSnapshot(string name, float elapsedMs, IReadOnlyList<ProfilerNodeSnapshot> children)
            {
                public string Name { get; } = name;
                public float ElapsedMs { get; } = elapsedMs;
                public IReadOnlyList<ProfilerNodeSnapshot> Children { get; } = children;
            }
        }
    }
}
