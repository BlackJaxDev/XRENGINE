using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

internal sealed class RenderPipelineGpuProfiler
{
    public static RenderPipelineGpuProfiler Instance { get; } = new();
    private const ulong PartialPublishDelayFrames = 3;
    private const ulong StalePendingQueryFrames = 120;
    private const int MaxTimestampScopesPerFrame = 512;
    private const double SlowTimestampQuerySuspendMilliseconds = 12.0;
    private const ulong SlowTimestampQuerySuspendFrames = 120;
    private const int TimingDumpWorstFrameLimit = 24;
    private const int TimingDumpTopNodeLimit = 64;
    private const int TimingDumpTopShaderNodeLimit = 40;
    private const string RenderThreadRootName = "Render Thread (CPU+Present)";

    private sealed class NodeAccumulator
    {
        private readonly Dictionary<string, NodeAccumulator> _childrenByName = new(StringComparer.Ordinal);
        private readonly List<NodeAccumulator> _children = [];

        public NodeAccumulator(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public ulong TotalNanoseconds { get; private set; }
        public int SampleCount { get; private set; }
        public IReadOnlyList<NodeAccumulator> Children => _children;

        public NodeAccumulator GetOrAddChild(string name)
        {
            if (_childrenByName.TryGetValue(name, out NodeAccumulator? existing))
                return existing;

            NodeAccumulator created = new(name);
            _childrenByName.Add(name, created);
            _children.Add(created);
            return created;
        }

        public void AddSample(ulong nanoseconds)
        {
            TotalNanoseconds += nanoseconds;
            SampleCount++;
        }
    }

    private sealed class FrameCapture
    {
        private readonly Dictionary<string, NodeAccumulator> _rootsByName = new(StringComparer.Ordinal);
        private readonly List<NodeAccumulator> _roots = [];

        public FrameCapture(ulong frameId)
        {
            FrameId = frameId;
        }

        public ulong FrameId { get; }
        public int PendingSamples { get; set; }
        public int SkippedSamples { get; set; }
        public bool HasSamples { get; private set; }
        public bool Supported { get; set; }
        public string BackendName { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public double RenderThreadMilliseconds { get; set; }
        public NodeAccumulator? RenderThreadTimingRoot { get; private set; }
        internal IReadOnlyList<NodeAccumulator> Roots => _roots;

        public void AddSample(IReadOnlyList<string> path, ulong nanoseconds)
        {
            if (path.Count == 0)
                return;

            HasSamples = true;
            NodeAccumulator node = GetOrAddRoot(path[0]);
            node.AddSample(nanoseconds);

            for (int i = 1; i < path.Count; i++)
            {
                node = node.GetOrAddChild(path[i]);
                node.AddSample(nanoseconds);
            }
        }

        private NodeAccumulator GetOrAddRoot(string name)
        {
            if (_rootsByName.TryGetValue(name, out NodeAccumulator? existing))
                return existing;

            NodeAccumulator created = new(name);
            _rootsByName.Add(name, created);
            _roots.Add(created);
            return created;
        }

        public void AddRenderThreadSample(string name, ulong nanoseconds)
        {
            if (string.IsNullOrWhiteSpace(name) || nanoseconds == 0UL)
                return;

            NodeAccumulator root = RenderThreadTimingRoot ??= new NodeAccumulator(RenderThreadRootName);
            root.AddSample(nanoseconds);
            root.GetOrAddChild(name).AddSample(nanoseconds);
        }
    }

    private sealed class PipelineTimingHistory
    {
        public PipelineTimingHistory(string pipelineName)
        {
            PipelineName = pipelineName;
        }

        public string PipelineName { get; }
        public string BackendName { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public DateTimeOffset FirstSampleTimestamp { get; set; }
        public DateTimeOffset LastSampleTimestamp { get; set; }
        public ulong FirstFrameId { get; set; }
        public ulong LastFrameId { get; set; }
        public int FrameCount { get; set; }
        public long TotalTimerSampleCount { get; set; }
        public double TotalFrameMilliseconds { get; set; }
        public double TotalFrameMillisecondsSquared { get; set; }
        public double MinFrameMilliseconds { get; set; } = double.MaxValue;
        public ulong MinFrameId { get; set; }
        public DateTimeOffset MinFrameTimestamp { get; set; }
        public double MaxFrameMilliseconds { get; set; }
        public ulong MaxFrameId { get; set; }
        public DateTimeOffset MaxFrameTimestamp { get; set; }
        public List<PipelineFrameSummary> FrameSummaries { get; } = [];
        public List<PipelineWorstFrame> WorstFrames { get; } = [];
        public Dictionary<string, PipelineNodeTimingStats> NodesByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PipelineNodeTimingStats> RenderThreadNodesByPath { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct PipelineFrameSummary(
        ulong FrameId,
        DateTimeOffset Timestamp,
        double FrameMilliseconds,
        int SampleCount,
        double RenderThreadMilliseconds,
        double RenderThreadUnattributedMilliseconds);

    private sealed class PipelineWorstFrame
    {
        public ulong FrameId { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public double FrameMilliseconds { get; init; }
        public double RenderThreadMilliseconds { get; init; }
        public double RenderThreadUnattributedMilliseconds { get; init; }
        public int SampleCount { get; init; }
        public required GpuPipelineTimingNodeData Root { get; init; }
    }

    private sealed class PipelineNodeTimingStats
    {
        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Depth { get; init; }
        public bool IsPotentialShaderOrMaterialNode { get; init; }
        public int ObservedFrameCount { get; private set; }
        public long TotalSampleCount { get; private set; }
        public double TotalMilliseconds { get; private set; }
        public double MinMilliseconds { get; private set; } = double.MaxValue;
        public double MaxMilliseconds { get; private set; }
        public double LastMilliseconds { get; private set; }
        public ulong WorstFrameId { get; private set; }
        public DateTimeOffset WorstTimestamp { get; private set; }
        public ulong LastFrameId { get; private set; }
        public DateTimeOffset LastTimestamp { get; private set; }

        public double AverageFrameMilliseconds
            => ObservedFrameCount <= 0 ? 0.0 : TotalMilliseconds / ObservedFrameCount;

        public double AverageTimerSampleMilliseconds
            => TotalSampleCount <= 0 ? 0.0 : TotalMilliseconds / TotalSampleCount;

        public void AddFrameSample(ulong frameId, DateTimeOffset timestamp, double milliseconds, int sampleCount)
        {
            ObservedFrameCount++;
            TotalSampleCount += Math.Max(0, sampleCount);
            TotalMilliseconds += milliseconds;
            LastMilliseconds = milliseconds;
            LastFrameId = frameId;
            LastTimestamp = timestamp;

            if (milliseconds < MinMilliseconds)
                MinMilliseconds = milliseconds;

            if (milliseconds >= MaxMilliseconds)
            {
                MaxMilliseconds = milliseconds;
                WorstFrameId = frameId;
                WorstTimestamp = timestamp;
            }
        }
    }

    private readonly struct PendingScope(ulong frameId, string[] path, GLRenderQuery startQuery, GLRenderQuery endQuery)
    {
        public ulong FrameId { get; } = frameId;
        public string[] Path { get; } = path;
        public GLRenderQuery StartQuery { get; } = startQuery;
        public GLRenderQuery EndQuery { get; } = endQuery;
    }

    private readonly struct OrphanedQuery(ulong frameId, GLRenderQuery query)
    {
        public ulong FrameId { get; } = frameId;
        public GLRenderQuery Query { get; } = query;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly RenderPipelineGpuProfiler? _profiler;
        private readonly ulong _frameId;
        private readonly string[]? _path;
        private readonly GLRenderQuery? _startQuery;
        private readonly bool _popScope;

        internal Scope(RenderPipelineGpuProfiler profiler, ulong frameId, string[] path, GLRenderQuery startQuery, bool popScope)
        {
            _profiler = profiler;
            _frameId = frameId;
            _path = path;
            _startQuery = startQuery;
            _popScope = popScope;
        }

        public void Dispose()
            => _profiler?.EndScope(_frameId, _path, _startQuery, _popScope);
    }

    [ThreadStatic]
    private static List<string>? _commandScopeStack;
    [ThreadStatic]
    private static List<string>? _commandRootPipelineStack;
    [ThreadStatic]
    private static List<string>? _userScopeStack;
    [ThreadStatic]
    private static Stack<UserScopeHandle>? _openUserScopes;

    private readonly object _lock = new();
    private readonly Queue<XRRenderQuery> _queryPool = new();
    private readonly Dictionary<ulong, FrameCapture> _frames = [];
    private readonly Dictionary<string, PipelineTimingHistory> _pipelineTimingHistories = new(StringComparer.Ordinal);
    private readonly List<PendingScope> _pendingScopes = [];
    private readonly List<OrphanedQuery> _orphanedQueries = [];
    private RenderStatsGpuPipelineSnapshot _latestSnapshot = RenderStatsGpuPipelineSnapshot.Disabled();
    private ulong _lastPublishedFrameId;
    private bool _hasPublishedFrame;
    private string _lastBackendName = string.Empty;
    private ulong _timestampBudgetFrameId;
    private int _timestampScopesIssuedThisFrame;
    private ulong _timestampQueriesSuspendedUntilFrameId;
    private int _enabled;

    private RenderPipelineGpuProfiler()
    {
    }

    /// <summary>
    /// Returns true when the host has asked for GPU pipeline profiling and stats tracking
    /// is enabled. Checked directly on each scope call so that profiling works regardless
    /// of whether <see cref="BeginFrame"/> has been invoked yet for the current frame
    /// (e.g. shadow/probe/scene-capture passes that execute before the main render).
    /// </summary>
    private static bool IsProfilingRequested()
    {
        var host = RuntimeRenderingHostServices.Current;
        return host.EnableRenderStatisticsTracking && host.EnableGpuRenderPipelineProfiling;
    }

    public bool IsProfilingActive
        => Volatile.Read(ref _enabled) != 0 || IsProfilingRequested();

    private readonly struct UserScopeHandle(ulong frameId, string[] path, GLRenderQuery startQuery)
    {
        public ulong FrameId { get; } = frameId;
        public string[] Path { get; } = path;
        public GLRenderQuery StartQuery { get; } = startQuery;
    }

    public RenderStatsGpuPipelineSnapshot LatestSnapshot
    {
        get
        {
            lock (_lock)
                return _latestSnapshot;
        }
    }

    public bool TryDumpTimingHistory(string pipelineName, out string fileName, out string? error)
    {
        fileName = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(pipelineName))
        {
            error = "No render pipeline name was supplied for the GPU timing dump.";
            return false;
        }

        string content;
        DateTimeOffset dumpTimestamp = DateTimeOffset.Now;

        lock (_lock)
        {
            if (!_pipelineTimingHistories.TryGetValue(pipelineName, out PipelineTimingHistory? history) ||
                history.FrameCount == 0)
            {
                error = $"No GPU timing history has been captured for render pipeline '{pipelineName}'.";
                return false;
            }

            fileName = BuildTimingDumpFileName(pipelineName, dumpTimestamp);
            content = BuildTimingDumpContentNoLock(history, dumpTimestamp, fileName);
        }

        Debug.WriteAuxiliaryLog(fileName, content);
        return true;
    }

    public bool TryDumpAllTimingHistories(out string[] fileNames, out string? error)
    {
        fileNames = [];
        error = null;

        List<(string FileName, string Content)> dumps = [];
        DateTimeOffset dumpTimestamp = DateTimeOffset.Now;

        lock (_lock)
        {
            foreach (PipelineTimingHistory history in _pipelineTimingHistories.Values)
            {
                if (history.FrameCount == 0)
                    continue;

                string fileName = BuildTimingDumpFileName(history.PipelineName, dumpTimestamp);
                string content = BuildTimingDumpContentNoLock(history, dumpTimestamp, fileName);
                dumps.Add((fileName, content));
            }
        }

        if (dumps.Count == 0)
        {
            error = "No GPU timing history has been captured for any render pipeline.";
            return false;
        }

        string[] written = new string[dumps.Count];
        for (int i = 0; i < dumps.Count; i++)
        {
            (string fileName, string content) = dumps[i];
            Debug.WriteAuxiliaryLog(fileName, content);
            written[i] = fileName;
        }

        fileNames = written;
        return true;
    }

    /// <summary>
    /// Records the wall-clock CPU duration of the render-thread frame dispatch (begin-of-frame
    /// through end of SwapBuffers) for the given frame id. Surfaced as a "Render Thread (CPU+Present)"
    /// root entry so the profiler totals can be compared directly to the FPS overlay.
    /// </summary>
    public void RecordRenderThreadFrameMs(ulong frameId, double milliseconds)
    {
        if (!IsProfilingRequested() ||
            milliseconds <= 0.0 ||
            double.IsNaN(milliseconds) ||
            double.IsInfinity(milliseconds))
            return;

        lock (_lock)
        {
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.RenderThreadMilliseconds = milliseconds;
        }
    }

    /// <summary>
    /// Records a named wall-clock phase inside XRWindow.RenderFrame for the same frame id as
    /// the GPU timers. The gap between these named phases and the total render-thread frame is
    /// reported as unattributed CPU/present wait in dumps and the live profiler tree.
    /// </summary>
    public void RecordRenderThreadCpuTiming(ulong frameId, string name, double milliseconds)
    {
        if (!IsProfilingRequested() ||
            frameId == 0UL ||
            string.IsNullOrWhiteSpace(name) ||
            milliseconds <= 0.0 ||
            double.IsNaN(milliseconds) ||
            double.IsInfinity(milliseconds))
        {
            return;
        }

        ulong nanoseconds = (ulong)Math.Round(milliseconds * 1_000_000.0);
        if (nanoseconds == 0UL)
            return;

        lock (_lock)
        {
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.AddRenderThreadSample(name, nanoseconds);
        }
    }

    public void BeginFrame(ulong frameId, bool enabled)
    {
        Volatile.Write(ref _enabled, enabled ? 1 : 0);

        lock (_lock)
        {
            if (!enabled)
            {
                CancelDanglingUserScopesNoLock();
                ClearPendingScopesNoLock();
                ClearOrphanedQueriesNoLock();
                _frames.Clear();
                _pipelineTimingHistories.Clear();
                _hasPublishedFrame = false;
                _lastPublishedFrameId = 0UL;
                _timestampBudgetFrameId = 0UL;
                _timestampScopesIssuedThisFrame = 0;
                _timestampQueriesSuspendedUntilFrameId = 0UL;
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Disabled();
                return;
            }

            CancelDanglingUserScopesNoLock();
            ResolveOrphanedQueriesNoLock(frameId);
            ResolveCompletedScopesNoLock(frameId);
            if (!_latestSnapshot.Enabled)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Pending(GetLastBackendNameNoLock(), "Waiting for the first resolved GPU command frame.");

            PublishLatestResolvedFrameNoLock(frameId);
            PruneFramesNoLock(frameId);
        }
    }

    public Scope StartScope(ViewportRenderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (Volatile.Read(ref _enabled) == 0 && !IsProfilingRequested())
            return default;

        return StartScope(command.GpuProfilingName);
    }

    public Scope StartScope(string scopeName)
    {
        if (string.IsNullOrWhiteSpace(scopeName))
            return default;

        if (Volatile.Read(ref _enabled) == 0 && !IsProfilingRequested())
            return default;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Unsupported(GetBackendName(), "GPU render-pipeline command timing is currently available on OpenGL.");
            return default;
        }

        XRRenderPipelineInstance instance = ViewportRenderCommand.ActivePipelineInstance;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        List<string> commandStack = _commandScopeStack ??= [];
        List<string> pipelineStack = _commandRootPipelineStack ??= [];
        List<string> userStack = _userScopeStack ??= [];
        string currentPipelineName = instance.ProfilerKey;
        string pipelineName = pipelineStack.Count > 0 ? pipelineStack[0] : currentPipelineName;

        string[] path = new string[userStack.Count + commandStack.Count + 2];
        path[0] = pipelineName;
        int pathIndex = 1;
        for (int i = 0; i < userStack.Count; i++)
            path[pathIndex++] = userStack[i];
        for (int i = 0; i < commandStack.Count; i++)
            path[pathIndex++] = commandStack[i];
        path[^1] = scopeName;

        if (!TryBeginTimestampScope(renderer, frameId, out GLRenderQuery? startQuery))
            return default;

        commandStack.Add(scopeName);
        pipelineStack.Add(pipelineName);

        lock (_lock)
        {
            _lastBackendName = "OpenGL";
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.Supported = true;
            frame.BackendName = "OpenGL";
            frame.StatusMessage = string.Empty;
            frame.PendingSamples++;
            if (!_latestSnapshot.Enabled)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Pending(GetLastBackendNameNoLock(), "Waiting for the first resolved GPU command frame.");
        }

        return new Scope(this, frameId, path, startQuery!, popScope: true);
    }

    public bool PushUserScope(string scopeName)
    {
        if (string.IsNullOrWhiteSpace(scopeName) ||
            (Volatile.Read(ref _enabled) == 0 && !IsProfilingRequested()))
            return false;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Unsupported(GetBackendName(), "GPU render-pipeline command timing is currently available on OpenGL.");
            return false;
        }

        XRRenderPipelineInstance instance = ViewportRenderCommand.ActivePipelineInstance;
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        string pipelineName = instance.ProfilerKey;

        List<string> userStack = _userScopeStack ??= [];
        Stack<UserScopeHandle> openScopes = _openUserScopes ??= [];
        string[] path = new string[userStack.Count + 2];
        path[0] = pipelineName;
        for (int i = 0; i < userStack.Count; i++)
            path[i + 1] = userStack[i];
        path[^1] = scopeName;

        if (!TryBeginTimestampScope(renderer, frameId, out GLRenderQuery? startQuery))
            return false;

        userStack.Add(scopeName);
        openScopes.Push(new UserScopeHandle(frameId, path, startQuery!));

        lock (_lock)
        {
            _lastBackendName = "OpenGL";
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.Supported = true;
            frame.BackendName = "OpenGL";
            frame.StatusMessage = string.Empty;
            frame.PendingSamples++;
            if (!_latestSnapshot.Enabled)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Pending(GetLastBackendNameNoLock(), "Waiting for the first resolved GPU command frame.");
        }

        return true;
    }

    public void PopUserScope(string? expectedScopeName = null)
    {
        Stack<UserScopeHandle>? openScopes = _openUserScopes;
        if (openScopes is null || openScopes.Count == 0)
            return;

        UserScopeHandle scope = openScopes.Pop();

        List<string>? userStack = _userScopeStack;
        if (userStack is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(expectedScopeName) &&
                !string.Equals(userStack[^1], expectedScopeName, StringComparison.Ordinal))
            {
                Debug.RenderingWarning($"GPU timer scope mismatch. Expected '{expectedScopeName}', but closing '{userStack[^1]}'.");
            }

            userStack.RemoveAt(userStack.Count - 1);
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
            {
                RetireQueryWhenReadyNoLock(scope.FrameId, scope.StartQuery);
                CancelPendingSampleNoLock(scope.FrameId);
            }
            return;
        }

        if (!TryBeginTimestampScope(renderer, scope.FrameId, out GLRenderQuery? endQuery))
        {
            lock (_lock)
            {
                RetireQueryWhenReadyNoLock(scope.FrameId, scope.StartQuery);
                CancelPendingSampleNoLock(scope.FrameId);
            }
            return;
        }

        lock (_lock)
            _pendingScopes.Add(new PendingScope(scope.FrameId, scope.Path, scope.StartQuery, endQuery!));
    }

    private void EndScope(ulong frameId, string[]? path, GLRenderQuery? startQuery, bool popScope)
    {
        if (popScope)
        {
            List<string>? stack = _commandScopeStack;
            if (stack is { Count: > 0 })
                stack.RemoveAt(stack.Count - 1);

            List<string>? pipelineStack = _commandRootPipelineStack;
            if (pipelineStack is { Count: > 0 })
                pipelineStack.RemoveAt(pipelineStack.Count - 1);
        }

        if (path is null || startQuery is null)
            return;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
            {
                RetireQueryWhenReadyNoLock(frameId, startQuery);
                CancelPendingSampleNoLock(frameId);
            }
            return;
        }

        if (!TryBeginTimestampScope(renderer, frameId, out GLRenderQuery? endQuery))
        {
            lock (_lock)
            {
                RetireQueryWhenReadyNoLock(frameId, startQuery);
                CancelPendingSampleNoLock(frameId);
            }
            return;
        }

        lock (_lock)
        {
            _pendingScopes.Add(new PendingScope(frameId, path, startQuery, endQuery!));
        }
    }

    private FrameCapture GetOrCreateFrameNoLock(ulong frameId)
    {
        if (_frames.TryGetValue(frameId, out FrameCapture? existing))
            return existing;

        FrameCapture created = new(frameId);
        _frames.Add(frameId, created);
        return created;
    }

    private void ResolveCompletedScopesNoLock(ulong currentFrameId)
    {
        for (int i = _pendingScopes.Count - 1; i >= 0; i--)
        {
            PendingScope pending = _pendingScopes[i];
            if (IsOlderThan(currentFrameId, pending.FrameId, StalePendingQueryFrames))
            {
                CancelPendingSampleNoLock(pending.FrameId);
                RetireQueryWhenReadyNoLock(pending.FrameId, pending.StartQuery);
                RetireQueryWhenReadyNoLock(pending.FrameId, pending.EndQuery);
                _pendingScopes.RemoveAt(i);
                continue;
            }

            if (_hasPublishedFrame && pending.FrameId <= _lastPublishedFrameId)
            {
                if (TryReadTimestamp(pending.StartQuery, out _) &&
                    TryReadTimestamp(pending.EndQuery, out _))
                {
                    ReleaseQueryNoLock(pending.StartQuery);
                    ReleaseQueryNoLock(pending.EndQuery);
                    _pendingScopes.RemoveAt(i);
                }

                continue;
            }

            if (!TryReadTimestamp(pending.StartQuery, out ulong startTimestamp) ||
                !TryReadTimestamp(pending.EndQuery, out ulong endTimestamp))
            {
                continue;
            }

            ulong duration = endTimestamp > startTimestamp ? endTimestamp - startTimestamp : 0UL;
            FrameCapture frame = GetOrCreateFrameNoLock(pending.FrameId);
            frame.Supported = true;
            frame.BackendName = "OpenGL";
            frame.StatusMessage = string.Empty;
            frame.AddSample(pending.Path, duration);
            if (frame.PendingSamples > 0)
                frame.PendingSamples--;

            ReleaseQueryNoLock(pending.StartQuery);
            ReleaseQueryNoLock(pending.EndQuery);
            _pendingScopes.RemoveAt(i);
        }
    }

    private void CancelPendingSampleNoLock(ulong frameId)
    {
        if (_frames.TryGetValue(frameId, out FrameCapture? frame) && frame.PendingSamples > 0)
            frame.PendingSamples--;
    }

    private bool TryBeginTimestampScope(OpenGLRenderer renderer, ulong frameId, out GLRenderQuery? query)
    {
        query = null;
        if (!TryReserveTimestampScope(frameId))
            return false;

        GLRenderQuery candidate = AcquireTimestampQuery(renderer);
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            candidate.QueryCounter();
            RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        }
        catch (Exception ex)
        {
            candidate.Data.CurrentQuery = null;
            candidate.Destroy();

            lock (_lock)
                RecordSkippedSampleNoLock(frameId, $"OpenGL timestamp query failed; GPU pipeline timing skipped. {ex.Message}");
            return false;
        }

        double elapsedMilliseconds = StopwatchTicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
        if (elapsedMilliseconds >= SlowTimestampQuerySuspendMilliseconds)
        {
            lock (_lock)
            {
                _timestampQueriesSuspendedUntilFrameId = Math.Max(
                    _timestampQueriesSuspendedUntilFrameId,
                    frameId + SlowTimestampQuerySuspendFrames);

                RecordSkippedSampleNoLock(
                    frameId,
                    $"OpenGL timestamp queries are stalling ({elapsedMilliseconds:0.0} ms); GPU pipeline timing is temporarily throttled.");
            }
        }

        query = candidate;
        return true;
    }

    private bool TryReserveTimestampScope(ulong frameId)
    {
        lock (_lock)
        {
            if (_timestampBudgetFrameId != frameId)
            {
                _timestampBudgetFrameId = frameId;
                _timestampScopesIssuedThisFrame = 0;
            }

            if (_timestampQueriesSuspendedUntilFrameId != 0UL)
            {
                if (frameId <= _timestampQueriesSuspendedUntilFrameId)
                {
                    RecordSkippedSampleNoLock(frameId, "OpenGL timestamp queries are temporarily throttled after a slow driver call.");
                    return false;
                }

                _timestampQueriesSuspendedUntilFrameId = 0UL;
            }

            if (_timestampScopesIssuedThisFrame >= MaxTimestampScopesPerFrame)
            {
                RecordSkippedSampleNoLock(
                    frameId,
                    $"GPU pipeline timing reached the per-frame timestamp scope budget ({MaxTimestampScopesPerFrame}); later scopes were skipped.");
                return false;
            }

            _timestampScopesIssuedThisFrame++;
            return true;
        }
    }

    private void RecordSkippedSampleNoLock(ulong frameId, string statusMessage)
    {
        _lastBackendName = "OpenGL";
        FrameCapture frame = GetOrCreateFrameNoLock(frameId);
        frame.Supported = true;
        frame.BackendName = "OpenGL";
        frame.SkippedSamples++;
        if (string.IsNullOrWhiteSpace(frame.StatusMessage))
            frame.StatusMessage = statusMessage;
        if (!_latestSnapshot.Enabled)
            _latestSnapshot = RenderStatsGpuPipelineSnapshot.Pending(GetLastBackendNameNoLock(), statusMessage);
    }

    private void PublishLatestResolvedFrameNoLock(ulong currentFrameId)
    {
        FrameCapture? best = null;

        foreach ((ulong frameId, FrameCapture frame) in _frames)
        {
            if (frameId >= currentFrameId)
                continue;

            if (frame.PendingSamples > 0 &&
                !IsOlderThan(currentFrameId, frameId, PartialPublishDelayFrames))
            {
                continue;
            }

            if (frame.PendingSamples > 0 && !frame.HasSamples)
                continue;

            if (!frame.HasSamples)
                continue;

            if (best is null || frameId > best.FrameId)
                best = frame;
        }

        if (best is null)
            return;

        RecordTimingHistoryNoLock(best);
        _latestSnapshot = CreateSnapshot(best);
        if (!_hasPublishedFrame || best.FrameId > _lastPublishedFrameId)
        {
            _lastPublishedFrameId = best.FrameId;
            _hasPublishedFrame = true;
        }

        List<ulong> keysToRemove = [];
        foreach (ulong key in _frames.Keys)
        {
            if (key <= best.FrameId)
                keysToRemove.Add(key);
        }

        for (int i = 0; i < keysToRemove.Count; i++)
            _frames.Remove(keysToRemove[i]);
    }

    private void PruneFramesNoLock(ulong currentFrameId)
    {
        List<ulong>? stale = null;
        foreach ((ulong frameId, FrameCapture frame) in _frames)
        {
            if (!IsOlderThan(currentFrameId, frameId, StalePendingQueryFrames))
                continue;

            stale ??= [];
            stale.Add(frameId);
        }

        if (stale is null)
            return;

        for (int i = 0; i < stale.Count; i++)
            _frames.Remove(stale[i]);
    }

    private void CancelDanglingUserScopesNoLock()
    {
        Stack<UserScopeHandle>? openScopes = _openUserScopes;
        int danglingCount = openScopes?.Count ?? 0;
        if (danglingCount > 0)
        {
            while (openScopes!.Count > 0)
            {
                UserScopeHandle scope = openScopes.Pop();
                CancelPendingSampleNoLock(scope.FrameId);
                RetireQueryWhenReadyNoLock(scope.FrameId, scope.StartQuery);
            }

            Debug.RenderingWarningEvery(
                "RenderPipelineGpuProfiler.DanglingUserScopes",
                TimeSpan.FromSeconds(1),
                "Cleared {0} dangling GPU timer scope(s) at frame boundary. Check VPRC_GPUTimerBegin/End pairing.",
                danglingCount);
        }

        _userScopeStack?.Clear();

        if (_commandScopeStack is { Count: > 0 } commandStack)
        {
            Debug.RenderingWarningEvery(
                "RenderPipelineGpuProfiler.DanglingCommandScopes",
                TimeSpan.FromSeconds(1),
                "Cleared {0} dangling GPU command timer scope(s) at frame boundary.",
                commandStack.Count);
            commandStack.Clear();
        }

        _commandRootPipelineStack?.Clear();
    }

    private void ResolveOrphanedQueriesNoLock(ulong currentFrameId)
    {
        for (int i = _orphanedQueries.Count - 1; i >= 0; i--)
        {
            OrphanedQuery orphaned = _orphanedQueries[i];
            if (TryReadTimestamp(orphaned.Query, out _))
            {
                ReleaseQueryNoLock(orphaned.Query);
                _orphanedQueries.RemoveAt(i);
                continue;
            }

            if (IsOlderThan(currentFrameId, orphaned.FrameId, StalePendingQueryFrames))
            {
                orphaned.Query.Destroy();
                _orphanedQueries.RemoveAt(i);
            }
        }
    }

    private void RetireQueryWhenReadyNoLock(ulong frameId, GLRenderQuery query)
    {
        query.Data.CurrentQuery = null;
        _orphanedQueries.Add(new OrphanedQuery(frameId, query));
    }

    private void ClearPendingScopesNoLock()
    {
        for (int i = 0; i < _pendingScopes.Count; i++)
        {
            _pendingScopes[i].StartQuery.Destroy();
            _pendingScopes[i].EndQuery.Destroy();
        }

        _pendingScopes.Clear();
    }

    private void ClearOrphanedQueriesNoLock()
    {
        for (int i = 0; i < _orphanedQueries.Count; i++)
            _orphanedQueries[i].Query.Destroy();

        _orphanedQueries.Clear();
    }

    private GLRenderQuery AcquireTimestampQuery(OpenGLRenderer renderer)
    {
        XRRenderQuery query;
        lock (_lock)
            query = _queryPool.Count > 0 ? _queryPool.Dequeue() : new XRRenderQuery();

        query.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        GLRenderQuery? glQuery = renderer.GenericToAPI<GLRenderQuery>(query)
            ?? throw new InvalidOperationException("Failed to create OpenGL render query wrapper for GPU profiling.");
        if (!glQuery.IsGenerated)
            glQuery.Generate();
        glQuery.Data.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        return glQuery;
    }

    private void ReleaseQueryNoLock(GLRenderQuery query)
    {
        query.Data.CurrentQuery = null;
        _queryPool.Enqueue(query.Data);
    }

    private string GetLastBackendNameNoLock()
        => string.IsNullOrWhiteSpace(_lastBackendName) ? GetBackendName() : _lastBackendName;

    private static bool TryReadTimestamp(GLRenderQuery query, out ulong timestamp)
    {
        timestamp = 0UL;
        long available = query.GetQueryObject(EGetQueryObject.QueryResultAvailable);
        if (available == 0)
            return false;

        timestamp = (ulong)query.GetQueryObject(EGetQueryObject.QueryResult);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryReadbackBytes, sizeof(ulong));
        return true;
    }

    private static bool IsOlderThan(ulong currentFrameId, ulong frameId, ulong frameCount)
        => currentFrameId > frameId && currentFrameId - frameId > frameCount;

    private static string GetBackendName()
    {
        string currentName = AbstractRenderer.Current switch
        {
            OpenGLRenderer => "OpenGL",
            Vulkan.VulkanRenderer => "Vulkan",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(currentName))
            return currentName;

        return RuntimeRenderingHostServices.Current.CurrentRenderBackend switch
        {
            RuntimeGraphicsApiKind.OpenGL => "OpenGL",
            RuntimeGraphicsApiKind.Vulkan => "Vulkan",
            _ => "Unknown"
        };
    }

    private void RecordTimingHistoryNoLock(FrameCapture frame)
    {
        if (frame.Roots.Count == 0)
            return;

        DateTimeOffset timestamp = DateTimeOffset.Now;
        for (int i = 0; i < frame.Roots.Count; i++)
        {
            NodeAccumulator root = frame.Roots[i];
            PipelineTimingHistory history = GetOrCreatePipelineTimingHistoryNoLock(root.Name);
            history.BackendName = string.IsNullOrWhiteSpace(frame.BackendName) ? history.BackendName : frame.BackendName;
            history.StatusMessage = frame.StatusMessage;

            double frameMilliseconds = root.TotalNanoseconds / 1_000_000.0;
            int sampleCount = root.SampleCount;
            double renderThreadMilliseconds = frame.RenderThreadMilliseconds;
            double renderThreadUnattributedMilliseconds = GetRenderThreadUnattributedMilliseconds(frame);

            if (history.FrameCount == 0)
            {
                history.FirstSampleTimestamp = timestamp;
                history.FirstFrameId = frame.FrameId;
            }

            history.LastSampleTimestamp = timestamp;
            history.LastFrameId = frame.FrameId;
            history.FrameCount++;
            history.TotalTimerSampleCount += sampleCount;
            history.TotalFrameMilliseconds += frameMilliseconds;
            history.TotalFrameMillisecondsSquared += frameMilliseconds * frameMilliseconds;
            history.FrameSummaries.Add(new PipelineFrameSummary(
                frame.FrameId,
                timestamp,
                frameMilliseconds,
                sampleCount,
                renderThreadMilliseconds,
                renderThreadUnattributedMilliseconds));

            if (frameMilliseconds < history.MinFrameMilliseconds)
            {
                history.MinFrameMilliseconds = frameMilliseconds;
                history.MinFrameId = frame.FrameId;
                history.MinFrameTimestamp = timestamp;
            }

            if (frameMilliseconds >= history.MaxFrameMilliseconds)
            {
                history.MaxFrameMilliseconds = frameMilliseconds;
                history.MaxFrameId = frame.FrameId;
                history.MaxFrameTimestamp = timestamp;
            }

            RecordNodeTimingStatsNoLock(history, root, root.Name, depth: 0, frame.FrameId, timestamp);
            if (frame.RenderThreadTimingRoot is not null)
            {
                RecordNodeTimingStatsNoLock(
                    history.RenderThreadNodesByPath,
                    frame.RenderThreadTimingRoot,
                    frame.RenderThreadTimingRoot.Name,
                    depth: 0,
                    frame.FrameId,
                    timestamp);
            }

            if (ShouldRetainWorstFrame(history, frameMilliseconds))
            {
                history.WorstFrames.Add(new PipelineWorstFrame
                {
                    FrameId = frame.FrameId,
                    Timestamp = timestamp,
                    FrameMilliseconds = frameMilliseconds,
                    RenderThreadMilliseconds = renderThreadMilliseconds,
                    RenderThreadUnattributedMilliseconds = renderThreadUnattributedMilliseconds,
                    SampleCount = sampleCount,
                    Root = ToPacket(root),
                });
                history.WorstFrames.Sort(static (left, right) => right.FrameMilliseconds.CompareTo(left.FrameMilliseconds));
                if (history.WorstFrames.Count > TimingDumpWorstFrameLimit)
                    history.WorstFrames.RemoveAt(history.WorstFrames.Count - 1);
            }
        }
    }

    private PipelineTimingHistory GetOrCreatePipelineTimingHistoryNoLock(string pipelineName)
    {
        if (_pipelineTimingHistories.TryGetValue(pipelineName, out PipelineTimingHistory? existing))
            return existing;

        PipelineTimingHistory created = new(pipelineName);
        _pipelineTimingHistories.Add(pipelineName, created);
        return created;
    }

    private static void RecordNodeTimingStatsNoLock(
        PipelineTimingHistory history,
        NodeAccumulator node,
        string path,
        int depth,
        ulong frameId,
        DateTimeOffset timestamp)
    {
        if (!history.NodesByPath.TryGetValue(path, out PipelineNodeTimingStats? stats))
        {
            stats = new PipelineNodeTimingStats
            {
                Path = path,
                Name = node.Name,
                Depth = depth,
                IsPotentialShaderOrMaterialNode = IsPotentialShaderOrMaterialNode(path),
            };
            history.NodesByPath.Add(path, stats);
        }

        stats.AddFrameSample(frameId, timestamp, node.TotalNanoseconds / 1_000_000.0, node.SampleCount);

        for (int i = 0; i < node.Children.Count; i++)
        {
            NodeAccumulator child = node.Children[i];
            RecordNodeTimingStatsNoLock(
                history,
                child,
                string.Concat(path, " > ", child.Name),
                depth + 1,
                frameId,
                timestamp);
        }
    }

    private static void RecordNodeTimingStatsNoLock(
        Dictionary<string, PipelineNodeTimingStats> nodesByPath,
        NodeAccumulator node,
        string path,
        int depth,
        ulong frameId,
        DateTimeOffset timestamp)
    {
        if (!nodesByPath.TryGetValue(path, out PipelineNodeTimingStats? stats))
        {
            stats = new PipelineNodeTimingStats
            {
                Path = path,
                Name = node.Name,
                Depth = depth,
                IsPotentialShaderOrMaterialNode = false,
            };
            nodesByPath.Add(path, stats);
        }

        stats.AddFrameSample(frameId, timestamp, node.TotalNanoseconds / 1_000_000.0, node.SampleCount);

        for (int i = 0; i < node.Children.Count; i++)
        {
            NodeAccumulator child = node.Children[i];
            RecordNodeTimingStatsNoLock(
                nodesByPath,
                child,
                string.Concat(path, " > ", child.Name),
                depth + 1,
                frameId,
                timestamp);
        }
    }

    private static bool ShouldRetainWorstFrame(PipelineTimingHistory history, double frameMilliseconds)
        => history.WorstFrames.Count < TimingDumpWorstFrameLimit ||
           frameMilliseconds > history.WorstFrames[^1].FrameMilliseconds;

    private static string BuildTimingDumpFileName(string pipelineName, DateTimeOffset timestamp)
    {
        string pipelineSegment = NormalizeDumpNameSegment(pipelineName);
        string timestampSegment = timestamp.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        string nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"profiler-gpu-pipeline-{pipelineSegment}-{timestampSegment}-{nonce}.log";
    }

    private static string BuildTimingDumpContentNoLock(PipelineTimingHistory history, DateTimeOffset dumpTimestamp, string fileName)
    {
        StringBuilder builder = new(64 * 1024);
        AppendLine(builder, "# XRENGINE GPU Render Pipeline Timing Dump");
        AppendLine(builder, "");
        AppendKeyValue(builder, "dump_file", fileName);
        AppendKeyValue(builder, "dump_local_time", FormatTimestamp(dumpTimestamp));
        AppendKeyValue(builder, "pipeline", history.PipelineName);
        AppendKeyValue(builder, "backend", string.IsNullOrWhiteSpace(history.BackendName) ? "Unknown" : history.BackendName);
        AppendKeyValue(builder, "frames_tracked", history.FrameCount.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, "timer_samples_tracked", history.TotalTimerSampleCount.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, "first_frame_id", history.FirstFrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, "last_frame_id", history.LastFrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, "first_sample_local_time", FormatTimestamp(history.FirstSampleTimestamp));
        AppendKeyValue(builder, "last_sample_local_time", FormatTimestamp(history.LastSampleTimestamp));
        if (!string.IsNullOrWhiteSpace(history.StatusMessage))
            AppendKeyValue(builder, "latest_status", history.StatusMessage);

        AppendLine(builder, "");
        AppendLine(builder, "## Reading Notes For LLM Analysis");
        AppendLine(builder, "- Timings are GPU timestamp durations in milliseconds.");
        AppendLine(builder, "- render_thread_ms is wall-clock CPU render-thread time for the same frame; render_thread_unattributed_ms is usually present/swap wait, driver blocking, OS/window events, or work not yet covered by named CPU phase timers.");
        AppendLine(builder, "- Parent rows are inclusive. User-authored GPU timer scopes can overlap child command timings, so do not sum parent and child rows as exclusive cost.");
        AppendLine(builder, "- Potential shader/material rows are name-based hints. If command names do not include asset names, inspect the owning render command path first.");
        AppendLine(builder, "- Warmup-excluded sections remove the first captured frames so shader/program linking spikes do not hide steady-state pipeline cost.");
        AppendLine(builder, "- Start with the worst frame table, then compare render-thread gaps, slow-node rankings, shader/material hints, and the all-node aggregate table.");

        AppendFrameStatistics(builder, history);
        AppendWarmupExcludedStatistics(builder, history);
        AppendRenderThreadCpuPhaseAggregates(builder, history);
        AppendWorstFrames(builder, history);
        AppendNodeRankings(builder, history);
        AppendAllNodeAggregates(builder, history);
        AppendFrameSampleCsv(builder, history);

        return builder.ToString();
    }

    private static void AppendFrameStatistics(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## Frame Timing Summary");

        if (history.FrameSummaries.Count == 0)
        {
            AppendLine(builder, "No frame samples were retained.");
            return;
        }

        double[] sorted = new double[history.FrameSummaries.Count];
        for (int i = 0; i < history.FrameSummaries.Count; i++)
            sorted[i] = history.FrameSummaries[i].FrameMilliseconds;
        Array.Sort(sorted);

        double average = history.TotalFrameMilliseconds / history.FrameCount;
        double variance = Math.Max(0.0, (history.TotalFrameMillisecondsSquared / history.FrameCount) - (average * average));
        double standardDeviation = Math.Sqrt(variance);

        AppendKeyValue(builder, "min_ms", FormatMilliseconds(sorted[0]));
        AppendKeyValue(builder, "p50_ms", FormatMilliseconds(Percentile(sorted, 0.50)));
        AppendKeyValue(builder, "p90_ms", FormatMilliseconds(Percentile(sorted, 0.90)));
        AppendKeyValue(builder, "p95_ms", FormatMilliseconds(Percentile(sorted, 0.95)));
        AppendKeyValue(builder, "p99_ms", FormatMilliseconds(Percentile(sorted, 0.99)));
        AppendKeyValue(builder, "avg_ms", FormatMilliseconds(average));
        AppendKeyValue(builder, "stddev_ms", FormatMilliseconds(standardDeviation));
        AppendKeyValue(builder, "max_ms", FormatMilliseconds(sorted[^1]));
        AppendKeyValue(builder, "best_frame_id", history.MinFrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, "worst_frame_id", history.MaxFrameId.ToString(CultureInfo.InvariantCulture));

        AppendRenderThreadStatistics(builder, history.FrameSummaries);
    }

    private static void AppendWarmupExcludedStatistics(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## Warmup-Excluded Frame Timing Summary");

        AppendFrameStatisticsWindow(builder, "after_first_10_frames", history.FrameSummaries.Skip(10).ToArray());
        AppendFrameStatisticsWindow(builder, "after_first_50_frames", history.FrameSummaries.Skip(50).ToArray());

        int lastCount = Math.Min(300, history.FrameSummaries.Count);
        PipelineFrameSummary[] lastFrames = history.FrameSummaries
            .Skip(history.FrameSummaries.Count - lastCount)
            .ToArray();
        AppendFrameStatisticsWindow(builder, "last_300_captured_frames", lastFrames);
    }

    private static void AppendFrameStatisticsWindow(StringBuilder builder, string prefix, IReadOnlyList<PipelineFrameSummary> frames)
    {
        if (frames.Count == 0)
        {
            AppendKeyValue(builder, string.Concat(prefix, "_available"), "false");
            return;
        }

        double[] sorted = new double[frames.Count];
        double total = 0.0;
        double totalSquared = 0.0;
        double min = double.MaxValue;
        double max = 0.0;
        ulong minFrameId = 0UL;
        ulong maxFrameId = 0UL;
        for (int i = 0; i < frames.Count; i++)
        {
            double value = frames[i].FrameMilliseconds;
            sorted[i] = value;
            total += value;
            totalSquared += value * value;
            if (value < min)
            {
                min = value;
                minFrameId = frames[i].FrameId;
            }
            if (value >= max)
            {
                max = value;
                maxFrameId = frames[i].FrameId;
            }
        }

        Array.Sort(sorted);
        double average = total / frames.Count;
        double variance = Math.Max(0.0, (totalSquared / frames.Count) - (average * average));

        AppendKeyValue(builder, string.Concat(prefix, "_available"), "true");
        AppendKeyValue(builder, string.Concat(prefix, "_frames"), frames.Count.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, string.Concat(prefix, "_first_frame_id"), frames[0].FrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, string.Concat(prefix, "_last_frame_id"), frames[^1].FrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, string.Concat(prefix, "_min_ms"), FormatMilliseconds(sorted[0]));
        AppendKeyValue(builder, string.Concat(prefix, "_p50_ms"), FormatMilliseconds(Percentile(sorted, 0.50)));
        AppendKeyValue(builder, string.Concat(prefix, "_p90_ms"), FormatMilliseconds(Percentile(sorted, 0.90)));
        AppendKeyValue(builder, string.Concat(prefix, "_p95_ms"), FormatMilliseconds(Percentile(sorted, 0.95)));
        AppendKeyValue(builder, string.Concat(prefix, "_p99_ms"), FormatMilliseconds(Percentile(sorted, 0.99)));
        AppendKeyValue(builder, string.Concat(prefix, "_avg_ms"), FormatMilliseconds(average));
        AppendKeyValue(builder, string.Concat(prefix, "_stddev_ms"), FormatMilliseconds(Math.Sqrt(variance)));
        AppendKeyValue(builder, string.Concat(prefix, "_max_ms"), FormatMilliseconds(sorted[^1]));
        AppendKeyValue(builder, string.Concat(prefix, "_best_frame_id"), minFrameId.ToString(CultureInfo.InvariantCulture));
        AppendKeyValue(builder, string.Concat(prefix, "_worst_frame_id"), maxFrameId.ToString(CultureInfo.InvariantCulture));
        AppendRenderThreadStatistics(builder, frames, string.Concat(prefix, "_"));
    }

    private static void AppendRenderThreadStatistics(
        StringBuilder builder,
        IReadOnlyList<PipelineFrameSummary> frames,
        string prefix = "")
    {
        PipelineFrameSummary[] renderThreadFrames = frames
            .Where(static frame => frame.RenderThreadMilliseconds > 0.0)
            .ToArray();

        if (renderThreadFrames.Length == 0)
        {
            AppendKeyValue(builder, string.Concat(prefix, "render_thread_available"), "false");
            return;
        }

        double[] sortedRenderThread = new double[renderThreadFrames.Length];
        double[] sortedGap = new double[renderThreadFrames.Length];
        double totalRenderThread = 0.0;
        double totalGap = 0.0;
        double maxGap = 0.0;
        ulong maxGapFrameId = 0UL;
        for (int i = 0; i < renderThreadFrames.Length; i++)
        {
            PipelineFrameSummary frame = renderThreadFrames[i];
            double gap = Math.Max(0.0, frame.RenderThreadMilliseconds - frame.FrameMilliseconds);
            sortedRenderThread[i] = frame.RenderThreadMilliseconds;
            sortedGap[i] = gap;
            totalRenderThread += frame.RenderThreadMilliseconds;
            totalGap += gap;
            if (gap >= maxGap)
            {
                maxGap = gap;
                maxGapFrameId = frame.FrameId;
            }
        }

        Array.Sort(sortedRenderThread);
        Array.Sort(sortedGap);

        AppendKeyValue(builder, string.Concat(prefix, "render_thread_available"), "true");
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_avg_ms"), FormatMilliseconds(totalRenderThread / renderThreadFrames.Length));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_p95_ms"), FormatMilliseconds(Percentile(sortedRenderThread, 0.95)));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_max_ms"), FormatMilliseconds(sortedRenderThread[^1]));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_minus_pipeline_avg_ms"), FormatMilliseconds(totalGap / renderThreadFrames.Length));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_minus_pipeline_p95_ms"), FormatMilliseconds(Percentile(sortedGap, 0.95)));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_minus_pipeline_max_ms"), FormatMilliseconds(sortedGap[^1]));
        AppendKeyValue(builder, string.Concat(prefix, "render_thread_minus_pipeline_worst_frame_id"), maxGapFrameId.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendWorstFrames(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## Worst Frames");
        AppendLine(builder, "| rank | frame_id | local_time | pipeline_ms | render_thread_ms | render_thread_minus_pipeline_ms | render_thread_unattributed_ms | timer_samples | hottest_path | hottest_path_ms |");
        AppendLine(builder, "| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |");

        for (int i = 0; i < history.WorstFrames.Count; i++)
        {
            PipelineWorstFrame frame = history.WorstFrames[i];
            string hottestPath = GetHottestGpuPipelinePath(frame.Root, out double hottestMilliseconds);
            AppendLine(
                builder,
                string.Concat(
                    "| ", (i + 1).ToString(CultureInfo.InvariantCulture),
                    " | ", frame.FrameId.ToString(CultureInfo.InvariantCulture),
                    " | ", EscapeTableCell(FormatTimestamp(frame.Timestamp)),
                    " | ", FormatMilliseconds(frame.FrameMilliseconds),
                    " | ", FormatMilliseconds(frame.RenderThreadMilliseconds),
                    " | ", FormatMilliseconds(Math.Max(0.0, frame.RenderThreadMilliseconds - frame.FrameMilliseconds)),
                    " | ", FormatMilliseconds(frame.RenderThreadUnattributedMilliseconds),
                    " | ", frame.SampleCount.ToString(CultureInfo.InvariantCulture),
                    " | ", EscapeTableCell(hottestPath),
                    " | ", FormatMilliseconds(hottestMilliseconds),
                    " |"));
        }

        AppendLine(builder, "");
        AppendLine(builder, "## Worst Frame Trees");
        for (int i = 0; i < history.WorstFrames.Count; i++)
        {
            PipelineWorstFrame frame = history.WorstFrames[i];
            AppendLine(builder, "");
            AppendLine(builder, string.Concat("### Worst Frame ", (i + 1).ToString(CultureInfo.InvariantCulture)));
            AppendKeyValue(builder, "frame_id", frame.FrameId.ToString(CultureInfo.InvariantCulture));
            AppendKeyValue(builder, "local_time", FormatTimestamp(frame.Timestamp));
            AppendKeyValue(builder, "pipeline_ms", FormatMilliseconds(frame.FrameMilliseconds));
            AppendKeyValue(builder, "render_thread_ms", FormatMilliseconds(frame.RenderThreadMilliseconds));
            AppendKeyValue(builder, "render_thread_minus_pipeline_ms", FormatMilliseconds(Math.Max(0.0, frame.RenderThreadMilliseconds - frame.FrameMilliseconds)));
            AppendKeyValue(builder, "render_thread_unattributed_ms", FormatMilliseconds(frame.RenderThreadUnattributedMilliseconds));
            AppendGpuPipelineTree(builder, frame.Root, frame.FrameMilliseconds, depth: 0);
        }
    }

    private static void AppendNodeRankings(StringBuilder builder, PipelineTimingHistory history)
    {
        PipelineNodeTimingStats[] nodes = history.NodesByPath.Values
            .Where(static node => node.Depth > 0)
            .ToArray();

        AppendRankedNodes(
            builder,
            "## Slow Nodes By Worst Single Frame",
            nodes.OrderByDescending(static node => node.MaxMilliseconds)
                .ThenByDescending(static node => node.AverageFrameMilliseconds)
                .Take(TimingDumpTopNodeLimit));

        AppendRankedNodes(
            builder,
            "## Slow Nodes By Average Active Frame",
            nodes.OrderByDescending(static node => node.AverageFrameMilliseconds)
                .ThenByDescending(static node => node.MaxMilliseconds)
                .Take(TimingDumpTopNodeLimit));

        AppendRankedNodes(
            builder,
            "## Slow Nodes By Total Captured Time",
            nodes.OrderByDescending(static node => node.TotalMilliseconds)
                .ThenByDescending(static node => node.MaxMilliseconds)
                .Take(TimingDumpTopNodeLimit));

        PipelineNodeTimingStats[] shaderNodes = nodes
            .Where(static node => node.IsPotentialShaderOrMaterialNode)
            .OrderByDescending(static node => node.MaxMilliseconds)
            .ThenByDescending(static node => node.AverageFrameMilliseconds)
            .Take(TimingDumpTopShaderNodeLimit)
            .ToArray();

        AppendLine(builder, "");
        AppendLine(builder, "## Potential Shader Or Material Hot Spots");
        if (shaderNodes.Length == 0)
        {
            AppendLine(builder, "No node names matched shader/material hints in the retained timing history.");
            return;
        }

        AppendNodeTable(builder, shaderNodes);
    }

    private static void AppendRankedNodes(
        StringBuilder builder,
        string heading,
        IEnumerable<PipelineNodeTimingStats> rankedNodes)
    {
        AppendLine(builder, "");
        AppendLine(builder, heading);
        AppendNodeTable(builder, rankedNodes);
    }

    private static void AppendAllNodeAggregates(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## All Node Aggregates");
        AppendNodeTable(
            builder,
            history.NodesByPath.Values.OrderBy(static node => node.Path, StringComparer.Ordinal));
    }

    private static void AppendRenderThreadCpuPhaseAggregates(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## Render Thread CPU Phase Aggregates");
        if (history.RenderThreadNodesByPath.Count == 0)
        {
            AppendLine(builder, "No named render-thread CPU phases were captured.");
            return;
        }

        AppendNodeTable(
            builder,
            history.RenderThreadNodesByPath.Values
                .OrderBy(static node => node.Depth)
                .ThenByDescending(static node => node.TotalMilliseconds));
    }

    private static void AppendNodeTable(StringBuilder builder, IEnumerable<PipelineNodeTimingStats> nodes)
    {
        AppendLine(builder, "| path | kind | frames | timer_samples | total_ms | avg_active_frame_ms | avg_timer_sample_ms | min_ms | max_ms | worst_frame_id | last_ms | last_frame_id |");
        AppendLine(builder, "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (PipelineNodeTimingStats node in nodes)
        {
            string kind = node.IsPotentialShaderOrMaterialNode ? "shader/material-hint" : "command/scope";
            AppendLine(
                builder,
                string.Concat(
                    "| ", EscapeTableCell(node.Path),
                    " | ", kind,
                    " | ", node.ObservedFrameCount.ToString(CultureInfo.InvariantCulture),
                    " | ", node.TotalSampleCount.ToString(CultureInfo.InvariantCulture),
                    " | ", FormatMilliseconds(node.TotalMilliseconds),
                    " | ", FormatMilliseconds(node.AverageFrameMilliseconds),
                    " | ", FormatMilliseconds(node.AverageTimerSampleMilliseconds),
                    " | ", FormatMilliseconds(node.MinMilliseconds == double.MaxValue ? 0.0 : node.MinMilliseconds),
                    " | ", FormatMilliseconds(node.MaxMilliseconds),
                    " | ", node.WorstFrameId.ToString(CultureInfo.InvariantCulture),
                    " | ", FormatMilliseconds(node.LastMilliseconds),
                    " | ", node.LastFrameId.ToString(CultureInfo.InvariantCulture),
                    " |"));
        }
    }

    private static void AppendFrameSampleCsv(StringBuilder builder, PipelineTimingHistory history)
    {
        AppendLine(builder, "");
        AppendLine(builder, "## Frame Samples CSV");
        AppendLine(builder, "frame_id,local_time,pipeline_ms,render_thread_ms,render_thread_minus_pipeline_ms,render_thread_unattributed_ms,timer_samples");
        for (int i = 0; i < history.FrameSummaries.Count; i++)
        {
            PipelineFrameSummary frame = history.FrameSummaries[i];
            AppendLine(
                builder,
                string.Concat(
                    frame.FrameId.ToString(CultureInfo.InvariantCulture),
                    ",", FormatTimestamp(frame.Timestamp),
                    ",", FormatMilliseconds(frame.FrameMilliseconds),
                    ",", FormatMilliseconds(frame.RenderThreadMilliseconds),
                    ",", FormatMilliseconds(Math.Max(0.0, frame.RenderThreadMilliseconds - frame.FrameMilliseconds)),
                    ",", FormatMilliseconds(frame.RenderThreadUnattributedMilliseconds),
                    ",", frame.SampleCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AppendGpuPipelineTree(
        StringBuilder builder,
        GpuPipelineTimingNodeData node,
        double frameMilliseconds,
        int depth)
    {
        string indent = new(' ', depth * 2);
        double percent = frameMilliseconds <= 0.0001 ? 0.0 : (node.ElapsedMs / frameMilliseconds) * 100.0;
        AppendLine(
            builder,
            string.Concat(
                indent,
                "- ",
                node.Name,
                " | ms=",
                FormatMilliseconds(node.ElapsedMs),
                " | frame_percent=",
                percent.ToString("0.0", CultureInfo.InvariantCulture),
                " | samples=",
                node.SampleCount.ToString(CultureInfo.InvariantCulture)));

        GpuPipelineTimingNodeData[] children = node.Children ?? [];
        if (children.Length == 0)
            return;

        GpuPipelineTimingNodeData[] ordered = new GpuPipelineTimingNodeData[children.Length];
        Array.Copy(children, ordered, children.Length);
        Array.Sort(ordered, static (left, right) => right.ElapsedMs.CompareTo(left.ElapsedMs));
        for (int i = 0; i < ordered.Length; i++)
            AppendGpuPipelineTree(builder, ordered[i], frameMilliseconds, depth + 1);
    }

    private static string GetHottestGpuPipelinePath(GpuPipelineTimingNodeData root, out double hottestMilliseconds)
    {
        List<string> parts = [root.Name];
        GpuPipelineTimingNodeData current = root;
        hottestMilliseconds = current.ElapsedMs;

        while (current.Children is { Length: > 0 })
        {
            GpuPipelineTimingNodeData best = current.Children[0];
            for (int i = 1; i < current.Children.Length; i++)
            {
                if (current.Children[i].ElapsedMs > best.ElapsedMs)
                    best = current.Children[i];
            }

            parts.Add(best.Name);
            current = best;
            hottestMilliseconds = current.ElapsedMs;
        }

        return string.Join(" > ", parts);
    }

    private static bool IsPotentialShaderOrMaterialNode(string path)
    {
        return path.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("material", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("program", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("render meshes", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("renderpasses", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("tonemap", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("bloom", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("blur", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("smaa", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ao", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("upscale", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("shadow", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("deferred", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("forward", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("post", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".fs", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".vs", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".gs", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(".comp", StringComparison.OrdinalIgnoreCase);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0.0;

        if (sortedValues.Length == 1)
            return sortedValues[0];

        double position = (sortedValues.Length - 1) * Math.Clamp(percentile, 0.0, 1.0);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sortedValues[lower];

        double fraction = position - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static void AppendKeyValue(StringBuilder builder, string key, string value)
        => AppendLine(builder, string.Concat(key, ": ", value));

    private static void AppendLine(StringBuilder builder, string value)
        => builder.AppendLine(value);

    private static string FormatMilliseconds(double milliseconds)
        => milliseconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static double StopwatchTicksToMilliseconds(long ticks)
        => ticks <= 0L ? 0.0 : (double)ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string EscapeTableCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('|', '/');
    }

    private static string NormalizeDumpNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        char[] buffer = new char[value.Length];
        int length = 0;
        bool lastWasSeparator = false;

        foreach (char rawChar in value)
        {
            char ch = char.ToLowerInvariant(rawChar);
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                buffer[length++] = ch;
                lastWasSeparator = false;
                continue;
            }

            if (lastWasSeparator)
                continue;

            buffer[length++] = '-';
            lastWasSeparator = true;
        }

        while (length > 0 && buffer[length - 1] == '-')
            length--;

        int start = 0;
        while (start < length && buffer[start] == '-')
            start++;

        return start >= length ? "unknown" : new string(buffer, start, length - start);
    }

    private static double GetRenderThreadAttributedMilliseconds(FrameCapture frame)
    {
        NodeAccumulator? root = frame.RenderThreadTimingRoot;
        if (root is null || root.Children.Count == 0)
            return 0.0;

        double total = 0.0;
        for (int i = 0; i < root.Children.Count; i++)
            total += root.Children[i].TotalNanoseconds / 1_000_000.0;

        return total;
    }

    private static double GetRenderThreadUnattributedMilliseconds(FrameCapture frame)
        => Math.Max(0.0, frame.RenderThreadMilliseconds - GetRenderThreadAttributedMilliseconds(frame));

    private static RenderStatsGpuPipelineSnapshot CreateSnapshot(FrameCapture frame)
    {
        bool hasRenderThread = frame.RenderThreadMilliseconds > 0.0;
        int rootCount = frame.Roots.Count + (hasRenderThread ? 1 : 0);
        GpuPipelineTimingNodeData[] roots = new GpuPipelineTimingNodeData[rootCount];
        double frameMilliseconds = 0.0;
        int writeIndex = 0;
        if (hasRenderThread)
        {
            roots[writeIndex++] = CreateRenderThreadRootPacket(frame);
            frameMilliseconds = frame.RenderThreadMilliseconds;
        }
        for (int i = 0; i < frame.Roots.Count; i++)
        {
            GpuPipelineTimingNodeData node = ToPacket(frame.Roots[i]);
            roots[writeIndex++] = node;
            if (!hasRenderThread)
                frameMilliseconds += node.ElapsedMs;
        }

        string statusMessage = frame.PendingSamples switch
        {
            > 0 when frame.SkippedSamples > 0 => $"{frame.PendingSamples} GPU timer sample(s) were still pending and {frame.SkippedSamples} were skipped; showing resolved commands.",
            > 0 => $"{frame.PendingSamples} GPU timer sample(s) were still pending; showing resolved commands.",
            _ when frame.SkippedSamples > 0 => string.IsNullOrWhiteSpace(frame.StatusMessage)
                ? $"{frame.SkippedSamples} GPU timer sample(s) were skipped."
                : $"{frame.StatusMessage} ({frame.SkippedSamples} skipped)",
            _ => frame.StatusMessage,
        };

        return new RenderStatsGpuPipelineSnapshot(true, frame.Supported, rootCount > 0, frame.BackendName, statusMessage, frameMilliseconds, roots);
    }

    private static GpuPipelineTimingNodeData CreateRenderThreadRootPacket(FrameCapture frame)
    {
        NodeAccumulator? root = frame.RenderThreadTimingRoot;
        int childCount = root?.Children.Count ?? 0;
        double unattributedMilliseconds = GetRenderThreadUnattributedMilliseconds(frame);
        bool includeUnattributed = unattributedMilliseconds > 0.001;
        GpuPipelineTimingNodeData[] children = new GpuPipelineTimingNodeData[childCount + (includeUnattributed ? 1 : 0)];

        if (root is not null)
        {
            for (int i = 0; i < root.Children.Count; i++)
                children[i] = ToPacket(root.Children[i]);
        }

        if (includeUnattributed)
        {
            children[^1] = new GpuPipelineTimingNodeData
            {
                Name = "Unattributed / Present Wait",
                ElapsedMs = unattributedMilliseconds,
                SampleCount = 1,
                Children = [],
            };
        }

        return new GpuPipelineTimingNodeData
        {
            Name = RenderThreadRootName,
            ElapsedMs = frame.RenderThreadMilliseconds,
            SampleCount = 1,
            Children = children,
        };
    }

    private static GpuPipelineTimingNodeData ToPacket(NodeAccumulator node)
    {
        GpuPipelineTimingNodeData[] children = new GpuPipelineTimingNodeData[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
            children[i] = ToPacket(node.Children[i]);

        return new GpuPipelineTimingNodeData
        {
            Name = node.Name,
            ElapsedMs = node.TotalNanoseconds / 1_000_000.0,
            SampleCount = node.SampleCount,
            Children = children,
        };
    }

    internal readonly struct RenderStatsGpuPipelineSnapshot(
        bool enabled,
        bool supported,
        bool ready,
        string backendName,
        string statusMessage,
        double frameMilliseconds,
        GpuPipelineTimingNodeData[] roots)
    {
        public bool Enabled { get; } = enabled;
        public bool Supported { get; } = supported;
        public bool Ready { get; } = ready;
        public string BackendName { get; } = backendName;
        public string StatusMessage { get; } = statusMessage;
        public double FrameMilliseconds { get; } = frameMilliseconds;
        public GpuPipelineTimingNodeData[] Roots { get; } = roots;

        public static RenderStatsGpuPipelineSnapshot Disabled()
            => new(false, true, false, string.Empty, "GPU render-pipeline command timing is disabled.", 0.0, []);

        public static RenderStatsGpuPipelineSnapshot Pending(string backendName, string statusMessage)
            => new(true, true, false, backendName, statusMessage, 0.0, []);

        public static RenderStatsGpuPipelineSnapshot Unsupported(string backendName, string statusMessage)
            => new(true, false, false, backendName, statusMessage, 0.0, []);
    }
}
