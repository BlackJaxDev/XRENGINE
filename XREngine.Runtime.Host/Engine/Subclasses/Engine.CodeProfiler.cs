using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Rendering;
using XREngine.Timers;

namespace XREngine
{
    public static partial class Engine
    {
#if !XRE_PUBLISHED
        /// <summary>
        /// Event-based code profiler with near-zero overhead on the calling thread.
        /// Start() only captures a timestamp and pushes a lightweight event to a queue.
        /// All tree reconstruction and processing happens on a dedicated stats thread.
        /// </summary>
        public class CodeProfiler : XRBase
        {
#if DEBUG
            private bool _enableFrameLogging = true;
            private bool _enableComponentTiming = false;
#else
            private bool _enableFrameLogging = false;
            private bool _enableComponentTiming = false;
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
                    {
                        Interlocked.Increment(ref _sessionEpoch);
                        StopStatsThread(waitForExit: true);
                    }
                }
            }

            public bool EnableComponentTiming
            {
                get => _enableComponentTiming;
                set
                {
                    if (!SetField(ref _enableComponentTiming, value))
                        return;

                    if (!value)
                    {
                        Interlocked.Exchange(ref _activeComponentTimingFrame, null);
                        _readyComponentTimingSnapshot = null;
                    }
                }
            }

            private float _debugOutputMinElapsedMs = 1.0f;
            public float DebugOutputMinElapsedMs
            {
                get => _debugOutputMinElapsedMs;
                set => SetField(ref _debugOutputMinElapsedMs, Math.Max(0.0f, value));
            }

            private int _statsThreadIntervalMs = 4;
            public int StatsThreadIntervalMs
            {
                get => _statsThreadIntervalMs;
                set => SetField(ref _statsThreadIntervalMs, Math.Clamp(value, 1, 1000));
            }

            private int _snapshotIntervalMs = 33;
            public int SnapshotIntervalMs
            {
                get => _snapshotIntervalMs;
                set => SetField(ref _snapshotIntervalMs, Math.Clamp(value, 1, 5_000));
            }

            private int _threadHistoryCapacity = 240;
            public int ThreadHistoryCapacity
            {
                get => _threadHistoryCapacity;
                set => SetField(ref _threadHistoryCapacity, Math.Clamp(value, 2, 10_000));
            }

            private int _maxOverflowPerCycle = 8_000;
            public int MaxOverflowPerCycle
            {
                get => _maxOverflowPerCycle;
                set => SetField(ref _maxOverflowPerCycle, Math.Clamp(value, 1, 1_000_000));
            }

            private int _maxOverflowQueueSize = 50_000;
            public int MaxOverflowQueueSize
            {
                get => _maxOverflowQueueSize;
                set => SetField(ref _maxOverflowQueueSize, Math.Clamp(value, 1, 5_000_000));
            }

            private int _producerBufferCapacity = 16_384;
            public int ProducerBufferCapacity
            {
                get => _producerBufferCapacity;
                set => SetField(ref _producerBufferCapacity, NormalizeProducerBufferCapacity(value));
            }

            private int _fpsDropBaselineWindowSamples = 30;
            public int FpsDropBaselineWindowSamples
            {
                get => _fpsDropBaselineWindowSamples;
                set => SetField(ref _fpsDropBaselineWindowSamples, Math.Clamp(value, 1, 10_000));
            }

            private float _fpsDropMinPreviousFps = 10.0f;
            public float FpsDropMinPreviousFps
            {
                get => _fpsDropMinPreviousFps;
                set => SetField(ref _fpsDropMinPreviousFps, Math.Max(0.0f, value));
            }

            private float _fpsDropMinDeltaMs = 1.0f;
            public float FpsDropMinDeltaMs
            {
                get => _fpsDropMinDeltaMs;
                set => SetField(ref _fpsDropMinDeltaMs, Math.Max(0.0f, value));
            }

            private float _renderStallThresholdMs = 500.0f;
            public float RenderStallThresholdMs
            {
                get => _renderStallThresholdMs;
                set => SetField(ref _renderStallThresholdMs, Math.Max(0.0f, value));
            }

            private const int ProducerBufferHardMaxCapacity = 1 << 20;
            private const int ProducerBufferAutoGrowthFactor = 8;
            private const int RenderThreadScopeStackCapacity = 256;
            private const string RenderDispatchScopeName = "EngineTimer.DispatchRender";
            private const string CollectVisibleWaitForRenderScopeName = "EngineTimer.CollectVisibleThread.WaitForRender";
            private const string RenderStallLogFileName = "profiler-render-stalls.log";
            private const string ConditionalLoopSpikeLogFileName = "profiler-conditional-loop-spikes.log";
            private const string OneOffInvokeLogFileName = "profiler-one-off-invokes.log";
            private const double SlowScopeLogCooldownSeconds = 1.0;

            private long SnapshotIntervalTicks => EngineTimer.SecondsToStopwatchTicks(SnapshotIntervalMs / 1000.0);
            private static long SlowScopeLogCooldownTicks => EngineTimer.SecondsToStopwatchTicks(SlowScopeLogCooldownSeconds);

            private static int NormalizeProducerBufferCapacity(int value)
            {
                int normalized = Math.Clamp(value, 2, ProducerBufferHardMaxCapacity);
                int capacity = 1;
                while (capacity < normalized)
                    capacity <<= 1;
                return capacity;
            }

            private static int GetMaxAutoGrowthProducerBufferCapacity(int capacity)
            {
                long autoGrowthCapacity = (long)capacity * ProducerBufferAutoGrowthFactor;
                return NormalizeProducerBufferCapacity((int)Math.Min(autoGrowthCapacity, ProducerBufferHardMaxCapacity));
            }

            private readonly object _statsThreadLock = new();
            private readonly object _producerRegistrationLock = new();
            private readonly List<ThreadProducerBuffer> _producerBuffers = [];
            private readonly List<ThreadProducerBuffer> _producerDrainScratch = new(8);
            private readonly ConcurrentQueue<CompletedScopeEvent> _overflowCompletedEvents = new();
            private long _lastOverflowWarningTicks;
            private long _overflowDiscardedEventCount;
            private long _pendingCompletedDiscardedEventCount;
            private long _staleCompletedDiscardedEventCount;
            private long _nextScopeId;
            private long _sessionEpoch = 1L;
            private long _nextPublicationId;
            private int _activeScopeCount;
            private int _queuedCompletedScopeCount;
            private int _unresolvedLinkedChildCount;
            private readonly ConcurrentDictionary<Guid, AsyncPendingTimer> _pendingAsyncTimers = [];
            private readonly ConcurrentDictionary<long, int> _unresolvedLinkedChildrenByParentScopeId = [];

            private Thread? _statsThread;
            private CancellationTokenSource? _statsThreadCts;

            private volatile ProfilerFrameSnapshot? _readySnapshot;
            private volatile Dictionary<int, float[]> _readyHistorySnapshot = [];
            private volatile ProfilerComponentFrameSnapshot? _readyComponentTimingSnapshot;
            private ComponentTimingFrameState? _activeComponentTimingFrame;

            private readonly Dictionary<int, Queue<float>> _threadFrameHistory = [];
            private readonly Dictionary<int, ThreadBuildState> _threadBuildStates = [];
            private readonly List<ProfilerThreadSnapshot> _threadSnapshotsBuffer = new(8);
            private readonly Dictionary<int, List<BuiltTimer>> _accumulatedRoots = [];
            // Counts completed descendants that are waiting for their still-active parent.
            // The stats thread owns this state, so it needs no interlocked access.
            private int _pendingCompletedCount;

            public long OverflowDiscardedEventCount => Volatile.Read(ref _overflowDiscardedEventCount);
            public long PendingCompletedDiscardedEventCount => Volatile.Read(ref _pendingCompletedDiscardedEventCount);
            public long StaleCompletedDiscardedEventCount => Volatile.Read(ref _staleCompletedDiscardedEventCount);
            public int PendingCompletedCount => Volatile.Read(ref _pendingCompletedCount);
            public int ActiveScopeCount => Volatile.Read(ref _activeScopeCount);
            public int QueuedCompletedScopeCount => Volatile.Read(ref _queuedCompletedScopeCount);
            public int UnresolvedLinkedChildCount => Volatile.Read(ref _unresolvedLinkedChildCount);
            public long SessionEpoch => Volatile.Read(ref _sessionEpoch);
            private readonly Dictionary<(string Name, ProfilerScopeKind ScopeKind), long> _lastSlowScopeLogTicks = [];
            private readonly string[] _renderThreadScopeNames = new string[RenderThreadScopeStackCapacity];
            private readonly ProfilerScopeKind[] _renderThreadScopeKinds = new ProfilerScopeKind[RenderThreadScopeStackCapacity];
            private readonly long[] _renderThreadScopeStartTicks = new long[RenderThreadScopeStackCapacity];
            private long _lastSnapshotTicks = -1L;
            private float _lastFpsDropProcessedFrameTime = float.NegativeInfinity;
            private const long FpsDropLogCooldownMilliseconds = 1_000L;
            private long _lastFpsDropLogMilliseconds;
            private int _suppressedFpsDropLogCount;
            private int _renderThreadScopeDepth;
            private long _lastCompletedRenderFrameTicks;
            private float _lastCompletedRenderThreadTotalMs;
            private float _lastCompletedRenderThreadHotPathMs;
            private float _lastCompletedRenderThreadHotPathLeafInclusiveMs;
            private float _lastCompletedRenderThreadHotPathLeafSelfMs;
            private ProfilerScopeKind _lastCompletedRenderThreadHotPathRootScopeKind = ProfilerScopeKind.Unspecified;
            private ProfilerScopeKind _lastCompletedRenderThreadHotPathLeafScopeKind = ProfilerScopeKind.Unspecified;
            private string _lastCompletedRenderThreadHotPath = string.Empty;
            private bool _renderStallTrackingActive;
            private long _renderStallBaseTicks;
            private long _renderStallDetectedTicks;
            private long _renderStallLastCompletedFrameTicks;
            private string _renderStallScopePath = string.Empty;

            [ThreadStatic]
            private static ThreadProducerState? _tlsProducerState;

            [ThreadStatic]
            private static LinkedThreadProducerState? _tlsLinkedProducerState;

            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            internal readonly record struct CompletedScopeEvent(
                long ScopeId,
                long ParentScopeId,
                long SessionEpoch,
                int LogicalThreadId,
                int ProducerThreadId,
                int Depth,
                long StartTicks,
                long ElapsedTicks,
                string? MethodName,
                ProfilerScopeKind ScopeKind,
                bool IsAsyncRoot,
                bool IsLinked);

            internal sealed class ThreadProducerState(int threadId, ThreadProducerBuffer buffer)
            {
                public int ThreadId { get; } = threadId;
                public ThreadProducerBuffer Buffer { get; } = buffer;
                public long SessionEpoch;
                public long CurrentScopeId;
                public int Depth;

                public void PrepareForSession(long sessionEpoch)
                {
                    if (SessionEpoch == sessionEpoch)
                        return;

                    SessionEpoch = sessionEpoch;
                    CurrentScopeId = 0L;
                    Depth = 0;
                }
            }

            internal sealed class LinkedThreadProducerState(int threadId, int depth, long currentScopeId, long sessionEpoch)
            {
                public int ThreadId { get; } = threadId;
                public long SessionEpoch { get; } = sessionEpoch;
                public long CurrentScopeId = currentScopeId;
                public int Depth = depth;
            }

            public readonly struct LinkedScopeContext
            {
                internal LinkedScopeContext(int threadId, int parentDepth, long parentScopeId, long sessionEpoch)
                {
                    ThreadId = threadId;
                    ParentDepth = parentDepth;
                    ParentScopeId = parentScopeId;
                    SessionEpoch = sessionEpoch;
                }

                internal int ThreadId { get; }
                internal int ParentDepth { get; }
                internal long ParentScopeId { get; }
                internal long SessionEpoch { get; }
                internal bool IsValid => ThreadId != 0 && ParentDepth >= 0 && SessionEpoch > 0L;
            }

            internal sealed class ThreadProducerBuffer(int threadId, int capacity)
            {
                private readonly object _resizeLock = new();
                private readonly int _maxAutoGrowCapacity = GetMaxAutoGrowthProducerBufferCapacity(capacity);
                private CompletedScopeEvent[] _events = new CompletedScopeEvent[capacity];
                private int _mask = capacity - 1;
                private int _readSequence;
                private int _writeSequence;

                public int ThreadId { get; } = threadId;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool TryWrite(in CompletedScopeEvent evt)
                {
                    int write = _writeSequence;
                    int read = Volatile.Read(ref _readSequence);
                    CompletedScopeEvent[] events = _events;
                    if (write - read >= events.Length)
                    {
                        if (!TryGrow(write, read))
                            return false;

                        write = _writeSequence;
                        read = Volatile.Read(ref _readSequence);
                        events = _events;
                        if (write - read >= events.Length)
                            return false;
                    }

                    events[write & _mask] = evt;
                    Volatile.Write(ref _writeSequence, write + 1);
                    return true;
                }

                public int DrainTo(CodeProfiler profiler, int maxCount)
                {
                    lock (_resizeLock)
                    {
                        int read = _readSequence;
                        int write = Volatile.Read(ref _writeSequence);
                        int count = Math.Min(write - read, maxCount);
                        CompletedScopeEvent[] events = _events;
                        int mask = _mask;

                        for (int i = 0; i < count; i++)
                            profiler.ProcessCompletedScopeEvent(events[(read + i) & mask]);

                        if (count > 0)
                            Volatile.Write(ref _readSequence, read + count);

                        return count;
                    }
                }

                public void Clear()
                {
                    lock (_resizeLock)
                        Volatile.Write(ref _readSequence, Volatile.Read(ref _writeSequence));
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                private bool TryGrow(int write, int read)
                {
                    if (_events.Length >= _maxAutoGrowCapacity)
                        return false;

                    lock (_resizeLock)
                    {
                        read = _readSequence;
                        write = Volatile.Read(ref _writeSequence);

                        int unreadCount = write - read;
                        int currentCapacity = _events.Length;
                        if (unreadCount < currentCapacity)
                            return true;

                        int targetCapacity = currentCapacity;
                        while (targetCapacity < unreadCount + 1 && targetCapacity < _maxAutoGrowCapacity)
                            targetCapacity <<= 1;

                        if (targetCapacity > _maxAutoGrowCapacity)
                            targetCapacity = _maxAutoGrowCapacity;

                        if (targetCapacity <= currentCapacity)
                            return false;

                        var newEvents = new CompletedScopeEvent[targetCapacity];
                        for (int i = 0; i < unreadCount; i++)
                            newEvents[i] = _events[(read + i) & _mask];

                        _events = newEvents;
                        _mask = targetCapacity - 1;
                        _readSequence = 0;
                        Volatile.Write(ref _writeSequence, unreadCount);
                        return true;
                    }
                }
            }

            private sealed class AsyncPendingTimer(long scopeId, long sessionEpoch, long startTicks, int logicalThreadId, int producerThreadId, string? methodName, ProfilerScopeKind scopeKind)
            {
                public long ScopeId { get; } = scopeId;
                public long SessionEpoch { get; } = sessionEpoch;
                public long StartTicks { get; } = startTicks;
                public int LogicalThreadId { get; } = logicalThreadId;
                public int ProducerThreadId { get; } = producerThreadId;
                public string? MethodName { get; } = methodName;
                public ProfilerScopeKind ScopeKind { get; } = scopeKind;
            }

            public readonly struct ProfilerScope : IDisposable
            {
                private readonly CodeProfiler? _profiler;
                private readonly ThreadProducerState? _state;
                private readonly LinkedThreadProducerState? _linkedState;
                private readonly LinkedThreadProducerState? _previousLinkedState;
                private readonly string? _methodName;
                private readonly long _startTicks;
                private readonly long _scopeId;
                private readonly long _parentScopeId;
                private readonly long _sessionEpoch;
                private readonly int _producerThreadId;
                private readonly int _depth;
                private readonly ProfilerScopeKind _scopeKind;
                private readonly bool _restoreLinkedStateOnDispose;

                internal ProfilerScope(CodeProfiler profiler, ThreadProducerState state, long startTicks, long scopeId, long parentScopeId, long sessionEpoch, int producerThreadId, int depth, string? methodName, ProfilerScopeKind scopeKind)
                {
                    _profiler = profiler;
                    _state = state;
                    _linkedState = null;
                    _previousLinkedState = null;
                    _methodName = methodName;
                    _startTicks = startTicks;
                    _scopeId = scopeId;
                    _parentScopeId = parentScopeId;
                    _sessionEpoch = sessionEpoch;
                    _producerThreadId = producerThreadId;
                    _depth = depth;
                    _scopeKind = scopeKind;
                    _restoreLinkedStateOnDispose = false;
                }

                internal ProfilerScope(
                    CodeProfiler profiler,
                    LinkedThreadProducerState linkedState,
                    LinkedThreadProducerState? previousLinkedState,
                    bool restoreLinkedStateOnDispose,
                    long startTicks,
                    long scopeId,
                    long parentScopeId,
                    long sessionEpoch,
                    int producerThreadId,
                    int depth,
                    string? methodName,
                    ProfilerScopeKind scopeKind)
                {
                    _profiler = profiler;
                    _state = null;
                    _linkedState = linkedState;
                    _previousLinkedState = previousLinkedState;
                    _methodName = methodName;
                    _startTicks = startTicks;
                    _scopeId = scopeId;
                    _parentScopeId = parentScopeId;
                    _sessionEpoch = sessionEpoch;
                    _producerThreadId = producerThreadId;
                    _depth = depth;
                    _scopeKind = scopeKind;
                    _restoreLinkedStateOnDispose = restoreLinkedStateOnDispose;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Dispose()
                {
                    if (_profiler is null)
                        return;

                    if (_linkedState is not null)
                    {
                        bool currentSession = _sessionEpoch == _profiler.SessionEpoch;
                        if (currentSession && _linkedState.CurrentScopeId == _scopeId)
                        {
                            _linkedState.Depth = _depth > 0 ? _depth - 1 : 0;
                            _linkedState.CurrentScopeId = _parentScopeId;
                        }

                        long linkedEndTicks = Stopwatch.GetTimestamp();
                        if (_profiler._enableFrameLogging && currentSession)
                        {
                            long linkedElapsedTicks = linkedEndTicks - _startTicks;
                            if (linkedElapsedTicks < 0L)
                                linkedElapsedTicks = 0L;

                            _profiler._overflowCompletedEvents.Enqueue(new CompletedScopeEvent(
                                _scopeId,
                                _parentScopeId,
                                _sessionEpoch,
                                _linkedState.ThreadId,
                                _producerThreadId,
                                _depth,
                                _startTicks,
                                linkedElapsedTicks,
                                _methodName,
                                _scopeKind,
                                IsAsyncRoot: false,
                                IsLinked: _restoreLinkedStateOnDispose));
                            Interlocked.Increment(ref _profiler._queuedCompletedScopeCount);
                            Interlocked.Decrement(ref _profiler._activeScopeCount);
                        }
                        else if (!currentSession)
                            Interlocked.Increment(ref _profiler._staleCompletedDiscardedEventCount);

                        if (_restoreLinkedStateOnDispose)
                            _tlsLinkedProducerState = _previousLinkedState;

                        return;
                    }

                    if (_state is null)
                        return;

                    bool currentProducerSession = _sessionEpoch == _profiler.SessionEpoch;
                    int newDepth = _depth > 0 ? _depth - 1 : 0;
                    if (currentProducerSession && _state.CurrentScopeId == _scopeId)
                    {
                        _state.Depth = newDepth;
                        _state.CurrentScopeId = _parentScopeId;
                    }

                    long endTicks = Stopwatch.GetTimestamp();
                    if (currentProducerSession && _state.ThreadId == RuntimeEngine.RenderThreadId)
                        _profiler.RecordRenderThreadScopeExit(newDepth, endTicks, _methodName, _scopeKind);

                    if (!_profiler._enableFrameLogging || !currentProducerSession)
                    {
                        if (!currentProducerSession)
                            Interlocked.Increment(ref _profiler._staleCompletedDiscardedEventCount);
                        return;
                    }

                    long elapsedTicks = endTicks - _startTicks;
                    if (elapsedTicks < 0L)
                        elapsedTicks = 0L;

                    var completedEvent = new CompletedScopeEvent(
                        _scopeId,
                        _parentScopeId,
                        _sessionEpoch,
                        _state.ThreadId,
                        _producerThreadId,
                        _depth,
                        _startTicks,
                        elapsedTicks,
                        _methodName,
                        _scopeKind,
                        IsAsyncRoot: false,
                        IsLinked: false);
                    Interlocked.Increment(ref _profiler._queuedCompletedScopeCount);
                    Interlocked.Decrement(ref _profiler._activeScopeCount);

                    if (!_state.Buffer.TryWrite(completedEvent))
                        _profiler._overflowCompletedEvents.Enqueue(completedEvent);
                }
            }

            private sealed class BuiltTimer
            {
                public string Name = string.Empty;
                public long ScopeId;
                public long ParentScopeId;
                public long SessionEpoch;
                public int LogicalThreadId;
                public int ProducerThreadId;
                public long StartTicks;
                public long ElapsedTicks;
                public int Depth;
                public ProfilerScopeKind ScopeKind;
                public bool IsLinked;
                public List<BuiltTimer> Children = new(4);

                public void Reset()
                {
                    Name = string.Empty;
                    ScopeId = 0L;
                    ParentScopeId = 0L;
                    SessionEpoch = 0L;
                    LogicalThreadId = 0;
                    ProducerThreadId = 0;
                    StartTicks = 0L;
                    ElapsedTicks = 0L;
                    Depth = 0;
                    ScopeKind = ProfilerScopeKind.Unspecified;
                    IsLinked = false;
                    Children.Clear();
                }
            }

            private sealed class ThreadBuildState
            {
                public Dictionary<long, List<BuiltTimer>> PendingChildrenByParentScopeId = new(32);
                public Dictionary<long, BuiltTimer> CompletedByScopeId = new(64);
                public Queue<BuiltTimer> BuiltTimerPool = new(64);

                public BuiltTimer RentBuilt()
                    => BuiltTimerPool.Count > 0 ? BuiltTimerPool.Dequeue() : new BuiltTimer();

                public void ReturnBuilt(BuiltTimer timer)
                {
                    timer.Reset();
                    BuiltTimerPool.Enqueue(timer);
                }

                public void ReturnBuiltRecursive(BuiltTimer timer)
                {
                    foreach (var child in timer.Children)
                        ReturnBuiltRecursive(child);
                    ReturnBuilt(timer);
                }
            }

            private sealed class ComponentTimingFrameState(float frameTime)
            {
                public float FrameTime { get; } = frameTime;
                public ConcurrentDictionary<Guid, ComponentTimingAccumulator> Components { get; } = [];
            }

            private sealed class ComponentTimingAccumulator(XRComponent component)
            {
                private long _elapsedTicks;
                private int _callCount;
                private int _tickGroupMask;

                public XRComponent Component { get; } = component;
                public long ElapsedTicks => Interlocked.Read(ref _elapsedTicks);
                public int CallCount => Volatile.Read(ref _callCount);
                public int TickGroupMask => Volatile.Read(ref _tickGroupMask);

                public void Add(long elapsedTicks, ETickGroup group)
                {
                    Interlocked.Add(ref _elapsedTicks, elapsedTicks);
                    Interlocked.Increment(ref _callCount);

                    int mask = 1 << (int)group;
                    int currentMask;
                    int updatedMask;
                    do
                    {
                        currentMask = _tickGroupMask;
                        updatedMask = currentMask | mask;
                    }
                    while (currentMask != updatedMask && Interlocked.CompareExchange(ref _tickGroupMask, updatedMask, currentMask) != currentMask);
                }
            }

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

            public ConcurrentDictionary<int, CodeProfilerTimer> RootEntriesPerThread { get; } = [];

            public CodeProfiler()
            {
                if (_enableFrameLogging)
                    StartStatsThread();
            }

            ~CodeProfiler()
            {
                StopStatsThread(waitForExit: false);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ThreadProducerState GetOrCreateThreadProducerState()
                => _tlsProducerState is { } state ? state : CreateThreadProducerStateSlow();

            [MethodImpl(MethodImplOptions.NoInlining)]
            private ThreadProducerState CreateThreadProducerStateSlow()
            {
                int threadId = Environment.CurrentManagedThreadId;
                var buffer = new ThreadProducerBuffer(threadId, ProducerBufferCapacity);
                var state = new ThreadProducerState(threadId, buffer);
                _tlsProducerState = state;

                lock (_producerRegistrationLock)
                    _producerBuffers.Add(buffer);

                return state;
            }

            private void StartStatsThread()
            {
                lock (_statsThreadLock)
                {
                    if (_statsThread is { IsAlive: true })
                        return;

                    _lastSnapshotTicks = -1L;
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
                lock (_statsThreadLock)
                {
                    if (_statsThread is null)
                        return;

                    _statsThreadCts?.Cancel();
                    if (!waitForExit)
                        return;

                    _statsThread.Join();
                    _statsThread = null;
                    _statsThreadCts?.Dispose();
                    _statsThreadCts = null;
                }

                while (_overflowCompletedEvents.TryDequeue(out _)) { }
                lock (_producerRegistrationLock)
                {
                    for (int i = 0; i < _producerBuffers.Count; i++)
                        _producerBuffers[i].Clear();
                }
                _pendingAsyncTimers.Clear();
                _unresolvedLinkedChildrenByParentScopeId.Clear();

                // Clear stale tree-build state so a restart doesn't process orphaned data
                foreach (var state in _threadBuildStates.Values)
                {
                    foreach (var pendingChildren in state.PendingChildrenByParentScopeId.Values)
                    {
                        for (int i = 0; i < pendingChildren.Count; i++)
                            state.ReturnBuiltRecursive(pendingChildren[i]);
                    }
                    state.PendingChildrenByParentScopeId.Clear();
                    state.CompletedByScopeId.Clear();
                }
                _pendingCompletedCount = 0;
                Volatile.Write(ref _activeScopeCount, 0);
                Volatile.Write(ref _queuedCompletedScopeCount, 0);
                Volatile.Write(ref _unresolvedLinkedChildCount, 0);
                foreach (var (threadId, roots) in _accumulatedRoots)
                {
                    if (_threadBuildStates.TryGetValue(threadId, out var state))
                    {
                        for (int i = 0; i < roots.Count; i++)
                            state.ReturnBuiltRecursive(roots[i]);
                    }
                    roots.Clear();
                }
                _threadFrameHistory.Clear();
                _lastSlowScopeLogTicks.Clear();
                _readySnapshot = null;
                _readyHistorySnapshot = [];
                Volatile.Write(ref _renderThreadScopeDepth, 0);
                Volatile.Write(ref _lastCompletedRenderFrameTicks, 0L);
                _lastCompletedRenderThreadTotalMs = 0.0f;
                _lastCompletedRenderThreadHotPathMs = 0.0f;
                _lastCompletedRenderThreadHotPathLeafInclusiveMs = 0.0f;
                _lastCompletedRenderThreadHotPathLeafSelfMs = 0.0f;
                _lastCompletedRenderThreadHotPathRootScopeKind = ProfilerScopeKind.Unspecified;
                _lastCompletedRenderThreadHotPathLeafScopeKind = ProfilerScopeKind.Unspecified;
                _lastCompletedRenderThreadHotPath = string.Empty;
                ResetRenderStallTracking();
            }

            private void StatsThreadMain(object? state)
            {
                if (state is not CancellationToken token)
                    return;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int scopesProcessed = DrainCompletedScopes();

                        long nowTicks = Stopwatch.GetTimestamp();
                        if (_lastSnapshotTicks < 0L || nowTicks - _lastSnapshotTicks >= SnapshotIntervalTicks)
                        {
                            BuildFrameSnapshot(nowTicks);
                            _lastSnapshotTicks = nowTicks;
                        }

                        CheckRenderThreadStall(nowTicks);

                        if (scopesProcessed == 0)
                            Thread.Sleep(StatsThreadIntervalMs);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }

            private int DrainCompletedScopes()
            {
                int processed = 0;

                lock (_producerRegistrationLock)
                {
                    _producerDrainScratch.Clear();
                    _producerDrainScratch.AddRange(_producerBuffers);
                }

                // The hot path stays on grow-only array-backed producer buffers; drain all available events.
                foreach (var buffer in _producerDrainScratch)
                    processed += buffer.DrainTo(this, int.MaxValue);

                // Overflow is reserved for async roots and producers that hit their growth ceiling.
                // Drain up to a cap per cycle, then discard stale overflow if it grows too large.
                int overflowDrained = 0;
                while (overflowDrained < MaxOverflowPerCycle && _overflowCompletedEvents.TryDequeue(out var overflowEvent))
                {
                    ProcessCompletedScopeEvent(overflowEvent);
                    overflowDrained++;
                }
                processed += overflowDrained;

                if (_overflowCompletedEvents.Count > MaxOverflowQueueSize)
                {
                    int discarded = 0;
                    while (_overflowCompletedEvents.TryDequeue(out var discardedEvent))
                    {
                        Interlocked.Decrement(ref _queuedCompletedScopeCount);
                        if (discardedEvent.IsLinked)
                            ResolveLinkedChild(discardedEvent.ParentScopeId);
                        discarded++;
                    }

                    Interlocked.Add(ref _overflowDiscardedEventCount, discarded);

                    // Rate-limit the warning to at most once per 10 seconds
                    long nowTicks = Environment.TickCount64;
                    if (nowTicks - _lastOverflowWarningTicks >= 10_000)
                    {
                        _lastOverflowWarningTicks = nowTicks;
                        Debug.LogWarning($"Profiler overflow queue exceeded capacity ({discarded} stale events discarded).");
                    }
                }

                return processed;
            }

            private void ProcessCompletedScopeEvent(in CompletedScopeEvent completedEvent)
            {
                Interlocked.Decrement(ref _queuedCompletedScopeCount);
                if (completedEvent.SessionEpoch != SessionEpoch)
                {
                    Interlocked.Increment(ref _staleCompletedDiscardedEventCount);
                    return;
                }

                LogSlowScopeByKind(completedEvent);

                if (!_threadBuildStates.TryGetValue(completedEvent.LogicalThreadId, out var state))
                {
                    state = new ThreadBuildState();
                    _threadBuildStates[completedEvent.LogicalThreadId] = state;
                }

                var built = state.RentBuilt();
                built.Name = completedEvent.MethodName ?? string.Empty;
                built.ScopeId = completedEvent.ScopeId;
                built.ParentScopeId = completedEvent.ParentScopeId;
                built.SessionEpoch = completedEvent.SessionEpoch;
                built.LogicalThreadId = completedEvent.LogicalThreadId;
                built.ProducerThreadId = completedEvent.ProducerThreadId;
                built.StartTicks = completedEvent.StartTicks;
                built.ElapsedTicks = completedEvent.ElapsedTicks;
                built.Depth = completedEvent.Depth;
                built.ScopeKind = completedEvent.ScopeKind;
                built.IsLinked = completedEvent.IsLinked;

                if (state.PendingChildrenByParentScopeId.Remove(completedEvent.ScopeId, out var pendingChildren))
                {
                    for (int i = 0; i < pendingChildren.Count; i++)
                        built.Children.Add(pendingChildren[i]);
                    _pendingCompletedCount -= pendingChildren.Count;
                }

                state.CompletedByScopeId[completedEvent.ScopeId] = built;

                if (!completedEvent.IsAsyncRoot && completedEvent.ParentScopeId != 0L)
                {
                    if (state.CompletedByScopeId.TryGetValue(completedEvent.ParentScopeId, out var completedParent))
                        completedParent.Children.Add(built);
                    else
                        RetainPendingCompleted(state, built);

                    if (completedEvent.IsLinked)
                        ResolveLinkedChild(completedEvent.ParentScopeId);
                    return;
                }

                if (!_accumulatedRoots.TryGetValue(completedEvent.LogicalThreadId, out var roots))
                {
                    roots = new List<BuiltTimer>(32);
                    _accumulatedRoots[completedEvent.LogicalThreadId] = roots;
                }

                roots.Add(built);
            }

            private void ResolveLinkedChild(long parentScopeId)
            {
                if (parentScopeId == 0L)
                    return;

                while (_unresolvedLinkedChildrenByParentScopeId.TryGetValue(parentScopeId, out int count))
                {
                    if (count <= 1)
                    {
                        if (_unresolvedLinkedChildrenByParentScopeId.TryRemove(parentScopeId, out _))
                        {
                            Interlocked.Decrement(ref _unresolvedLinkedChildCount);
                            return;
                        }
                    }
                    else if (_unresolvedLinkedChildrenByParentScopeId.TryUpdate(parentScopeId, count - 1, count))
                    {
                        Interlocked.Decrement(ref _unresolvedLinkedChildCount);
                        return;
                    }
                }
            }

            /// <summary>
            /// Retains a completed descendant until the matching parent completion arrives.
            /// Snapshot publication must not consume this state: an active parent can span many
            /// snapshot epochs. The existing overflow limit also bounds retained reconstruction
            /// state when a producer leaves a parent scope open indefinitely.
            /// </summary>
            private void RetainPendingCompleted(ThreadBuildState state, BuiltTimer built)
            {
                if (_pendingCompletedCount >= MaxOverflowQueueSize)
                {
                    RemoveCompletedScopeMappings(state, built);
                    state.ReturnBuiltRecursive(built);
                    Interlocked.Increment(ref _pendingCompletedDiscardedEventCount);
                    LogPendingCompletedOverflow();
                    return;
                }

                if (!state.PendingChildrenByParentScopeId.TryGetValue(built.ParentScopeId, out var siblings))
                {
                    siblings = new List<BuiltTimer>(2);
                    state.PendingChildrenByParentScopeId[built.ParentScopeId] = siblings;
                }

                siblings.Add(built);
                _pendingCompletedCount++;
            }

            private void LogPendingCompletedOverflow()
            {
                long nowTicks = Environment.TickCount64;
                if (nowTicks - _lastOverflowWarningTicks < 10_000)
                    return;

                _lastOverflowWarningTicks = nowTicks;
                Debug.LogWarning($"Profiler pending completed scopes exceeded capacity ({MaxOverflowQueueSize}); discarding descendants whose parent has not completed.");
            }

            private void LogSlowScopeByKind(in CompletedScopeEvent completedEvent)
            {
                ProfilerScopeKind scopeKind = completedEvent.ScopeKind;
                if (scopeKind is not (ProfilerScopeKind.ConditionalLoop or ProfilerScopeKind.OneOffInvoke))
                    return;

                float elapsedMs = TicksToMilliseconds(completedEvent.ElapsedTicks);
                if (elapsedMs < _debugOutputMinElapsedMs)
                    return;

                string name = string.IsNullOrWhiteSpace(completedEvent.MethodName)
                    ? "<unnamed>"
                    : completedEvent.MethodName!;
                long nowTicks = Stopwatch.GetTimestamp();
                var key = (name, scopeKind);
                if (_lastSlowScopeLogTicks.TryGetValue(key, out long lastLogTicks)
                    && nowTicks - lastLogTicks < SlowScopeLogCooldownTicks)
                {
                    return;
                }

                _lastSlowScopeLogTicks[key] = nowTicks;

                try
                {
                    string fileName = scopeKind == ProfilerScopeKind.OneOffInvoke
                        ? OneOffInvokeLogFileName
                        : ConditionalLoopSpikeLogFileName;

                    var builder = new StringBuilder(512);
                    builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] Slow profiler scope");
                    builder.Append("ScopeKind: ").Append(scopeKind).AppendLine();
                    builder.Append("LoggingPolicy: ").Append(GetScopeLoggingPolicy(scopeKind)).AppendLine();
                    builder.Append("ScopeName: ").Append(name).AppendLine();
                    builder.Append("ScopeId: ").Append(completedEvent.ScopeId).AppendLine();
                    builder.Append("ParentScopeId: ").Append(completedEvent.ParentScopeId).AppendLine();
                    builder.Append("SessionEpoch: ").Append(completedEvent.SessionEpoch).AppendLine();
                    builder.Append("LogicalThreadId: ").Append(completedEvent.LogicalThreadId).AppendLine();
                    builder.Append("ProducerThreadId: ").Append(completedEvent.ProducerThreadId).AppendLine();
                    builder.Append("Depth: ").Append(completedEvent.Depth).AppendLine();
                    builder.Append("ElapsedMs: ").Append(elapsedMs.ToString("F3")).AppendLine();
                    builder.Append("IsAsyncRoot: ").Append(completedEvent.IsAsyncRoot).AppendLine();
                    Debug.WriteAuxiliaryLog(fileName, builder.ToString());
                }
                catch
                {
                    // Diagnostics must never break the profiler stats thread.
                }
            }

            private void BuildFrameSnapshot(long frameTicks)
            {
                _threadSnapshotsBuffer.Clear();

                foreach (var kvp in _accumulatedRoots)
                {
                    int threadId = kvp.Key;
                    var roots = kvp.Value;
                    if (roots.Count == 0)
                        continue;

                    var rootSnapshots = new List<ProfilerNodeSnapshot>(roots.Count);
                    int retainedRootCount = 0;
                    for (int i = 0; i < roots.Count; i++)
                    {
                        BuiltTimer root = roots[i];
                        if (HasUnresolvedLinkedDescendant(root))
                        {
                            roots[retainedRootCount++] = root;
                            continue;
                        }

                        rootSnapshots.Add(BuildSnapshotFromBuilt(root));
                        if (_threadBuildStates.TryGetValue(threadId, out var state))
                        {
                            RemoveCompletedScopeMappings(state, root);
                            state.ReturnBuiltRecursive(root);
                        }
                    }

                    if (retainedRootCount < roots.Count)
                        roots.RemoveRange(retainedRootCount, roots.Count - retainedRootCount);

                    if (rootSnapshots.Count > 0)
                        _threadSnapshotsBuffer.Add(new ProfilerThreadSnapshot(threadId, rootSnapshots.ToArray()));
                }

                ProfilerFrameSnapshot? frameSnapshot = null;
                if (_threadSnapshotsBuffer.Count > 0)
                {
                    float frameTime = Time.Timer.Time();
                    frameSnapshot = new ProfilerFrameSnapshot(
                        frameTime,
                        SessionEpoch,
                        Interlocked.Increment(ref _nextPublicationId),
                        frameTicks,
                        Time.Timer.UpdateFrameId,
                        RuntimeEngine.Rendering.State.RenderFrameId,
                        ActiveScopeCount,
                        QueuedCompletedScopeCount,
                        PendingCompletedCount,
                        UnresolvedLinkedChildCount,
                        StaleCompletedDiscardedEventCount,
                        _threadSnapshotsBuffer.ToArray(),
                        _readyComponentTimingSnapshot);
                }

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

                if (frameSnapshot is not null)
                {
                    UpdateLastRenderThreadSnapshot(frameSnapshot);
                    LogFpsDrops(frameSnapshot, historySnapshot);
                }

                _readyHistorySnapshot = historySnapshot;
                _readySnapshot = frameSnapshot;
            }

            private void UpdateLastRenderThreadSnapshot(ProfilerFrameSnapshot frameSnapshot)
            {
                int renderThreadId = RuntimeEngine.RenderThreadId;
                if (renderThreadId <= 0)
                    return;

                for (int i = 0; i < frameSnapshot.Threads.Count; i++)
                {
                    ProfilerThreadSnapshot thread = frameSnapshot.Threads[i];
                    if (thread.ThreadId != renderThreadId)
                        continue;

                    _lastCompletedRenderThreadTotalMs = thread.TotalTimeMs;
                    _lastCompletedRenderThreadHotPath = GetHottestPath(
                        thread.RootNodes,
                        out float hotPathRootMs,
                        out ProfilerScopeKind hotPathRootScopeKind,
                        out float hotPathLeafInclusiveMs,
                        out float hotPathLeafSelfMs,
                        out ProfilerScopeKind hotPathLeafScopeKind);
                    _lastCompletedRenderThreadHotPathMs = hotPathRootMs;
                    _lastCompletedRenderThreadHotPathLeafInclusiveMs = hotPathLeafInclusiveMs;
                    _lastCompletedRenderThreadHotPathLeafSelfMs = hotPathLeafSelfMs;
                    _lastCompletedRenderThreadHotPathRootScopeKind = hotPathRootScopeKind;
                    _lastCompletedRenderThreadHotPathLeafScopeKind = hotPathLeafScopeKind;
                    return;
                }
            }

            private void CheckRenderThreadStall(long nowTicks)
            {
                if (RenderStallThresholdMs <= 0.0f)
                {
                    ResetRenderStallTracking();
                    return;
                }

                long lastCompletedRenderTicks = Volatile.Read(ref _lastCompletedRenderFrameTicks);
                bool renderDispatchActive = IsRenderDispatchActive();
                if (_renderStallTrackingActive)
                {
                    if (lastCompletedRenderTicks > _renderStallLastCompletedFrameTicks)
                    {
                        LogRenderThreadStallRecovered(lastCompletedRenderTicks);
                        ResetRenderStallTracking();
                        return;
                    }

                    if (!renderDispatchActive)
                    {
                        ResetRenderStallTracking();
                        return;
                    }

                    return;
                }

                if (!renderDispatchActive)
                    return;

                if (!TrySnapshotActiveRenderDispatch(out int scopeDepth, out long rootStartTicks, out long leafStartTicks, out string leafScope, out string scopePath))
                    return;

                long baseTicks = lastCompletedRenderTicks > 0L ? lastCompletedRenderTicks : rootStartTicks;
                float noCompletedRenderMs = TicksToMilliseconds(Math.Max(0L, nowTicks - baseTicks));
                if (noCompletedRenderMs < RenderStallThresholdMs)
                    return;

                _renderStallTrackingActive = true;
                _renderStallBaseTicks = baseTicks;
                _renderStallDetectedTicks = nowTicks;
                _renderStallLastCompletedFrameTicks = lastCompletedRenderTicks;
                _renderStallScopePath = scopePath;

                LogRenderThreadStallDetected(
                    nowTicks,
                    lastCompletedRenderTicks,
                    scopeDepth,
                    rootStartTicks,
                    leafStartTicks,
                    leafScope,
                    scopePath);
            }

            private bool IsRenderDispatchActive()
            {
                int depth = Volatile.Read(ref _renderThreadScopeDepth);
                return depth > 0 && string.Equals(_renderThreadScopeNames[0], RenderDispatchScopeName, StringComparison.Ordinal);
            }

            private bool TrySnapshotActiveRenderDispatch(
                out int scopeDepth,
                out long rootStartTicks,
                out long leafStartTicks,
                out string leafScope,
                out string scopePath)
            {
                scopeDepth = Volatile.Read(ref _renderThreadScopeDepth);
                rootStartTicks = 0L;
                leafStartTicks = 0L;
                leafScope = string.Empty;
                scopePath = string.Empty;

                if (scopeDepth <= 0)
                    return false;

                scopeDepth = Math.Min(scopeDepth, RenderThreadScopeStackCapacity);
                if (!string.Equals(_renderThreadScopeNames[0], RenderDispatchScopeName, StringComparison.Ordinal))
                    return false;

                rootStartTicks = _renderThreadScopeStartTicks[0];
                int leafIndex = scopeDepth - 1;
                leafStartTicks = _renderThreadScopeStartTicks[leafIndex];
                leafScope = FormatScopeLabel(_renderThreadScopeNames[leafIndex] ?? RenderDispatchScopeName, _renderThreadScopeKinds[leafIndex]);

                var builder = new StringBuilder(scopeDepth * 32);
                for (int i = 0; i < scopeDepth; i++)
                {
                    string? name = _renderThreadScopeNames[i];
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (builder.Length > 0)
                        builder.Append(" > ");
                    AppendScopeLabel(builder, name, _renderThreadScopeKinds[i]);
                }

                scopePath = builder.Length == 0 ? RenderDispatchScopeName : builder.ToString();
                return true;
            }

            private void LogRenderThreadStallDetected(
                long detectedTicks,
                long lastCompletedRenderTicks,
                int scopeDepth,
                long rootStartTicks,
                long leafStartTicks,
                string leafScope,
                string scopePath)
            {
                try
                {
                    string timingBasis = lastCompletedRenderTicks > 0L
                        ? "LastCompletedRender"
                        : "CurrentRenderFrameStart";
                    float noCompletedRenderMs = TicksToMilliseconds(Math.Max(0L, detectedTicks - _renderStallBaseTicks));
                    float currentFrameElapsedMs = TicksToMilliseconds(Math.Max(0L, detectedTicks - rootStartTicks));
                    float currentLeafElapsedMs = TicksToMilliseconds(Math.Max(0L, detectedTicks - leafStartTicks));

                    var builder = new StringBuilder(1024);
                    builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] Render stall detected");
                    builder.Append("ThresholdMs: ").Append(RenderStallThresholdMs.ToString("F3")).AppendLine();
                    builder.Append("TimingBasis: ").Append(timingBasis).AppendLine();
                    builder.Append("RenderThreadId: ").Append(RuntimeEngine.RenderThreadId).AppendLine();
                    builder.Append("NoCompletedRenderForMs: ").Append(noCompletedRenderMs.ToString("F3")).AppendLine();
                    builder.Append("CurrentRenderFrameElapsedMs: ").Append(currentFrameElapsedMs.ToString("F3")).AppendLine();
                    builder.Append("CurrentRenderLeafElapsedMs: ").Append(currentLeafElapsedMs.ToString("F3")).AppendLine();
                    builder.Append("RenderScopeDepth: ").Append(scopeDepth).AppendLine();
                    builder.Append("RenderScopeLeaf: ").Append(leafScope).AppendLine();
                    builder.Append("RenderScopePath: ").Append(scopePath).AppendLine();
                    builder.Append("QueuedRenderJobsNow: ").Append(GetQueuedRenderThreadJobCount()).AppendLine();
                    builder.Append("IsDispatchingRenderFrame: ").Append(IsDispatchingRenderFrame).AppendLine();
                    XRTexture2D.AppendRenderWorkBudgetProfilerSummary(builder);

                    if (!string.IsNullOrWhiteSpace(_lastCompletedRenderThreadHotPath))
                    {
                        builder.Append("LastCompletedRenderThreadTotalMs: ").Append(_lastCompletedRenderThreadTotalMs.ToString("F3")).AppendLine();
                        builder.Append("LastCompletedRenderRootMs: ").Append(_lastCompletedRenderThreadHotPathMs.ToString("F3")).AppendLine();
                        builder.Append("LastCompletedRenderLeafInclusiveMs: ").Append(_lastCompletedRenderThreadHotPathLeafInclusiveMs.ToString("F3")).AppendLine();
                        builder.Append("LastCompletedRenderLeafSelfMs: ").Append(_lastCompletedRenderThreadHotPathLeafSelfMs.ToString("F3")).AppendLine();
                        builder.Append("LastCompletedRenderRootScopeKind: ").Append(_lastCompletedRenderThreadHotPathRootScopeKind).AppendLine();
                        builder.Append("LastCompletedRenderLeafScopeKind: ").Append(_lastCompletedRenderThreadHotPathLeafScopeKind).AppendLine();
                        builder.Append("LastCompletedRenderHotPath: ").Append(_lastCompletedRenderThreadHotPath).AppendLine();
                    }

                    Debug.WriteAuxiliaryLog(RenderStallLogFileName, builder.ToString());
                }
                catch
                {
                    // Diagnostics must never break the profiler stats thread.
                }
            }

            private void LogRenderThreadStallRecovered(long recoveredTicks)
            {
                try
                {
                    float totalNoRenderMs = TicksToMilliseconds(Math.Max(0L, recoveredTicks - _renderStallBaseTicks));
                    float detectionToRecoveryMs = TicksToMilliseconds(Math.Max(0L, recoveredTicks - _renderStallDetectedTicks));

                    var builder = new StringBuilder(896);
                    builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] Render stall recovered");
                    builder.Append("ThresholdMs: ").Append(RenderStallThresholdMs.ToString("F3")).AppendLine();
                    builder.Append("RenderThreadId: ").Append(RuntimeEngine.RenderThreadId).AppendLine();
                    builder.Append("NoCompletedRenderForMs: ").Append(totalNoRenderMs.ToString("F3")).AppendLine();
                    builder.Append("DetectionToRecoveryMs: ").Append(detectionToRecoveryMs.ToString("F3")).AppendLine();
                    builder.Append("StallScopePathAtDetection: ").Append(_renderStallScopePath).AppendLine();
                    builder.Append("QueuedRenderJobsNow: ").Append(GetQueuedRenderThreadJobCount()).AppendLine();
                    builder.Append("IsDispatchingRenderFrame: ").Append(IsDispatchingRenderFrame).AppendLine();
                    XRTexture2D.AppendRenderWorkBudgetProfilerSummary(builder);

                    if (!string.IsNullOrWhiteSpace(_lastCompletedRenderThreadHotPath))
                    {
                        builder.Append("RecoveredRenderThreadTotalMs: ").Append(_lastCompletedRenderThreadTotalMs.ToString("F3")).AppendLine();
                        builder.Append("RecoveredRenderRootMs: ").Append(_lastCompletedRenderThreadHotPathMs.ToString("F3")).AppendLine();
                        builder.Append("RecoveredRenderLeafInclusiveMs: ").Append(_lastCompletedRenderThreadHotPathLeafInclusiveMs.ToString("F3")).AppendLine();
                        builder.Append("RecoveredRenderLeafSelfMs: ").Append(_lastCompletedRenderThreadHotPathLeafSelfMs.ToString("F3")).AppendLine();
                        builder.Append("RecoveredRenderRootScopeKind: ").Append(_lastCompletedRenderThreadHotPathRootScopeKind).AppendLine();
                        builder.Append("RecoveredRenderLeafScopeKind: ").Append(_lastCompletedRenderThreadHotPathLeafScopeKind).AppendLine();
                        builder.Append("RecoveredRenderHotPath: ").Append(_lastCompletedRenderThreadHotPath).AppendLine();
                    }

                    Debug.WriteAuxiliaryLog(RenderStallLogFileName, builder.ToString());
                }
                catch
                {
                    // Diagnostics must never break the profiler stats thread.
                }
            }

            private void ResetRenderStallTracking()
            {
                _renderStallTrackingActive = false;
                _renderStallBaseTicks = 0L;
                _renderStallDetectedTicks = 0L;
                _renderStallLastCompletedFrameTicks = 0L;
                _renderStallScopePath = string.Empty;
            }

            private void LogFpsDrops(ProfilerFrameSnapshot frame, Dictionary<int, float[]> history)
            {
                if (!float.IsFinite(frame.FrameTime) || frame.FrameTime == _lastFpsDropProcessedFrameTime)
                    return;

                _lastFpsDropProcessedFrameTime = frame.FrameTime;

                if (frame.Threads.Count == 0)
                    return;

                ProfilerThreadSnapshot? worstThread = null;
                float worstCurrentMs = 0.0f;
                float worstPreviousMs = 0.0f;
                float worstBaselineMs = 0.0f;
                float worstCurrentFps = 0.0f;
                float worstPreviousFps = 0.0f;
                float worstBaselineFps = 0.0f;
                float worstComparisonFps = 0.0f;
                float worstDeltaMs = 0.0f;
                float worstDeltaFps = 0.0f;
                float worstDropFraction = 0.0f;

                foreach (var thread in frame.Threads)
                {
                    if (!history.TryGetValue(thread.ThreadId, out float[]? samples) || samples.Length < 2)
                        continue;

                    float currentMs = samples[^1];
                    float previousMs = samples[^2];
                    if (currentMs <= 0.0001f || previousMs <= 0.0001f)
                        continue;

                    float currentFps = 1000f / currentMs;
                    float previousFps = 1000f / previousMs;
                    if (previousFps < FpsDropMinPreviousFps)
                        continue;

                    float deltaMs = currentMs - previousMs;
                    if (deltaMs < FpsDropMinDeltaMs)
                        continue;

                    float baselineMs = GetMedianTailMs(samples, FpsDropBaselineWindowSamples, skipFromEnd: 1);
                    if (baselineMs <= 0.0001f)
                        continue;

                    float baselineFps = 1000f / baselineMs;
                    float comparisonFps = MathF.Min(previousFps, baselineFps);
                    if (comparisonFps <= 0.0001f)
                        continue;

                    float deltaFps = comparisonFps - currentFps;
                    if (deltaFps <= 0.0f)
                        continue;

                    float dropFraction = Math.Clamp(deltaFps / comparisonFps, 0f, 1f);
                    if (worstThread is not null &&
                        (deltaMs < worstDeltaMs ||
                         (deltaMs == worstDeltaMs && dropFraction <= worstDropFraction)))
                    {
                        continue;
                    }

                    worstThread = thread;
                    worstCurrentMs = currentMs;
                    worstPreviousMs = previousMs;
                    worstBaselineMs = baselineMs;
                    worstCurrentFps = currentFps;
                    worstPreviousFps = previousFps;
                    worstBaselineFps = baselineFps;
                    worstComparisonFps = comparisonFps;
                    worstDeltaMs = deltaMs;
                    worstDeltaFps = deltaFps;
                    worstDropFraction = dropFraction;
                }

                if (worstThread is null)
                    return;

                long nowMilliseconds = Environment.TickCount64;
                if (_lastFpsDropLogMilliseconds != 0L &&
                    nowMilliseconds - _lastFpsDropLogMilliseconds < FpsDropLogCooldownMilliseconds)
                {
                    _suppressedFpsDropLogCount++;
                    return;
                }

                int suppressedDropCount = _suppressedFpsDropLogCount;
                _suppressedFpsDropLogCount = 0;
                _lastFpsDropLogMilliseconds = nowMilliseconds;

                string hotPath = GetHottestPath(
                    worstThread.RootNodes,
                    out float hotPathRootMs,
                    out ProfilerScopeKind hotPathRootScopeKind,
                    out float hotPathLeafInclusiveMs,
                    out float hotPathLeafSelfMs,
                    out ProfilerScopeKind hotPathLeafScopeKind);

                var builder = new StringBuilder(1024);
                builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] FPS drop detected");
                builder.Append("FrameTimeSeconds: ").Append(frame.FrameTime.ToString("F6")).AppendLine();
                builder.Append("ThreadId: ").Append(worstThread.ThreadId).AppendLine();
                builder.Append("ThreadWorkTimeMs: ").Append(worstThread.TotalTimeMs.ToString("F3")).AppendLine();
                builder.Append("ThreadWallTimeMs: ").Append(worstThread.WallTimeMs.ToString("F3")).AppendLine();
                builder.Append("ThreadDownstreamRenderPressureMs: ").Append(worstThread.DownstreamRenderPressureMs.ToString("F3")).AppendLine();
                builder.Append("CurrentMs: ").Append(worstCurrentMs.ToString("F3")).AppendLine();
                builder.Append("PreviousMs: ").Append(worstPreviousMs.ToString("F3")).AppendLine();
                builder.Append("BaselineMs: ").Append(worstBaselineMs.ToString("F3")).AppendLine();
                builder.Append("CurrentFps: ").Append(worstCurrentFps.ToString("F2")).AppendLine();
                builder.Append("PreviousFps: ").Append(worstPreviousFps.ToString("F2")).AppendLine();
                builder.Append("BaselineFps: ").Append(worstBaselineFps.ToString("F2")).AppendLine();
                builder.Append("ComparisonFps: ").Append(worstComparisonFps.ToString("F2")).AppendLine();
                builder.Append("DeltaMs: ").Append(worstDeltaMs.ToString("F3")).AppendLine();
                builder.Append("DeltaFps: ").Append(worstDeltaFps.ToString("F2")).AppendLine();
                builder.Append("DropPercent: ").Append((worstDropFraction * 100.0f).ToString("F1")).AppendLine();
                builder.Append("SuppressedDropsSincePreviousLog: ").Append(suppressedDropCount).AppendLine();
                builder.Append("HotPathRootMs: ").Append(hotPathRootMs.ToString("F3")).AppendLine();
                builder.Append("HotPathLeafInclusiveMs: ").Append(hotPathLeafInclusiveMs.ToString("F3")).AppendLine();
                builder.Append("HotPathLeafSelfMs: ").Append(hotPathLeafSelfMs.ToString("F3")).AppendLine();
                builder.Append("HotPathRootScopeKind: ").Append(hotPathRootScopeKind).AppendLine();
                builder.Append("HotPathLeafScopeKind: ").Append(hotPathLeafScopeKind).AppendLine();
                builder.Append("HotPathLoggingPolicy: ").Append(GetScopeLoggingPolicy(hotPathLeafScopeKind)).AppendLine();
                builder.Append("HotPath: ").Append(hotPath).AppendLine();

                if (hotPath.Contains("WaitForRender", StringComparison.Ordinal)
                    && TryGetLikelyBlockingThread(frame.Threads, worstThread.ThreadId, out var blockingThread))
                {
                    string blockingHotPath = GetHottestPath(
                        blockingThread.RootNodes,
                        out float blockingHotPathRootMs,
                        out ProfilerScopeKind blockingHotPathRootScopeKind,
                        out float blockingHotPathLeafInclusiveMs,
                        out float blockingHotPathLeafSelfMs,
                        out ProfilerScopeKind blockingHotPathLeafScopeKind);
                    builder.Append("LikelyBlockingThreadId: ").Append(blockingThread.ThreadId).AppendLine();
                    builder.Append("LikelyBlockingThreadWorkTimeMs: ").Append(blockingThread.TotalTimeMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingThreadWallTimeMs: ").Append(blockingThread.WallTimeMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingThreadDownstreamRenderPressureMs: ").Append(blockingThread.DownstreamRenderPressureMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingHotPathRootMs: ").Append(blockingHotPathRootMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingHotPathLeafInclusiveMs: ").Append(blockingHotPathLeafInclusiveMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingHotPathLeafSelfMs: ").Append(blockingHotPathLeafSelfMs.ToString("F3")).AppendLine();
                    builder.Append("LikelyBlockingHotPathRootScopeKind: ").Append(blockingHotPathRootScopeKind).AppendLine();
                    builder.Append("LikelyBlockingHotPathLeafScopeKind: ").Append(blockingHotPathLeafScopeKind).AppendLine();
                    builder.Append("LikelyBlockingHotPath: ").Append(blockingHotPath).AppendLine();
                }

                AppendRootScopeKindSummary(builder, worstThread.RootNodes);
                AppendTopRootTimings(builder, worstThread.RootNodes, 5);
                AppendTopFrameThreads(builder, frame.Threads, worstThread.ThreadId, 5);
                AppendTopComponentTimings(builder, frame.ComponentTimings?.Components, 5);
                AppendRenderMatrixStatsSnapshot(builder);
                AppendSkinnedBoundsStatsSnapshot(builder);
                AppendOctreeStatsSnapshot(builder);
                XRTexture2D.AppendRenderWorkBudgetProfilerSummary(builder);
                Debug.WriteAuxiliaryLog("profiler-fps-drops.log", builder.ToString());
            }

            private static void AppendTopRootTimings(StringBuilder builder, IReadOnlyList<ProfilerNodeSnapshot> roots, int maxCount)
            {
                if (roots.Count == 0)
                    return;

                var rankedRoots = new List<ProfilerNodeSnapshot>(roots.Count);
                for (int i = 0; i < roots.Count; i++)
                    rankedRoots.Add(roots[i]);

                rankedRoots.Sort(static (left, right) => right.ElapsedMs.CompareTo(left.ElapsedMs));

                builder.AppendLine("TopRoots:");
                int count = Math.Min(maxCount, rankedRoots.Count);
                for (int i = 0; i < count; i++)
                {
                    ProfilerNodeSnapshot root = rankedRoots[i];
                    builder.Append("  ").Append(i + 1).Append(". ");
                    AppendScopeLabel(builder, root.Name, root.ScopeKind);
                    builder.Append(" = ").Append(root.ElapsedMs.ToString("F3")).AppendLine(" ms");
                }
            }

            private static void AppendTopFrameThreads(StringBuilder builder, IReadOnlyList<ProfilerThreadSnapshot> threads, int currentThreadId, int maxCount)
            {
                if (threads.Count == 0)
                    return;

                var rankedThreads = new List<ProfilerThreadSnapshot>(threads.Count);
                for (int i = 0; i < threads.Count; i++)
                    rankedThreads.Add(threads[i]);

                rankedThreads.Sort(static (left, right) => right.TotalTimeMs.CompareTo(left.TotalTimeMs));

                builder.AppendLine("FrameTopThreads:");
                int count = Math.Min(maxCount, rankedThreads.Count);
                for (int i = 0; i < count; i++)
                {
                    ProfilerThreadSnapshot thread = rankedThreads[i];
                    string hotPath = GetHottestPath(
                        thread.RootNodes,
                        out float hotPathRootMs,
                        out _,
                        out float hotPathLeafInclusiveMs,
                        out float hotPathLeafSelfMs,
                        out ProfilerScopeKind hotPathLeafScopeKind);
                    builder.Append("  ").Append(i + 1).Append(". Thread ").Append(thread.ThreadId);
                    if (thread.ThreadId == currentThreadId)
                        builder.Append(" (current)");
                    builder.Append(" work=").Append(thread.TotalTimeMs.ToString("F3"));
                    builder.Append(" ms wall=").Append(thread.WallTimeMs.ToString("F3"));
                    builder.Append(" ms downstreamRenderPressure=").Append(thread.DownstreamRenderPressureMs.ToString("F3"));
                    builder.Append(" ms rootHot=").Append(hotPathRootMs.ToString("F3"));
                    builder.Append(" ms leafHot=").Append(hotPathLeafInclusiveMs.ToString("F3"));
                    builder.Append(" ms leafSelf=").Append(hotPathLeafSelfMs.ToString("F3")).Append(" ms ");
                    builder.Append("kind=").Append(hotPathLeafScopeKind).Append(" ");
                    builder.Append(hotPath).AppendLine();
                }
            }

            private static void AppendRootScopeKindSummary(StringBuilder builder, IReadOnlyList<ProfilerNodeSnapshot> roots)
            {
                if (roots.Count == 0)
                    return;

                Span<float> elapsedByKind = stackalloc float[4];
                Span<int> countByKind = stackalloc int[4];
                for (int i = 0; i < roots.Count; i++)
                {
                    ProfilerNodeSnapshot root = roots[i];
                    int kindIndex = GetScopeKindSummaryIndex(root.ScopeKind);
                    elapsedByKind[kindIndex] += root.ElapsedMs;
                    countByKind[kindIndex]++;
                }

                builder.AppendLine("RootScopeKinds:");
                AppendRootScopeKindSummaryLine(builder, ProfilerScopeKind.AlwaysOnHotPathLoop, elapsedByKind, countByKind);
                AppendRootScopeKindSummaryLine(builder, ProfilerScopeKind.ConditionalLoop, elapsedByKind, countByKind);
                AppendRootScopeKindSummaryLine(builder, ProfilerScopeKind.OneOffInvoke, elapsedByKind, countByKind);
                AppendRootScopeKindSummaryLine(builder, ProfilerScopeKind.Unspecified, elapsedByKind, countByKind);
            }

            private static void AppendRootScopeKindSummaryLine(
                StringBuilder builder,
                ProfilerScopeKind scopeKind,
                ReadOnlySpan<float> elapsedByKind,
                ReadOnlySpan<int> countByKind)
            {
                int index = GetScopeKindSummaryIndex(scopeKind);
                if (countByKind[index] == 0)
                    return;

                builder.Append("  ").Append(scopeKind)
                    .Append(": roots=").Append(countByKind[index])
                    .Append(" totalMs=").Append(elapsedByKind[index].ToString("F3"))
                    .Append(" policy=").Append(GetScopeLoggingPolicy(scopeKind))
                    .AppendLine();
            }

            private static int GetScopeKindSummaryIndex(ProfilerScopeKind scopeKind)
                => scopeKind switch
                {
                    ProfilerScopeKind.AlwaysOnHotPathLoop => 1,
                    ProfilerScopeKind.ConditionalLoop => 2,
                    ProfilerScopeKind.OneOffInvoke => 3,
                    _ => 0,
                };

            private static float CalculateDownstreamRenderPressureMs(IReadOnlyList<ProfilerNodeSnapshot> nodes)
            {
                float total = 0.0f;
                for (int i = 0; i < nodes.Count; i++)
                    total += CalculateDownstreamRenderPressureMs(nodes[i]);
                return total;
            }

            private static float CalculateDownstreamRenderPressureMs(ProfilerNodeSnapshot node)
            {
                float total = IsDownstreamRenderPressureScope(node.Name) ? node.ElapsedMs : 0.0f;
                IReadOnlyList<ProfilerNodeSnapshot> children = node.Children;
                for (int i = 0; i < children.Count; i++)
                    total += CalculateDownstreamRenderPressureMs(children[i]);
                return total;
            }

            private static bool IsDownstreamRenderPressureScope(string? scopeName)
                => string.Equals(scopeName, CollectVisibleWaitForRenderScopeName, StringComparison.Ordinal);

            private static bool TryGetLikelyBlockingThread(IReadOnlyList<ProfilerThreadSnapshot> threads, int currentThreadId, out ProfilerThreadSnapshot blockingThread)
            {
                blockingThread = null!;
                float bestTotalMs = float.MinValue;

                for (int i = 0; i < threads.Count; i++)
                {
                    ProfilerThreadSnapshot candidate = threads[i];
                    if (candidate.ThreadId == currentThreadId)
                        continue;

                    if (candidate.TotalTimeMs > bestTotalMs)
                    {
                        bestTotalMs = candidate.TotalTimeMs;
                        blockingThread = candidate;
                    }
                }

                return bestTotalMs > float.MinValue;
            }

            private static void AppendTopComponentTimings(StringBuilder builder, IReadOnlyList<ProfilerComponentTimingSnapshot>? components, int maxCount)
            {
                if (components is null || components.Count == 0)
                    return;

                builder.AppendLine("TopComponents:");
                int count = Math.Min(maxCount, components.Count);
                for (int i = 0; i < count; i++)
                {
                    ProfilerComponentTimingSnapshot component = components[i];
                    builder.Append("  ").Append(i + 1).Append(". ")
                        .Append(component.ComponentName).Append(" [")
                        .Append(component.ComponentType).Append("] on ")
                        .Append(component.SceneNodeName).Append(" = ")
                        .Append(component.ElapsedMs.ToString("F3")).Append(" ms over ")
                        .Append(component.CallCount).Append(" calls")
                        .Append(" (TickMask=").Append(component.TickGroupMask).AppendLine(")");
                }
            }

            private static void AppendRenderMatrixStatsSnapshot(StringBuilder builder)
            {
                if (!RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixStatsReady)
                    return;

                builder.AppendLine("RenderMatrixStats:");
                builder.Append("  Applied: ").Append(RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixApplied.ToString("N0")).AppendLine();
                builder.Append("  NonEmptyBatches: ").Append(RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixBatchCount.ToString("N0")).AppendLine();
                builder.Append("  MaxBatchSize: ").Append(RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixMaxBatchSize.ToString("N0")).AppendLine();
                builder.Append("  SetCalls: ").Append(RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixSetCalls.ToString("N0")).AppendLine();
                builder.Append("  ListenerInvocations: ").Append(RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixListenerInvocations.ToString("N0")).AppendLine();
            }

            private static void AppendOctreeStatsSnapshot(StringBuilder builder)
            {
                if (!RuntimeEngine.Rendering.Stats.Octree.OctreeStatsReady)
                    return;

                builder.AppendLine("CpuSpatialTreeStats:");
                builder.Append("  Mode: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMode).AppendLine();
                builder.Append("  CollectMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeCollectMs.ToString("F3")).AppendLine();
                builder.Append("  MaxCollectMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxCollectMs.ToString("F3")).AppendLine();
                builder.Append("  Nodes: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeNodeCount.ToString("N0")).AppendLine();
                builder.Append("  Items: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeItemCount.ToString("N0")).AppendLine();
                builder.Append("  RootItems: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeRootItemCount.ToString("N0")).AppendLine();
                builder.Append("  MaxItemsPerNode: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxNodeItemCount.ToString("N0")).AppendLine();
                builder.Append("  MaxDepth: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxDepth.ToString("N0")).AppendLine();
                builder.Append("  UnboundedItems: ").Append(RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeUnboundedItemCount.ToString("N0")).AppendLine();
                builder.Append("  CollectCalls: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeCollectCallCount.ToString("N0")).AppendLine();
                builder.Append("  VisibleRenderables: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeVisibleRenderableCount.ToString("N0")).AppendLine();
                builder.Append("  EmittedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeEmittedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  MaxVisiblePerCollect: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeMaxVisibleRenderablesPerCollect.ToString("N0")).AppendLine();
                builder.Append("  MaxCommandsPerCollect: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeMaxEmittedCommandsPerCollect.ToString("N0")).AppendLine();
                builder.Append("  Add: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeAddCount.ToString("N0")).AppendLine();
                builder.Append("  Move: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeMoveCount.ToString("N0")).AppendLine();
                builder.Append("  Remove: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRemoveCount.ToString("N0")).AppendLine();
                builder.Append("  SkippedMove: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSkippedMoveCount.ToString("N0")).AppendLine();
                builder.Append("  SwapDrainedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapBufferedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapBufferedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapExecutedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecutedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapDrainMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainMs.ToString("F3")).AppendLine();
                builder.Append("  SwapExecuteMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecuteMs.ToString("F3")).AppendLine();
                builder.Append("  SwapMaxCommandMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandMs.ToString("F3")).AppendLine();
                builder.Append("  SwapMaxCommandKind: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandKind).AppendLine();
                builder.Append("  RaycastProcessedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastProcessedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  RaycastDroppedCommands: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastDroppedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  RaycastTraversalMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastTraversalMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastCallbackMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastCallbackMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxTraversalMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxTraversalMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxCallbackMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCallbackMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxCommandMs: ").Append(RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCommandMs.ToString("F3")).AppendLine();
            }

            private static void AppendSkinnedBoundsStatsSnapshot(StringBuilder builder)
            {
                if (!RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsStatsReady)
                    return;

                int deferredFinished = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCompletedCount + RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredFailedCount;
                double deferredAvgQueueMs = deferredFinished <= 0 ? 0.0 : RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredQueueWaitMs / deferredFinished;
                double deferredAvgCpuJobMs = deferredFinished <= 0 ? 0.0 : RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCpuJobMs / deferredFinished;
                double deferredAvgApplyMs = deferredFinished <= 0 ? 0.0 : RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredApplyMs / deferredFinished;
                double gpuAvgComputeMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount <= 0 ? 0.0 : RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuComputeMs / RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount;
                double gpuAvgApplyMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount <= 0 ? 0.0 : RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuApplyMs / RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount;

                builder.AppendLine("SkinnedBoundsStats:");
                builder.Append("  DeferredScheduled: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredScheduledCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredCompleted: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCompletedCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredFailed: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredFailedCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredInFlight: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredInFlightCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredMaxInFlight: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxInFlightCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredQueueWaitMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredQueueWaitMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgQueueWaitMs: ").Append(deferredAvgQueueMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxQueueWaitMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxQueueWaitMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredCpuJobMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgCpuJobMs: ").Append(deferredAvgCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxCpuJobMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredApplyMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredApplyMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgApplyMs: ").Append(deferredAvgApplyMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxApplyMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuCompleted: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount.ToString("N0")).AppendLine();
                builder.Append("  GpuComputeMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuAvgComputeMs: ").Append(gpuAvgComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuMaxComputeMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuApplyMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuAvgApplyMs: ").Append(gpuAvgApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuMaxApplyMs: ").Append(RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxApplyMs.ToString("F3")).AppendLine();
            }

            private static float GetMedianTailMs(float[] samples, int takeCount, int skipFromEnd)
            {
                int available = samples.Length - skipFromEnd;
                if (available <= 0)
                    return 0f;

                int count = Math.Min(takeCount, available);
                if (count <= 0)
                    return 0f;

                float[] window = new float[count];
                Array.Copy(samples, available - count, window, 0, count);
                Array.Sort(window);

                int middle = count / 2;
                return (count % 2 == 0)
                    ? (window[middle - 1] + window[middle]) * 0.5f
                    : window[middle];
            }

            private static string GetScopeLoggingPolicy(ProfilerScopeKind scopeKind)
                => scopeKind switch
                {
                    ProfilerScopeKind.AlwaysOnHotPathLoop => "aggregate in frame history; diagnose as recurring frame-budget cost",
                    ProfilerScopeKind.ConditionalLoop => "aggregate and rate-limit slow-scope spike logs",
                    ProfilerScopeKind.OneOffInvoke => "aggregate and log slow individual invokes",
                    _ => "aggregate with unspecified recurrence",
                };

            private static string FormatScopeLabel(string name, ProfilerScopeKind scopeKind)
                => scopeKind == ProfilerScopeKind.Unspecified
                    ? name
                    : string.Concat(name, " [", scopeKind.ToString(), "]");

            private static void AppendScopeLabel(StringBuilder builder, string name, ProfilerScopeKind scopeKind)
            {
                builder.Append(name);
                if (scopeKind != ProfilerScopeKind.Unspecified)
                    builder.Append(" [").Append(scopeKind).Append(']');
            }

            /// <summary>
            /// Returns the scope path of the hottest execution branch.
            /// Out parameter <paramref name="pathMs"/> returns the root-inclusive duration of the hottest root.
            /// </summary>
            private static string GetHottestPath(IReadOnlyList<ProfilerNodeSnapshot> roots, out float pathMs)
                => GetHottestPath(roots, out pathMs, out _);

            /// <summary>
            /// Returns the scope path of the hottest execution branch.
            /// Out parameter <paramref name="pathMs"/> returns the root-inclusive duration of the hottest root,
            /// and <paramref name="pathScopeKind"/> returns the leaf node's scope kind.
            /// </summary>
            private static string GetHottestPath(IReadOnlyList<ProfilerNodeSnapshot> roots, out float pathMs, out ProfilerScopeKind pathScopeKind)
                => GetHottestPath(roots, out pathMs, out _, out _, out _, out pathScopeKind);

            /// <summary>
            /// Traverses the hottest execution branch from roots to deepest hot child, cleanly separating
            /// the root-inclusive duration from the selected leaf node's inclusive duration and synchronous self-time.
            /// </summary>
            private static string GetHottestPath(
                IReadOnlyList<ProfilerNodeSnapshot> roots,
                out float rootInclusiveMs,
                out ProfilerScopeKind rootScopeKind,
                out float leafInclusiveMs,
                out float leafSelfMs,
                out ProfilerScopeKind leafScopeKind)
            {
                rootInclusiveMs = 0f;
                rootScopeKind = ProfilerScopeKind.Unspecified;
                leafInclusiveMs = 0f;
                leafSelfMs = 0f;
                leafScopeKind = ProfilerScopeKind.Unspecified;
                if (roots.Count == 0)
                    return "(no samples)";

                ProfilerNodeSnapshot hottest = roots[0];
                for (int i = 1; i < roots.Count; i++)
                {
                    if (roots[i].ElapsedMs > hottest.ElapsedMs)
                        hottest = roots[i];
                }

                rootInclusiveMs = hottest.ElapsedMs;
                rootScopeKind = hottest.ScopeKind;
                leafInclusiveMs = hottest.ElapsedMs;
                leafSelfMs = hottest.SelfMs;
                leafScopeKind = hottest.ScopeKind;

                var parts = new List<string>(8) { FormatScopeLabel(hottest.Name, hottest.ScopeKind) };
                ProfilerNodeSnapshot current = hottest;
                while (current.Children.Count > 0)
                {
                    ProfilerNodeSnapshot best = current.Children[0];
                    for (int i = 1; i < current.Children.Count; i++)
                    {
                        if (current.Children[i].ElapsedMs > best.ElapsedMs)
                            best = current.Children[i];
                    }

                    parts.Add(FormatScopeLabel(best.Name, best.ScopeKind));
                    current = best;
                    leafInclusiveMs = best.ElapsedMs;
                    leafSelfMs = best.SelfMs;
                    leafScopeKind = best.ScopeKind;
                }

                return string.Join(" > ", parts);
            }

            private static ProfilerNodeSnapshot BuildSnapshotFromBuilt(BuiltTimer timer)
            {
                var children = timer.Children;
                children.Sort(static (left, right) => left.StartTicks.CompareTo(right.StartTicks));
                int childCount = children.Count;
                ProfilerNodeSnapshot[] childSnapshots = childCount > 0 ? new ProfilerNodeSnapshot[childCount] : [];
                long childTicksSum = 0L;

                for (int i = 0; i < childCount; ++i)
                {
                    childSnapshots[i] = BuildSnapshotFromBuilt(children[i]);
                    if (!children[i].IsLinked && children[i].ProducerThreadId == timer.ProducerThreadId)
                        childTicksSum += children[i].ElapsedTicks;
                }

                float elapsedMs = TicksToMilliseconds(timer.ElapsedTicks);
                float selfMs = TicksToMilliseconds(Math.Max(0L, timer.ElapsedTicks - childTicksSum));
                return new ProfilerNodeSnapshot(
                    timer.ScopeId,
                    timer.ParentScopeId,
                    timer.SessionEpoch,
                    timer.LogicalThreadId,
                    timer.ProducerThreadId,
                    timer.StartTicks,
                    timer.StartTicks + timer.ElapsedTicks,
                    timer.IsLinked,
                    timer.Name,
                    elapsedMs,
                    selfMs,
                    timer.ScopeKind,
                    childSnapshots);
            }

            private static void RemoveCompletedScopeMappings(ThreadBuildState state, BuiltTimer timer)
            {
                state.CompletedByScopeId.Remove(timer.ScopeId);
                for (int i = 0; i < timer.Children.Count; i++)
                    RemoveCompletedScopeMappings(state, timer.Children[i]);
            }

            private bool HasUnresolvedLinkedDescendant(BuiltTimer timer)
            {
                if (_unresolvedLinkedChildrenByParentScopeId.ContainsKey(timer.ScopeId))
                    return true;

                for (int i = 0; i < timer.Children.Count; i++)
                {
                    if (HasUnresolvedLinkedDescendant(timer.Children[i]))
                        return true;
                }

                return false;
            }

            private static float TicksToMilliseconds(long ticks)
                => (float)(ticks * 1000.0 / EngineTimer.StopwatchTickFrequency);

            public void ClearFrameLog()
            {
            }

            /// <summary>
            /// Gets the snapshot assembled from completed root scopes during the most recent
            /// snapshot epoch. A completed descendant whose parent is still active is intentionally
            /// not published until its parent completes, so <see langword="false"/> does not imply
            /// that no profiling scopes are currently active.
            /// </summary>
            public bool TryGetSnapshot(out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
            {
                frameSnapshot = _readySnapshot;
                history = _readyHistorySnapshot;
                return frameSnapshot is not null;
            }

            public bool TryRequestSnapshot(float minIntervalSeconds, out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
                => TryGetSnapshot(out frameSnapshot, out history);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool HasActiveComponentTimingFrame()
                => _activeComponentTimingFrame is not null;

            public void BeginComponentTimingFrame(float frameTime)
            {
                if (!EnableComponentTiming)
                    return;

                _activeComponentTimingFrame = new ComponentTimingFrameState(frameTime);
            }

            public void EndComponentTimingFrame(float frameTime)
            {
                var state = Interlocked.Exchange(ref _activeComponentTimingFrame, null);
                if (state is null)
                    return;

                _readyComponentTimingSnapshot = BuildComponentTimingSnapshot(state, frameTime);
            }

            internal void RecordComponentTick(XRComponent component, ETickGroup group, long elapsedTicks)
            {
                var state = _activeComponentTimingFrame;
                if (state is null)
                    return;

                var accumulator = state.Components.GetOrAdd(component.ID, static (_, c) => new ComponentTimingAccumulator(c), component);
                accumulator.Add(elapsedTicks, group);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start(DelTimerCallback? callback, [CallerMemberName] string? methodName = null)
                => Start(methodName, ProfilerScopeKind.Unspecified);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start(DelTimerCallback? callback, ProfilerScopeKind scopeKind, [CallerMemberName] string? methodName = null)
                => Start(methodName, scopeKind);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start([CallerMemberName] string? methodName = null, ProfilerScopeKind scopeKind = ProfilerScopeKind.Unspecified)
            {
                if (!_enableFrameLogging)
                    return default;

                if (_tlsLinkedProducerState is { } linkedState)
                {
                    long sessionEpoch = SessionEpoch;
                    if (linkedState.SessionEpoch != sessionEpoch)
                        return default;

                    int linkedDepth = linkedState.Depth + 1;
                    linkedState.Depth = linkedDepth;
                    long linkedParentScopeId = linkedState.CurrentScopeId;
                    long linkedScopeId = Interlocked.Increment(ref _nextScopeId);
                    linkedState.CurrentScopeId = linkedScopeId;
                    Interlocked.Increment(ref _activeScopeCount);
                    long linkedStartTicks = Stopwatch.GetTimestamp();
                    return new ProfilerScope(
                        this,
                        linkedState,
                        null,
                        false,
                        linkedStartTicks,
                        linkedScopeId,
                        linkedParentScopeId,
                        sessionEpoch,
                        Environment.CurrentManagedThreadId,
                        linkedDepth,
                        methodName,
                        scopeKind);
                }

                var state = GetOrCreateThreadProducerState();
                long currentSessionEpoch = SessionEpoch;
                state.PrepareForSession(currentSessionEpoch);
                int depth = state.Depth + 1;
                state.Depth = depth;
                long parentScopeId = state.CurrentScopeId;
                long scopeId = Interlocked.Increment(ref _nextScopeId);
                state.CurrentScopeId = scopeId;
                Interlocked.Increment(ref _activeScopeCount);
                long startTicks = Stopwatch.GetTimestamp();
                if (state.ThreadId == RuntimeEngine.RenderThreadId)
                    RecordRenderThreadScopeEntry(depth, startTicks, methodName, scopeKind);
                return new ProfilerScope(this, state, startTicks, scopeId, parentScopeId, currentSessionEpoch, Environment.CurrentManagedThreadId, depth, methodName, scopeKind);
            }

            public LinkedScopeContext CaptureLinkedChildContext()
            {
                if (!_enableFrameLogging)
                    return default;

                if (_tlsLinkedProducerState is { } linkedState)
                    return new LinkedScopeContext(linkedState.ThreadId, linkedState.Depth, linkedState.CurrentScopeId, linkedState.SessionEpoch);

                var state = GetOrCreateThreadProducerState();
                long sessionEpoch = SessionEpoch;
                state.PrepareForSession(sessionEpoch);
                return new LinkedScopeContext(state.ThreadId, state.Depth, state.CurrentScopeId, sessionEpoch);
            }

            public ProfilerScope StartLinkedChild(
                LinkedScopeContext context,
                string? methodName,
                ProfilerScopeKind scopeKind = ProfilerScopeKind.OneOffInvoke)
            {
                long sessionEpoch = SessionEpoch;
                if (!_enableFrameLogging || !context.IsValid || context.SessionEpoch != sessionEpoch)
                {
                    if (context.IsValid && context.SessionEpoch != sessionEpoch)
                        Interlocked.Increment(ref _staleCompletedDiscardedEventCount);
                    return default;
                }

                int childDepth = context.ParentDepth + 1;
                var previousLinkedState = _tlsLinkedProducerState;
                long scopeId = Interlocked.Increment(ref _nextScopeId);
                var linkedState = new LinkedThreadProducerState(context.ThreadId, childDepth, scopeId, sessionEpoch);
                _tlsLinkedProducerState = linkedState;
                Interlocked.Increment(ref _activeScopeCount);
                if (context.ParentScopeId != 0L)
                {
                    _unresolvedLinkedChildrenByParentScopeId.AddOrUpdate(context.ParentScopeId, 1, static (_, count) => count + 1);
                    Interlocked.Increment(ref _unresolvedLinkedChildCount);
                }

                long startTicks = Stopwatch.GetTimestamp();
                return new ProfilerScope(
                    this,
                    linkedState,
                    previousLinkedState,
                    true,
                    startTicks,
                    scopeId,
                    context.ParentScopeId,
                    sessionEpoch,
                    Environment.CurrentManagedThreadId,
                    childDepth,
                    methodName,
                    scopeKind);
            }

            private void RecordRenderThreadScopeEntry(int depth, long startTicks, string? methodName, ProfilerScopeKind scopeKind)
            {
                if (depth <= 0)
                    return;

                int clampedDepth = Math.Min(depth, RenderThreadScopeStackCapacity);
                int index = clampedDepth - 1;
                _renderThreadScopeNames[index] = string.IsNullOrWhiteSpace(methodName) ? "<unnamed>" : methodName;
                _renderThreadScopeKinds[index] = scopeKind;
                _renderThreadScopeStartTicks[index] = startTicks;
                Volatile.Write(ref _renderThreadScopeDepth, clampedDepth);
            }

            private void RecordRenderThreadScopeExit(int depth, long endTicks, string? methodName, ProfilerScopeKind scopeKind)
            {
                Volatile.Write(ref _renderThreadScopeDepth, Math.Max(0, Math.Min(depth, RenderThreadScopeStackCapacity)));

                if (string.Equals(methodName, RenderDispatchScopeName, StringComparison.Ordinal))
                    Volatile.Write(ref _lastCompletedRenderFrameTicks, endTicks);
            }

            public Guid StartAsync(
                DelTimerCallback? callback = null,
                [CallerMemberName] string? methodName = null,
                ProfilerScopeKind scopeKind = ProfilerScopeKind.Unspecified)
            {
                Guid id = Guid.NewGuid();
                if (!EnableFrameLogging)
                    return id;

                var state = GetOrCreateThreadProducerState();
                long sessionEpoch = SessionEpoch;
                state.PrepareForSession(sessionEpoch);
                long scopeId = Interlocked.Increment(ref _nextScopeId);
                _pendingAsyncTimers[id] = new AsyncPendingTimer(scopeId, sessionEpoch, Stopwatch.GetTimestamp(), state.ThreadId, Environment.CurrentManagedThreadId, methodName, scopeKind);
                Interlocked.Increment(ref _activeScopeCount);
                return id;
            }

            public float StopAsync(Guid id, out string? methodName)
            {
                methodName = string.Empty;
                if (!_pendingAsyncTimers.TryRemove(id, out var pending))
                    return 0.0f;

                methodName = pending.MethodName;
                if (!EnableFrameLogging || pending.SessionEpoch != SessionEpoch)
                {
                    if (pending.SessionEpoch != SessionEpoch)
                        Interlocked.Increment(ref _staleCompletedDiscardedEventCount);
                    return 0.0f;
                }

                long endTicks = Stopwatch.GetTimestamp();
                long elapsedTicks = Math.Max(0L, endTicks - pending.StartTicks);
                _overflowCompletedEvents.Enqueue(new CompletedScopeEvent(
                    pending.ScopeId,
                    ParentScopeId: 0L,
                    pending.SessionEpoch,
                    pending.LogicalThreadId,
                    pending.ProducerThreadId,
                    Depth: 1,
                    pending.StartTicks,
                    elapsedTicks,
                    pending.MethodName,
                    pending.ScopeKind,
                    IsAsyncRoot: true,
                    IsLinked: false));
                Interlocked.Increment(ref _queuedCompletedScopeCount);
                Interlocked.Decrement(ref _activeScopeCount);

                return TicksToMilliseconds(elapsedTicks);
            }

            public float StopAsync(Guid id)
                => StopAsync(id, out _);

            public ProfilerFrameSnapshot? GetLastFrameSnapshot()
                => _readySnapshot;

            public ProfilerComponentFrameSnapshot? GetLastComponentTimingSnapshot()
                => _readyComponentTimingSnapshot;

            public Dictionary<int, float[]> GetThreadHistorySnapshot()
                => _readyHistorySnapshot;

            private static ProfilerComponentFrameSnapshot BuildComponentTimingSnapshot(ComponentTimingFrameState state, float frameTime)
            {
                if (state.Components.IsEmpty)
                    return new ProfilerComponentFrameSnapshot(frameTime, []);

                var snapshots = new List<ProfilerComponentTimingSnapshot>(state.Components.Count);
                foreach (var entry in state.Components)
                {
                    var component = entry.Value.Component;
                    long elapsedTicks = entry.Value.ElapsedTicks;
                    int callCount = entry.Value.CallCount;
                    if (elapsedTicks <= 0 || callCount <= 0)
                        continue;

                    string componentType = component.GetType().Name;
                    string componentName = string.IsNullOrWhiteSpace(component.Name) ? componentType : component.Name!;
                    string sceneNodeName = string.IsNullOrWhiteSpace(component.SceneNode?.Name) ? "(unnamed node)" : component.SceneNode.Name!;

                    snapshots.Add(new ProfilerComponentTimingSnapshot(
                        component.ID,
                        componentName,
                        componentType,
                        sceneNodeName,
                        TicksToMilliseconds(elapsedTicks),
                        callCount,
                        entry.Value.TickGroupMask));
                }

                snapshots.Sort(static (left, right) => right.ElapsedMs.CompareTo(left.ElapsedMs));
                return new ProfilerComponentFrameSnapshot(frameTime, snapshots.ToArray());
            }

            public sealed class ProfilerFrameSnapshot(
                float frameTime,
                long sessionEpoch,
                long publicationId,
                long capturedAtTicks,
                ulong updateFrameId,
                ulong renderFrameId,
                int activeScopeCount,
                int queuedCompletedScopeCount,
                int pendingCompletedScopeCount,
                int unresolvedLinkedChildCount,
                long staleCompletedScopeCount,
                IReadOnlyList<ProfilerThreadSnapshot> threads,
                ProfilerComponentFrameSnapshot? componentTimings)
            {
                public float FrameTime { get; } = frameTime;
                public long SessionEpoch { get; } = sessionEpoch;
                public long PublicationId { get; } = publicationId;
                public long CapturedAtTicks { get; } = capturedAtTicks;
                public ulong UpdateFrameId { get; } = updateFrameId;
                public ulong RenderFrameId { get; } = renderFrameId;
                public int ActiveScopeCount { get; } = activeScopeCount;
                public int QueuedCompletedScopeCount { get; } = queuedCompletedScopeCount;
                public int PendingCompletedScopeCount { get; } = pendingCompletedScopeCount;
                public int UnresolvedLinkedChildCount { get; } = unresolvedLinkedChildCount;
                public long StaleCompletedScopeCount { get; } = staleCompletedScopeCount;
                public bool ContainsIncompleteScopes => ActiveScopeCount > 0 || QueuedCompletedScopeCount > 0 || PendingCompletedScopeCount > 0 || UnresolvedLinkedChildCount > 0;
                public IReadOnlyList<ProfilerThreadSnapshot> Threads { get; } = threads;
                public ProfilerComponentFrameSnapshot? ComponentTimings { get; } = componentTimings;
            }

            public sealed class ProfilerThreadSnapshot
            {
                public int ThreadId { get; }
                public IReadOnlyList<ProfilerNodeSnapshot> RootNodes { get; }
                public float TotalTimeMs { get; }
                public float WallTimeMs { get; }
                public float DownstreamRenderPressureMs { get; }

                public ProfilerThreadSnapshot(int threadId, IReadOnlyList<ProfilerNodeSnapshot> rootNodes)
                {
                    ThreadId = threadId;
                    RootNodes = rootNodes;
                    float wallTotal = 0f;
                    for (int i = 0; i < rootNodes.Count; i++)
                        wallTotal += rootNodes[i].ElapsedMs;

                    WallTimeMs = wallTotal;
                    DownstreamRenderPressureMs = CalculateDownstreamRenderPressureMs(rootNodes);
                    TotalTimeMs = Math.Max(0.0f, wallTotal - DownstreamRenderPressureMs);
                }
            }

            public sealed class ProfilerNodeSnapshot
            {
                public long ScopeId { get; }
                public long ParentScopeId { get; }
                public long SessionEpoch { get; }
                public int LogicalThreadId { get; }
                public int ProducerThreadId { get; }
                public long StartTicks { get; }
                public long EndTicks { get; }
                public bool IsComplete => true;
                public bool IsLinked { get; }
                public string Name { get; }
                /// <summary>Inclusive wall-clock duration of this scope in milliseconds.</summary>
                public float ElapsedMs { get; }
                /// <summary>Synchronous wall-clock self-time in milliseconds (elapsed minus sum of direct children).</summary>
                public float SelfMs { get; }
                public ProfilerScopeKind ScopeKind { get; }
                public IReadOnlyList<ProfilerNodeSnapshot> Children { get; }

                public ProfilerNodeSnapshot(
                    long scopeId,
                    long parentScopeId,
                    long sessionEpoch,
                    int logicalThreadId,
                    int producerThreadId,
                    long startTicks,
                    long endTicks,
                    bool isLinked,
                    string name,
                    float elapsedMs,
                    float selfMs,
                    ProfilerScopeKind scopeKind,
                    IReadOnlyList<ProfilerNodeSnapshot> children)
                {
                    ScopeId = scopeId;
                    ParentScopeId = parentScopeId;
                    SessionEpoch = sessionEpoch;
                    LogicalThreadId = logicalThreadId;
                    ProducerThreadId = producerThreadId;
                    StartTicks = startTicks;
                    EndTicks = endTicks;
                    IsLinked = isLinked;
                    Name = name;
                    ElapsedMs = elapsedMs;
                    SelfMs = selfMs;
                    ScopeKind = scopeKind;
                    Children = children;
                }

                public ProfilerNodeSnapshot(string name, float elapsedMs, ProfilerScopeKind scopeKind, IReadOnlyList<ProfilerNodeSnapshot> children)
                    : this(0L, 0L, 0L, 0, 0, 0L, 0L, false, name, elapsedMs, CalculateSelfMs(elapsedMs, children), scopeKind, children)
                {
                }

                private static float CalculateSelfMs(float elapsedMs, IReadOnlyList<ProfilerNodeSnapshot> children)
                {
                    if (children == null || children.Count == 0)
                        return Math.Max(0.0f, elapsedMs);

                    float childSum = 0f;
                    for (int i = 0; i < children.Count; i++)
                        childSum += children[i].ElapsedMs;

                    return Math.Max(0.0f, elapsedMs - childSum);
                }
            }

            public sealed class ProfilerComponentFrameSnapshot(float frameTime, IReadOnlyList<ProfilerComponentTimingSnapshot> components)
            {
                public float FrameTime { get; } = frameTime;
                public IReadOnlyList<ProfilerComponentTimingSnapshot> Components { get; } = components;
            }

            public sealed class ProfilerComponentTimingSnapshot(
                Guid componentId,
                string componentName,
                string componentType,
                string sceneNodeName,
                float elapsedMs,
                int callCount,
                int tickGroupMask)
            {
                public Guid ComponentId { get; } = componentId;
                public string ComponentName { get; } = componentName;
                public string ComponentType { get; } = componentType;
                public string SceneNodeName { get; } = sceneNodeName;
                public float ElapsedMs { get; } = elapsedMs;
                public int CallCount { get; } = callCount;
                public int TickGroupMask { get; } = tickGroupMask;
            }
        }
#else //Stub implementation when running a published build without the profiler to avoid stripping out the code paths that call it
        public class CodeProfiler : XRBase
        {
            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            public struct ProfilerScope : IDisposable
            {
                public void Dispose()
                {
                }
            }

            public readonly struct LinkedScopeContext
            {
            }

            public sealed class CodeProfilerTimer(string? name = null) : IPoolable
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

            public sealed class ProfilerFrameSnapshot(
                float frameTime,
                long sessionEpoch,
                long publicationId,
                long capturedAtTicks,
                ulong updateFrameId,
                ulong renderFrameId,
                int activeScopeCount,
                int queuedCompletedScopeCount,
                int pendingCompletedScopeCount,
                int unresolvedLinkedChildCount,
                long staleCompletedScopeCount,
                IReadOnlyList<ProfilerThreadSnapshot> threads,
                ProfilerComponentFrameSnapshot? componentTimings)
            {
                public float FrameTime { get; } = frameTime;
                public long SessionEpoch { get; } = sessionEpoch;
                public long PublicationId { get; } = publicationId;
                public long CapturedAtTicks { get; } = capturedAtTicks;
                public ulong UpdateFrameId { get; } = updateFrameId;
                public ulong RenderFrameId { get; } = renderFrameId;
                public int ActiveScopeCount { get; } = activeScopeCount;
                public int QueuedCompletedScopeCount { get; } = queuedCompletedScopeCount;
                public int PendingCompletedScopeCount { get; } = pendingCompletedScopeCount;
                public int UnresolvedLinkedChildCount { get; } = unresolvedLinkedChildCount;
                public long StaleCompletedScopeCount { get; } = staleCompletedScopeCount;
                public bool ContainsIncompleteScopes => ActiveScopeCount > 0 || QueuedCompletedScopeCount > 0 || PendingCompletedScopeCount > 0 || UnresolvedLinkedChildCount > 0;
                public IReadOnlyList<ProfilerThreadSnapshot> Threads { get; } = threads;
                public ProfilerComponentFrameSnapshot? ComponentTimings { get; } = componentTimings;
            }

            public sealed class ProfilerThreadSnapshot(int threadId, IReadOnlyList<ProfilerNodeSnapshot> rootNodes)
            {
                public int ThreadId { get; } = threadId;
                public IReadOnlyList<ProfilerNodeSnapshot> RootNodes { get; } = rootNodes;
                public float TotalTimeMs { get; } = 0f;
                public float WallTimeMs { get; } = 0f;
                public float DownstreamRenderPressureMs { get; } = 0f;
            }

            public sealed class ProfilerNodeSnapshot
            {
                public long ScopeId { get; }
                public long ParentScopeId { get; }
                public long SessionEpoch { get; }
                public int LogicalThreadId { get; }
                public int ProducerThreadId { get; }
                public long StartTicks { get; }
                public long EndTicks { get; }
                public bool IsComplete => true;
                public bool IsLinked => false;
                public string Name { get; }
                public float ElapsedMs { get; }
                public float SelfMs { get; }
                public ProfilerScopeKind ScopeKind { get; }
                public IReadOnlyList<ProfilerNodeSnapshot> Children { get; }

                public ProfilerNodeSnapshot(string name, float elapsedMs, float selfMs, ProfilerScopeKind scopeKind, IReadOnlyList<ProfilerNodeSnapshot> children)
                {
                    ScopeId = 0L;
                    ParentScopeId = 0L;
                    SessionEpoch = 0L;
                    LogicalThreadId = 0;
                    ProducerThreadId = 0;
                    StartTicks = 0L;
                    EndTicks = 0L;
                    Name = name;
                    ElapsedMs = elapsedMs;
                    SelfMs = selfMs;
                    ScopeKind = scopeKind;
                    Children = children;
                }

                public ProfilerNodeSnapshot(string name, float elapsedMs, ProfilerScopeKind scopeKind, IReadOnlyList<ProfilerNodeSnapshot> children)
                    : this(name, elapsedMs, elapsedMs, scopeKind, children)
                {
                }
            }

            public sealed class ProfilerComponentFrameSnapshot(float frameTime, IReadOnlyList<ProfilerComponentTimingSnapshot> components)
            {
                public float FrameTime { get; } = frameTime;
                public IReadOnlyList<ProfilerComponentTimingSnapshot> Components { get; } = components;
            }

            public sealed class ProfilerComponentTimingSnapshot(
                Guid componentId,
                string componentName,
                string componentType,
                string sceneNodeName,
                float elapsedMs,
                int callCount,
                int tickGroupMask)
            {
                public Guid ComponentId { get; } = componentId;
                public string ComponentName { get; } = componentName;
                public string ComponentType { get; } = componentType;
                public string SceneNodeName { get; } = sceneNodeName;
                public float ElapsedMs { get; } = elapsedMs;
                public int CallCount { get; } = callCount;
                public int TickGroupMask { get; } = tickGroupMask;
            }

            public bool EnableFrameLogging
            {
                get => false;
                set { }
            }

            public bool EnableComponentTiming
            {
                get => false;
                set { }
            }

            public float DebugOutputMinElapsedMs
            {
                get => 0f;
                set { }
            }

            public int StatsThreadIntervalMs
            {
                get => 0;
                set { }
            }

            public int SnapshotIntervalMs
            {
                get => 0;
                set { }
            }

            public int ThreadHistoryCapacity
            {
                get => 0;
                set { }
            }

            public int MaxOverflowPerCycle
            {
                get => 0;
                set { }
            }

            public int MaxOverflowQueueSize
            {
                get => 0;
                set { }
            }

            public long OverflowDiscardedEventCount => 0;
            public long PendingCompletedDiscardedEventCount => 0;
            public long StaleCompletedDiscardedEventCount => 0;
            public int PendingCompletedCount => 0;
            public int ActiveScopeCount => 0;
            public int QueuedCompletedScopeCount => 0;
            public int UnresolvedLinkedChildCount => 0;
            public long SessionEpoch => 0;

            public int ProducerBufferCapacity
            {
                get => 0;
                set { }
            }

            public int FpsDropBaselineWindowSamples
            {
                get => 0;
                set { }
            }

            public float FpsDropMinPreviousFps
            {
                get => 0f;
                set { }
            }

            public float FpsDropMinDeltaMs
            {
                get => 0f;
                set { }
            }

            public float RenderStallThresholdMs
            {
                get => 0f;
                set { }
            }

            public ConcurrentDictionary<int, CodeProfilerTimer> RootEntriesPerThread { get; } = [];

            public void ClearFrameLog()
            {
            }

            public bool TryGetSnapshot(out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
            {
                frameSnapshot = null;
                history = [];
                return false;
            }

            public bool TryRequestSnapshot(float minIntervalSeconds, out ProfilerFrameSnapshot? frameSnapshot, out Dictionary<int, float[]> history)
            {
                frameSnapshot = null;
                history = [];
                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool HasActiveComponentTimingFrame()
                => false;

            public void BeginComponentTimingFrame(float frameTime)
            {
            }

            public void EndComponentTimingFrame(float frameTime)
            {
            }

            internal void RecordComponentTick(XRComponent component, ETickGroup group, long elapsedTicks)
            {
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start(DelTimerCallback? callback, [CallerMemberName] string? methodName = null)
                => default;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start(DelTimerCallback? callback, ProfilerScopeKind scopeKind, [CallerMemberName] string? methodName = null)
                => default;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start([CallerMemberName] string? methodName = null, ProfilerScopeKind scopeKind = ProfilerScopeKind.Unspecified)
                => default;

            public LinkedScopeContext CaptureLinkedChildContext()
                => default;

            public ProfilerScope StartLinkedChild(
                LinkedScopeContext context,
                string? methodName,
                ProfilerScopeKind scopeKind = ProfilerScopeKind.OneOffInvoke)
                => default;

            public Guid StartAsync(
                DelTimerCallback? callback = null,
                [CallerMemberName] string? methodName = null,
                ProfilerScopeKind scopeKind = ProfilerScopeKind.Unspecified)
                => Guid.Empty;

            public float StopAsync(Guid id, out string? methodName)
            {
                methodName = string.Empty;
                return 0.0f;
            }

            public float StopAsync(Guid id)
                => 0.0f;

            public ProfilerFrameSnapshot? GetLastFrameSnapshot()
                => null;

            public ProfilerComponentFrameSnapshot? GetLastComponentTimingSnapshot()
                => null;

            public Dictionary<int, float[]> GetThreadHistorySnapshot()
                => [];
        }
#endif
    }
}
