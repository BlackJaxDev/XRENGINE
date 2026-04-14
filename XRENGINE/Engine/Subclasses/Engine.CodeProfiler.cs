using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
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
            private bool _enableComponentTiming = true;
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
                        StopStatsThread(waitForExit: true);
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

            private const int ProducerBufferHardMaxCapacity = 1 << 20;
            private const int ProducerBufferAutoGrowthFactor = 8;

            private long SnapshotIntervalTicks => EngineTimer.SecondsToStopwatchTicks(SnapshotIntervalMs / 1000.0);

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
            private readonly ConcurrentDictionary<int, AsyncPendingTimer> _pendingAsyncTimers = [];

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
            private long _lastSnapshotTicks = -1L;
            private float _lastFpsDropProcessedFrameTime = float.NegativeInfinity;

            [ThreadStatic]
            private static ThreadProducerState? _tlsProducerState;

            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            internal readonly record struct CompletedScopeEvent(
                int ThreadId,
                int Depth,
                long StartTicks,
                long ElapsedTicks,
                string? MethodName,
                bool IsAsyncRoot);

            internal sealed class ThreadProducerState(int threadId, ThreadProducerBuffer buffer)
            {
                public int ThreadId { get; } = threadId;
                public ThreadProducerBuffer Buffer { get; } = buffer;
                public int Depth;
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

            private sealed class AsyncPendingTimer(long startTicks, int threadId, string? methodName)
            {
                public long StartTicks { get; } = startTicks;
                public int ThreadId { get; } = threadId;
                public string? MethodName { get; } = methodName;
            }

            public readonly struct ProfilerScope : IDisposable
            {
                private readonly CodeProfiler? _profiler;
                private readonly ThreadProducerState? _state;
                private readonly string? _methodName;
                private readonly long _startTicks;
                private readonly int _depth;

                internal ProfilerScope(CodeProfiler profiler, ThreadProducerState state, long startTicks, int depth, string? methodName)
                {
                    _profiler = profiler;
                    _state = state;
                    _methodName = methodName;
                    _startTicks = startTicks;
                    _depth = depth;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Dispose()
                {
                    if (_profiler is null || _state is null)
                        return;

                    int depth = _state.Depth;
                    _state.Depth = depth > 0 ? depth - 1 : 0;
                    if (!_profiler._enableFrameLogging)
                        return;

                    long endTicks = Time.Timer.TimeTicks();
                    long elapsedTicks = endTicks - _startTicks;
                    if (elapsedTicks < 0L)
                        elapsedTicks = 0L;

                    var completedEvent = new CompletedScopeEvent(
                        _state.ThreadId,
                        _depth,
                        _startTicks,
                        elapsedTicks,
                        _methodName,
                        IsAsyncRoot: false);

                    if (!_state.Buffer.TryWrite(completedEvent))
                        _profiler._overflowCompletedEvents.Enqueue(completedEvent);
                }
            }

            private sealed class BuiltTimer
            {
                public string Name = string.Empty;
                public long ElapsedTicks;
                public int Depth;
                public List<BuiltTimer> Children = new(4);

                public void Reset()
                {
                    Name = string.Empty;
                    ElapsedTicks = 0L;
                    Depth = 0;
                    Children.Clear();
                }
            }

            private sealed class ThreadBuildState
            {
                public Stack<BuiltTimer> PendingCompleted = new(32);
                public Queue<BuiltTimer> BuiltTimerPool = new(64);
                public List<BuiltTimer> ChildScratch = new(16);

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

                while (_overflowCompletedEvents.TryDequeue(out _)) { }
                _pendingAsyncTimers.Clear();

                // Clear stale tree-build state so a restart doesn't process orphaned data
                foreach (var state in _threadBuildStates.Values)
                {
                    state.PendingCompleted.Clear();
                    state.ChildScratch.Clear();
                }
                foreach (var roots in _accumulatedRoots.Values)
                    roots.Clear();
                _threadFrameHistory.Clear();
                _readySnapshot = null;
                _readyHistorySnapshot = [];
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

                        long nowTicks = Time.Timer.TimeTicks();
                        if (_lastSnapshotTicks < 0L || nowTicks - _lastSnapshotTicks >= SnapshotIntervalTicks)
                        {
                            BuildFrameSnapshot(nowTicks);
                            _lastSnapshotTicks = nowTicks;
                        }

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
                    while (_overflowCompletedEvents.TryDequeue(out _))
                        discarded++;

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
                if (!_threadBuildStates.TryGetValue(completedEvent.ThreadId, out var state))
                {
                    state = new ThreadBuildState();
                    _threadBuildStates[completedEvent.ThreadId] = state;
                }

                var built = state.RentBuilt();
                built.Name = completedEvent.MethodName ?? string.Empty;
                built.ElapsedTicks = completedEvent.ElapsedTicks;
                built.Depth = completedEvent.Depth;

                while (state.PendingCompleted.Count > 0 && state.PendingCompleted.Peek().Depth > completedEvent.Depth)
                    state.ChildScratch.Add(state.PendingCompleted.Pop());

                for (int i = state.ChildScratch.Count - 1; i >= 0; i--)
                    built.Children.Add(state.ChildScratch[i]);
                state.ChildScratch.Clear();

                if (completedEvent.IsAsyncRoot || completedEvent.Depth <= 1)
                {
                    if (!_accumulatedRoots.TryGetValue(completedEvent.ThreadId, out var roots))
                    {
                        roots = new List<BuiltTimer>(32);
                        _accumulatedRoots[completedEvent.ThreadId] = roots;
                    }

                    roots.Add(built);
                }
                else
                {
                    state.PendingCompleted.Push(built);
                }
            }

            private void BuildFrameSnapshot(long frameTicks)
            {
                // Flush orphaned PendingCompleted entries as roots to prevent unbounded accumulation
                // from mismatched scope depths (e.g., a Start() without a corresponding Dispose())
                foreach (var kvp in _threadBuildStates)
                {
                    var buildState = kvp.Value;
                    if (buildState.PendingCompleted.Count == 0)
                        continue;

                    if (!_accumulatedRoots.TryGetValue(kvp.Key, out var orphanRoots))
                    {
                        orphanRoots = new List<BuiltTimer>(32);
                        _accumulatedRoots[kvp.Key] = orphanRoots;
                    }

                    while (buildState.PendingCompleted.Count > 0)
                        orphanRoots.Add(buildState.PendingCompleted.Pop());
                }

                _threadSnapshotsBuffer.Clear();

                foreach (var kvp in _accumulatedRoots)
                {
                    int threadId = kvp.Key;
                    var roots = kvp.Value;
                    if (roots.Count == 0)
                        continue;

                    var rootSnapshots = new ProfilerNodeSnapshot[roots.Count];
                    for (int i = 0; i < roots.Count; i++)
                    {
                        rootSnapshots[i] = BuildSnapshotFromBuilt(roots[i]);
                        if (_threadBuildStates.TryGetValue(threadId, out var state))
                            state.ReturnBuiltRecursive(roots[i]);
                    }

                    roots.Clear();
                    _threadSnapshotsBuffer.Add(new ProfilerThreadSnapshot(threadId, rootSnapshots));
                }

                ProfilerFrameSnapshot? frameSnapshot = null;
                if (_threadSnapshotsBuffer.Count > 0)
                {
                    float frameTime = EngineTimer.TicksToSeconds(frameTicks);
                    frameSnapshot = new ProfilerFrameSnapshot(frameTime, _threadSnapshotsBuffer.ToArray(), _readyComponentTimingSnapshot);
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
                    LogFpsDrops(frameSnapshot, historySnapshot);

                _readyHistorySnapshot = historySnapshot;
                _readySnapshot = frameSnapshot;
            }

            private void LogFpsDrops(ProfilerFrameSnapshot frame, Dictionary<int, float[]> history)
            {
                if (!float.IsFinite(frame.FrameTime) || frame.FrameTime == _lastFpsDropProcessedFrameTime)
                    return;

                _lastFpsDropProcessedFrameTime = frame.FrameTime;

                if (frame.Threads.Count == 0)
                    return;

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
                    string hotPath = GetHottestPath(thread.RootNodes, out float hotPathMs);

                    var builder = new StringBuilder(1024);
                    builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] FPS drop detected");
                    builder.Append("FrameTimeSeconds: ").Append(frame.FrameTime.ToString("F6")).AppendLine();
                    builder.Append("ThreadId: ").Append(thread.ThreadId).AppendLine();
                    builder.Append("ThreadTotalTimeMs: ").Append(thread.TotalTimeMs.ToString("F3")).AppendLine();
                    builder.Append("CurrentMs: ").Append(currentMs.ToString("F3")).AppendLine();
                    builder.Append("PreviousMs: ").Append(previousMs.ToString("F3")).AppendLine();
                    builder.Append("BaselineMs: ").Append(baselineMs.ToString("F3")).AppendLine();
                    builder.Append("CurrentFps: ").Append(currentFps.ToString("F2")).AppendLine();
                    builder.Append("PreviousFps: ").Append(previousFps.ToString("F2")).AppendLine();
                    builder.Append("BaselineFps: ").Append(baselineFps.ToString("F2")).AppendLine();
                    builder.Append("ComparisonFps: ").Append(comparisonFps.ToString("F2")).AppendLine();
                    builder.Append("DeltaMs: ").Append(deltaMs.ToString("F3")).AppendLine();
                    builder.Append("DeltaFps: ").Append(deltaFps.ToString("F2")).AppendLine();
                    builder.Append("DropPercent: ").Append((dropFraction * 100.0f).ToString("F1")).AppendLine();
                    builder.Append("HotPathMs: ").Append(hotPathMs.ToString("F3")).AppendLine();
                    builder.Append("HotPath: ").Append(hotPath).AppendLine();

                    if (hotPath.Contains("WaitForRender", StringComparison.Ordinal)
                        && TryGetLikelyBlockingThread(frame.Threads, thread.ThreadId, out var blockingThread))
                    {
                        string blockingHotPath = GetHottestPath(blockingThread.RootNodes, out float blockingHotPathMs);
                        builder.Append("LikelyBlockingThreadId: ").Append(blockingThread.ThreadId).AppendLine();
                        builder.Append("LikelyBlockingThreadTotalTimeMs: ").Append(blockingThread.TotalTimeMs.ToString("F3")).AppendLine();
                        builder.Append("LikelyBlockingHotPathMs: ").Append(blockingHotPathMs.ToString("F3")).AppendLine();
                        builder.Append("LikelyBlockingHotPath: ").Append(blockingHotPath).AppendLine();
                    }

                    AppendTopRootTimings(builder, thread.RootNodes, 5);
                    AppendTopFrameThreads(builder, frame.Threads, thread.ThreadId, 5);
                    AppendTopComponentTimings(builder, frame.ComponentTimings?.Components, 5);
                    AppendRenderMatrixStatsSnapshot(builder);
                    AppendSkinnedBoundsStatsSnapshot(builder);
                    AppendOctreeStatsSnapshot(builder);
                    Debug.WriteAuxiliaryLog("profiler-fps-drops.log", builder.ToString());
                }
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
                    builder.Append("  ").Append(i + 1).Append(". ").Append(root.Name).Append(" = ").Append(root.ElapsedMs.ToString("F3")).AppendLine(" ms");
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
                    string hotPath = GetHottestPath(thread.RootNodes, out float hotPathMs);
                    builder.Append("  ").Append(i + 1).Append(". Thread ").Append(thread.ThreadId);
                    if (thread.ThreadId == currentThreadId)
                        builder.Append(" (current)");
                    builder.Append(" total=").Append(thread.TotalTimeMs.ToString("F3"));
                    builder.Append(" ms hot=").Append(hotPathMs.ToString("F3")).Append(" ms ");
                    builder.Append(hotPath).AppendLine();
                }
            }

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
                if (!Rendering.Stats.RenderMatrixStatsReady)
                    return;

                builder.AppendLine("RenderMatrixStats:");
                builder.Append("  Applied: ").Append(Rendering.Stats.RenderMatrixApplied.ToString("N0")).AppendLine();
                builder.Append("  NonEmptyBatches: ").Append(Rendering.Stats.RenderMatrixBatchCount.ToString("N0")).AppendLine();
                builder.Append("  MaxBatchSize: ").Append(Rendering.Stats.RenderMatrixMaxBatchSize.ToString("N0")).AppendLine();
                builder.Append("  SetCalls: ").Append(Rendering.Stats.RenderMatrixSetCalls.ToString("N0")).AppendLine();
                builder.Append("  ListenerInvocations: ").Append(Rendering.Stats.RenderMatrixListenerInvocations.ToString("N0")).AppendLine();
            }

            private static void AppendOctreeStatsSnapshot(StringBuilder builder)
            {
                if (!Rendering.Stats.OctreeStatsReady)
                    return;

                builder.AppendLine("OctreeStats:");
                builder.Append("  CollectCalls: ").Append(Rendering.Stats.OctreeCollectCallCount.ToString("N0")).AppendLine();
                builder.Append("  VisibleRenderables: ").Append(Rendering.Stats.OctreeVisibleRenderableCount.ToString("N0")).AppendLine();
                builder.Append("  EmittedCommands: ").Append(Rendering.Stats.OctreeEmittedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  MaxVisiblePerCollect: ").Append(Rendering.Stats.OctreeMaxVisibleRenderablesPerCollect.ToString("N0")).AppendLine();
                builder.Append("  MaxCommandsPerCollect: ").Append(Rendering.Stats.OctreeMaxEmittedCommandsPerCollect.ToString("N0")).AppendLine();
                builder.Append("  Add: ").Append(Rendering.Stats.OctreeAddCount.ToString("N0")).AppendLine();
                builder.Append("  Move: ").Append(Rendering.Stats.OctreeMoveCount.ToString("N0")).AppendLine();
                builder.Append("  Remove: ").Append(Rendering.Stats.OctreeRemoveCount.ToString("N0")).AppendLine();
                builder.Append("  SkippedMove: ").Append(Rendering.Stats.OctreeSkippedMoveCount.ToString("N0")).AppendLine();
                builder.Append("  SwapDrainedCommands: ").Append(Rendering.Stats.OctreeSwapDrainedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapBufferedCommands: ").Append(Rendering.Stats.OctreeSwapBufferedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapExecutedCommands: ").Append(Rendering.Stats.OctreeSwapExecutedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  SwapDrainMs: ").Append(Rendering.Stats.OctreeSwapDrainMs.ToString("F3")).AppendLine();
                builder.Append("  SwapExecuteMs: ").Append(Rendering.Stats.OctreeSwapExecuteMs.ToString("F3")).AppendLine();
                builder.Append("  SwapMaxCommandMs: ").Append(Rendering.Stats.OctreeSwapMaxCommandMs.ToString("F3")).AppendLine();
                builder.Append("  SwapMaxCommandKind: ").Append(Rendering.Stats.OctreeSwapMaxCommandKind).AppendLine();
                builder.Append("  RaycastProcessedCommands: ").Append(Rendering.Stats.OctreeRaycastProcessedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  RaycastDroppedCommands: ").Append(Rendering.Stats.OctreeRaycastDroppedCommandCount.ToString("N0")).AppendLine();
                builder.Append("  RaycastTraversalMs: ").Append(Rendering.Stats.OctreeRaycastTraversalMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastCallbackMs: ").Append(Rendering.Stats.OctreeRaycastCallbackMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxTraversalMs: ").Append(Rendering.Stats.OctreeRaycastMaxTraversalMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxCallbackMs: ").Append(Rendering.Stats.OctreeRaycastMaxCallbackMs.ToString("F3")).AppendLine();
                builder.Append("  RaycastMaxCommandMs: ").Append(Rendering.Stats.OctreeRaycastMaxCommandMs.ToString("F3")).AppendLine();
            }

            private static void AppendSkinnedBoundsStatsSnapshot(StringBuilder builder)
            {
                if (!Rendering.Stats.SkinnedBoundsStatsReady)
                    return;

                int deferredFinished = Rendering.Stats.SkinnedBoundsDeferredCompletedCount + Rendering.Stats.SkinnedBoundsDeferredFailedCount;
                double deferredAvgQueueMs = deferredFinished <= 0 ? 0.0 : Rendering.Stats.SkinnedBoundsDeferredQueueWaitMs / deferredFinished;
                double deferredAvgCpuJobMs = deferredFinished <= 0 ? 0.0 : Rendering.Stats.SkinnedBoundsDeferredCpuJobMs / deferredFinished;
                double deferredAvgApplyMs = deferredFinished <= 0 ? 0.0 : Rendering.Stats.SkinnedBoundsDeferredApplyMs / deferredFinished;
                double gpuAvgComputeMs = Rendering.Stats.SkinnedBoundsGpuCompletedCount <= 0 ? 0.0 : Rendering.Stats.SkinnedBoundsGpuComputeMs / Rendering.Stats.SkinnedBoundsGpuCompletedCount;
                double gpuAvgApplyMs = Rendering.Stats.SkinnedBoundsGpuCompletedCount <= 0 ? 0.0 : Rendering.Stats.SkinnedBoundsGpuApplyMs / Rendering.Stats.SkinnedBoundsGpuCompletedCount;

                builder.AppendLine("SkinnedBoundsStats:");
                builder.Append("  DeferredScheduled: ").Append(Rendering.Stats.SkinnedBoundsDeferredScheduledCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredCompleted: ").Append(Rendering.Stats.SkinnedBoundsDeferredCompletedCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredFailed: ").Append(Rendering.Stats.SkinnedBoundsDeferredFailedCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredInFlight: ").Append(Rendering.Stats.SkinnedBoundsDeferredInFlightCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredMaxInFlight: ").Append(Rendering.Stats.SkinnedBoundsDeferredMaxInFlightCount.ToString("N0")).AppendLine();
                builder.Append("  DeferredQueueWaitMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredQueueWaitMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgQueueWaitMs: ").Append(deferredAvgQueueMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxQueueWaitMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredMaxQueueWaitMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredCpuJobMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgCpuJobMs: ").Append(deferredAvgCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxCpuJobMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredMaxCpuJobMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredApplyMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredApplyMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredAvgApplyMs: ").Append(deferredAvgApplyMs.ToString("F3")).AppendLine();
                builder.Append("  DeferredMaxApplyMs: ").Append(Rendering.Stats.SkinnedBoundsDeferredMaxApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuCompleted: ").Append(Rendering.Stats.SkinnedBoundsGpuCompletedCount.ToString("N0")).AppendLine();
                builder.Append("  GpuComputeMs: ").Append(Rendering.Stats.SkinnedBoundsGpuComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuAvgComputeMs: ").Append(gpuAvgComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuMaxComputeMs: ").Append(Rendering.Stats.SkinnedBoundsGpuMaxComputeMs.ToString("F3")).AppendLine();
                builder.Append("  GpuApplyMs: ").Append(Rendering.Stats.SkinnedBoundsGpuApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuAvgApplyMs: ").Append(gpuAvgApplyMs.ToString("F3")).AppendLine();
                builder.Append("  GpuMaxApplyMs: ").Append(Rendering.Stats.SkinnedBoundsGpuMaxApplyMs.ToString("F3")).AppendLine();
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

            private static string GetHottestPath(IReadOnlyList<ProfilerNodeSnapshot> roots, out float pathMs)
            {
                pathMs = 0f;
                if (roots.Count == 0)
                    return "(no samples)";

                ProfilerNodeSnapshot hottest = roots[0];
                for (int i = 1; i < roots.Count; i++)
                {
                    if (roots[i].ElapsedMs > hottest.ElapsedMs)
                        hottest = roots[i];
                }

                pathMs = hottest.ElapsedMs;
                var parts = new List<string>(8) { hottest.Name };
                ProfilerNodeSnapshot current = hottest;
                while (current.Children.Count > 0)
                {
                    ProfilerNodeSnapshot best = current.Children[0];
                    for (int i = 1; i < current.Children.Count; i++)
                    {
                        if (current.Children[i].ElapsedMs > best.ElapsedMs)
                            best = current.Children[i];
                    }

                    parts.Add(best.Name);
                    current = best;
                }

                return string.Join(" > ", parts);
            }

            private static ProfilerNodeSnapshot BuildSnapshotFromBuilt(BuiltTimer timer)
            {
                var children = timer.Children;
                int childCount = children.Count;
                ProfilerNodeSnapshot[] childSnapshots = childCount > 0 ? new ProfilerNodeSnapshot[childCount] : [];

                for (int i = 0; i < childCount; ++i)
                    childSnapshots[i] = BuildSnapshotFromBuilt(children[i]);

                return new ProfilerNodeSnapshot(timer.Name, TicksToMilliseconds(timer.ElapsedTicks), childSnapshots);
            }

            private static float TicksToMilliseconds(long ticks)
                => (float)(ticks * 1000.0 / EngineTimer.StopwatchTickFrequency);

            public void ClearFrameLog()
            {
            }

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
                => Start(methodName);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ProfilerScope Start([CallerMemberName] string? methodName = null)
            {
                if (!_enableFrameLogging)
                    return default;

                var state = GetOrCreateThreadProducerState();
                int depth = state.Depth + 1;
                state.Depth = depth;
                long startTicks = Time.Timer.TimeTicks();
                return new ProfilerScope(this, state, startTicks, depth, methodName);
            }

            public Guid StartAsync(DelTimerCallback? callback = null, [CallerMemberName] string? methodName = null)
            {
                Guid id = Guid.NewGuid();
                if (!EnableFrameLogging)
                    return id;

                var state = GetOrCreateThreadProducerState();
                int correlationId = id.GetHashCode();
                _pendingAsyncTimers[correlationId] = new AsyncPendingTimer(Time.Timer.TimeTicks(), state.ThreadId, methodName);
                return id;
            }

            public float StopAsync(Guid id, out string? methodName)
            {
                methodName = string.Empty;
                int correlationId = id.GetHashCode();
                if (!_pendingAsyncTimers.TryRemove(correlationId, out var pending))
                    return 0.0f;

                methodName = pending.MethodName;
                if (!EnableFrameLogging)
                    return 0.0f;

                long endTicks = Time.Timer.TimeTicks();
                long elapsedTicks = Math.Max(0L, endTicks - pending.StartTicks);
                _overflowCompletedEvents.Enqueue(new CompletedScopeEvent(
                    pending.ThreadId,
                    Depth: 1,
                    pending.StartTicks,
                    elapsedTicks,
                    pending.MethodName,
                    IsAsyncRoot: true));

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

            public sealed class ProfilerFrameSnapshot(float frameTime, IReadOnlyList<ProfilerThreadSnapshot> threads, ProfilerComponentFrameSnapshot? componentTimings)
            {
                public float FrameTime { get; } = frameTime;
                public IReadOnlyList<ProfilerThreadSnapshot> Threads { get; } = threads;
                public ProfilerComponentFrameSnapshot? ComponentTimings { get; } = componentTimings;
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

            public sealed class ProfilerFrameSnapshot(float frameTime, IReadOnlyList<ProfilerThreadSnapshot> threads, ProfilerComponentFrameSnapshot? componentTimings)
            {
                public float FrameTime { get; } = frameTime;
                public IReadOnlyList<ProfilerThreadSnapshot> Threads { get; } = threads;
                public ProfilerComponentFrameSnapshot? ComponentTimings { get; } = componentTimings;
            }

            public sealed class ProfilerThreadSnapshot(int threadId, IReadOnlyList<ProfilerNodeSnapshot> rootNodes)
            {
                public int ThreadId { get; } = threadId;
                public IReadOnlyList<ProfilerNodeSnapshot> RootNodes { get; } = rootNodes;
                public float TotalTimeMs { get; } = 0f;
            }

            public sealed class ProfilerNodeSnapshot(string name, float elapsedMs, IReadOnlyList<ProfilerNodeSnapshot> children)
            {
                public string Name { get; } = name;
                public float ElapsedMs { get; } = elapsedMs;
                public IReadOnlyList<ProfilerNodeSnapshot> Children { get; } = children;
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
            public ProfilerScope Start([CallerMemberName] string? methodName = null)
                => default;

            public Guid StartAsync(DelTimerCallback? callback = null, [CallerMemberName] string? methodName = null)
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
