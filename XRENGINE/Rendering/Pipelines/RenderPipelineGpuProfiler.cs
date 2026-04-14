using System;
using System.Collections.Generic;
using XREngine.Data.Profiling;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

internal sealed class RenderPipelineGpuProfiler
{
    public static RenderPipelineGpuProfiler Instance { get; } = new();

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
        public bool HasSamples { get; private set; }
        public bool Supported { get; set; }
        public string BackendName { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
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
    }

    private readonly struct PendingScope(ulong frameId, string[] path, GLRenderQuery startQuery, GLRenderQuery endQuery)
    {
        public ulong FrameId { get; } = frameId;
        public string[] Path { get; } = path;
        public GLRenderQuery StartQuery { get; } = startQuery;
        public GLRenderQuery EndQuery { get; } = endQuery;
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
    private static List<string>? _userScopeStack;
    [ThreadStatic]
    private static Stack<UserScopeHandle>? _openUserScopes;

    private readonly object _lock = new();
    private readonly Queue<XRRenderQuery> _queryPool = new();
    private readonly Dictionary<ulong, FrameCapture> _frames = [];
    private readonly List<PendingScope> _pendingScopes = [];
    private RenderStatsGpuPipelineSnapshot _latestSnapshot = RenderStatsGpuPipelineSnapshot.Disabled();

    private RenderPipelineGpuProfiler()
    {
    }

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

    public void BeginFrame(ulong frameId, bool enabled)
    {
        lock (_lock)
        {
            if (!enabled)
            {
                ClearPendingScopesNoLock();
                _frames.Clear();
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Disabled();
                return;
            }

            ResolveCompletedScopesNoLock();
            PublishLatestResolvedFrameNoLock(frameId);
            PruneFramesNoLock(frameId);
        }
    }

    public Scope StartScope(ViewportRenderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling || !Engine.Rendering.Stats.EnableTracking)
            return default;

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Unsupported(GetBackendName(), "GPU render-pipeline command timing is currently available on OpenGL.");
            return default;
        }

        XRRenderPipelineInstance instance = ViewportRenderCommand.ActivePipelineInstance;
        ulong frameId = Engine.Rendering.State.RenderFrameId;
        string pipelineName = instance.DebugDescriptor;
        string commandName = command.GpuProfilingName;

        List<string> commandStack = _commandScopeStack ??= [];
        List<string> userStack = _userScopeStack ??= [];
        string[] path = new string[userStack.Count + commandStack.Count + 2];
        path[0] = pipelineName;
        int pathIndex = 1;
        for (int i = 0; i < userStack.Count; i++)
            path[pathIndex++] = userStack[i];
        for (int i = 0; i < commandStack.Count; i++)
            path[pathIndex++] = commandStack[i];
        path[^1] = commandName;

        GLRenderQuery startQuery = AcquireTimestampQuery(renderer);
        startQuery.Data.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        startQuery.QueryCounter();

        commandStack.Add(commandName);

        lock (_lock)
        {
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.Supported = true;
            frame.BackendName = "OpenGL";
            frame.StatusMessage = string.Empty;
            frame.PendingSamples++;
        }

        return new Scope(this, frameId, path, startQuery, popScope: true);
    }

    public bool PushUserScope(string scopeName)
    {
        if (string.IsNullOrWhiteSpace(scopeName) ||
            !Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling ||
            !Engine.Rendering.Stats.EnableTracking)
        {
            return false;
        }

        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
        {
            lock (_lock)
                _latestSnapshot = RenderStatsGpuPipelineSnapshot.Unsupported(GetBackendName(), "GPU render-pipeline command timing is currently available on OpenGL.");
            return false;
        }

        XRRenderPipelineInstance instance = ViewportRenderCommand.ActivePipelineInstance;
        ulong frameId = Engine.Rendering.State.RenderFrameId;
        string pipelineName = instance.DebugDescriptor;

        List<string> userStack = _userScopeStack ??= [];
        Stack<UserScopeHandle> openScopes = _openUserScopes ??= [];
        string[] path = new string[userStack.Count + 2];
        path[0] = pipelineName;
        for (int i = 0; i < userStack.Count; i++)
            path[i + 1] = userStack[i];
        path[^1] = scopeName;

        GLRenderQuery startQuery = AcquireTimestampQuery(renderer);
        startQuery.Data.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        startQuery.QueryCounter();

        userStack.Add(scopeName);
        openScopes.Push(new UserScopeHandle(frameId, path, startQuery));

        lock (_lock)
        {
            FrameCapture frame = GetOrCreateFrameNoLock(frameId);
            frame.Supported = true;
            frame.BackendName = "OpenGL";
            frame.StatusMessage = string.Empty;
            frame.PendingSamples++;
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
                ReleaseQueryNoLock(scope.StartQuery);
                CancelPendingSampleNoLock(scope.FrameId);
            }
            return;
        }

        GLRenderQuery endQuery = AcquireTimestampQuery(renderer);
        endQuery.Data.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        endQuery.QueryCounter();

        lock (_lock)
            _pendingScopes.Add(new PendingScope(scope.FrameId, scope.Path, scope.StartQuery, endQuery));
    }

    private void EndScope(ulong frameId, string[]? path, GLRenderQuery? startQuery, bool popScope)
    {
        if (popScope)
        {
            List<string>? stack = _commandScopeStack;
            if (stack is { Count: > 0 })
                stack.RemoveAt(stack.Count - 1);
        }

        if (path is null || startQuery is null || AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        GLRenderQuery endQuery = AcquireTimestampQuery(renderer);
        endQuery.Data.CurrentQuery = Data.Rendering.EQueryTarget.Timestamp;
        endQuery.QueryCounter();

        lock (_lock)
            _pendingScopes.Add(new PendingScope(frameId, path, startQuery, endQuery));
    }

    private FrameCapture GetOrCreateFrameNoLock(ulong frameId)
    {
        if (_frames.TryGetValue(frameId, out FrameCapture? existing))
            return existing;

        FrameCapture created = new(frameId);
        _frames.Add(frameId, created);
        return created;
    }

    private void ResolveCompletedScopesNoLock()
    {
        for (int i = _pendingScopes.Count - 1; i >= 0; i--)
        {
            PendingScope pending = _pendingScopes[i];
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
        FrameCapture frame = GetOrCreateFrameNoLock(frameId);
        if (frame.PendingSamples > 0)
            frame.PendingSamples--;
    }

    private void PublishLatestResolvedFrameNoLock(ulong currentFrameId)
    {
        FrameCapture? best = null;

        foreach ((ulong frameId, FrameCapture frame) in _frames)
        {
            if (frameId >= currentFrameId || frame.PendingSamples > 0)
                continue;

            if (!frame.HasSamples && !frame.Supported)
                continue;

            if (best is null || frameId > best.FrameId)
                best = frame;
        }

        if (best is null)
            return;

        _latestSnapshot = CreateSnapshot(best);

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
            if (currentFrameId <= frameId + 8 || frame.PendingSamples > 0)
                continue;

            stale ??= [];
            stale.Add(frameId);
        }

        if (stale is null)
            return;

        for (int i = 0; i < stale.Count; i++)
            _frames.Remove(stale[i]);
    }

    private void ClearPendingScopesNoLock()
    {
        for (int i = 0; i < _pendingScopes.Count; i++)
        {
            ReleaseQueryNoLock(_pendingScopes[i].StartQuery);
            ReleaseQueryNoLock(_pendingScopes[i].EndQuery);
        }

        _pendingScopes.Clear();
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

    private static bool TryReadTimestamp(GLRenderQuery query, out ulong timestamp)
    {
        timestamp = 0UL;
        long available = query.GetQueryObject(EGetQueryObject.QueryResultAvailable);
        if (available == 0)
            return false;

        timestamp = (ulong)query.GetQueryObject(EGetQueryObject.QueryResult);
        return true;
    }

    private static string GetBackendName()
        => AbstractRenderer.Current switch
        {
            OpenGLRenderer => "OpenGL",
            Vulkan.VulkanRenderer => "Vulkan",
            _ => "Unknown"
        };

    private static RenderStatsGpuPipelineSnapshot CreateSnapshot(FrameCapture frame)
    {
        GpuPipelineTimingNodeData[] roots = new GpuPipelineTimingNodeData[frame.Roots.Count];
        double frameMilliseconds = 0.0;
        for (int i = 0; i < frame.Roots.Count; i++)
        {
            GpuPipelineTimingNodeData node = ToPacket(frame.Roots[i]);
            roots[i] = node;
            frameMilliseconds += node.ElapsedMs;
        }

        return new RenderStatsGpuPipelineSnapshot(true, frame.Supported, roots.Length > 0, frame.BackendName, frame.StatusMessage, frameMilliseconds, roots);
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

        public static RenderStatsGpuPipelineSnapshot Unsupported(string backendName, string statusMessage)
            => new(true, false, false, backendName, statusMessage, 0.0, []);
    }
}