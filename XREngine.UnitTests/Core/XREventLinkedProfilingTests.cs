using System.Collections.Concurrent;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Data.Profiling;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class XREventLinkedProfilingTests
{
    [Test]
    public async Task InvokeAsync_PublishesLinkedListenersWithExplicitIdentity()
    {
        using ProfilerSession session = new();
        const string parentName = "XREvent.InvokeAsync.ValidationParent";
        int callerThreadId = Environment.CurrentManagedThreadId;
        ConcurrentBag<int> listenerThreadIds = [];
        using ManualResetEventSlim listenersReady = new(false);
        int listenerCount = 0;

        XREvent xrEvent = new();
        for (int i = 0; i < 4; i++)
        {
            xrEvent.AddListener(() =>
            {
                listenerThreadIds.Add(Environment.CurrentManagedThreadId);
                if (Interlocked.Increment(ref listenerCount) >= 2)
                    listenersReady.Set();
                listenersReady.Wait(TimeSpan.FromSeconds(2));
            });
        }

        using (Engine.Profiler.Start(parentName, ProfilerScopeKind.OneOffInvoke))
            await xrEvent.InvokeAsync();

        Engine.CodeProfiler.ProfilerNodeSnapshot parent = WaitForRoot(parentName);
        Engine.CodeProfiler.ProfilerNodeSnapshot invocation = FindNode(parent, "XREvent.InvokeAsync");
        Engine.CodeProfiler.ProfilerNodeSnapshot actions = FindNode(parent, "XREvent.AsyncActions");
        Engine.CodeProfiler.ProfilerNodeSnapshot[] listeners = actions.Children
            .Where(static node => node.Name.StartsWith("XREvent.AsyncAction[", StringComparison.Ordinal))
            .ToArray();

        listeners.Length.ShouldBe(4);
        listenerThreadIds.ShouldContain(threadId => threadId != callerThreadId);
        AssertLinkedListenerIdentity(parent, invocation, actions, listeners, callerThreadId);
    }

    [Test]
    public void InvokeParallel_PublishesLinkedListenersWithExplicitIdentity()
    {
        using ProfilerSession session = new();
        const string parentName = "XREvent.InvokeParallel.ValidationParent";
        int callerThreadId = Environment.CurrentManagedThreadId;
        ConcurrentBag<int> listenerThreadIds = [];
        using ManualResetEventSlim listenersReady = new(false);
        int listenerCount = 0;

        XREvent xrEvent = new();
        for (int i = 0; i < 4; i++)
        {
            xrEvent.AddListener(() =>
            {
                listenerThreadIds.Add(Environment.CurrentManagedThreadId);
                if (Interlocked.Increment(ref listenerCount) >= 2)
                    listenersReady.Set();
                listenersReady.Wait(TimeSpan.FromSeconds(2));
            });
        }

        using (Engine.Profiler.Start(parentName, ProfilerScopeKind.OneOffInvoke))
            xrEvent.InvokeParallel();

        Engine.CodeProfiler.ProfilerNodeSnapshot parent = WaitForRoot(parentName);
        Engine.CodeProfiler.ProfilerNodeSnapshot invocation = FindNode(parent, "XREvent.InvokeParallel");
        Engine.CodeProfiler.ProfilerNodeSnapshot actions = FindNode(parent, "XREvent.ParallelActions");
        Engine.CodeProfiler.ProfilerNodeSnapshot[] listeners = actions.Children
            .Where(static node => node.Name.StartsWith("XREvent.ParallelAction[", StringComparison.Ordinal))
            .ToArray();

        listeners.Length.ShouldBe(4);
        listenerThreadIds.ShouldContain(threadId => threadId != callerThreadId);
        AssertLinkedListenerIdentity(parent, invocation, actions, listeners, callerThreadId);
    }

    private static void AssertLinkedListenerIdentity(
        Engine.CodeProfiler.ProfilerNodeSnapshot parent,
        Engine.CodeProfiler.ProfilerNodeSnapshot invocation,
        Engine.CodeProfiler.ProfilerNodeSnapshot actions,
        Engine.CodeProfiler.ProfilerNodeSnapshot[] listeners,
        int callerThreadId)
    {
        parent.ParentScopeId.ShouldBe(0);
        parent.LogicalThreadId.ShouldBe(callerThreadId);
        invocation.ParentScopeId.ShouldBe(parent.ScopeId);
        actions.ParentScopeId.ShouldBe(invocation.ScopeId);
        actions.SelfMs.ShouldBe(actions.ElapsedMs);

        foreach (Engine.CodeProfiler.ProfilerNodeSnapshot listener in listeners)
        {
            listener.IsLinked.ShouldBeTrue();
            listener.IsComplete.ShouldBeTrue();
            listener.ParentScopeId.ShouldBe(actions.ScopeId);
            listener.SessionEpoch.ShouldBe(parent.SessionEpoch);
            listener.LogicalThreadId.ShouldBe(callerThreadId);
            listener.StartTicks.ShouldBeGreaterThanOrEqualTo(actions.StartTicks);
            listener.EndTicks.ShouldBeLessThanOrEqualTo(actions.EndTicks);
        }

        listeners.ShouldContain(listener => listener.ProducerThreadId != callerThreadId);
    }

    private static Engine.CodeProfiler.ProfilerNodeSnapshot WaitForRoot(string name)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        Engine.CodeProfiler.ProfilerFrameSnapshot? lastSnapshot = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Engine.Profiler.TryGetSnapshot(out Engine.CodeProfiler.ProfilerFrameSnapshot? snapshot, out _) &&
                snapshot is not null)
            {
                lastSnapshot = snapshot;
                foreach (Engine.CodeProfiler.ProfilerThreadSnapshot thread in snapshot.Threads)
                {
                    Engine.CodeProfiler.ProfilerNodeSnapshot? root = thread.RootNodes
                        .FirstOrDefault(node => string.Equals(node.Name, name, StringComparison.Ordinal));
                    if (root is not null)
                    {
                        snapshot.UnresolvedLinkedChildCount.ShouldBe(0);
                        return root;
                    }
                }
            }

            Thread.Sleep(10);
        }

        string roots = lastSnapshot is null
            ? "<no snapshot>"
            : string.Join(", ", lastSnapshot.Threads.SelectMany(static thread => thread.RootNodes).Select(static node => node.Name));
        throw new TimeoutException(
            $"Profiler did not publish root '{name}'. " +
            $"Epoch={Engine.Profiler.SessionEpoch}, Active={Engine.Profiler.ActiveScopeCount}, " +
            $"Queued={Engine.Profiler.QueuedCompletedScopeCount}, Pending={Engine.Profiler.PendingCompletedCount}, " +
            $"UnresolvedLinked={Engine.Profiler.UnresolvedLinkedChildCount}, " +
            $"Stale={Engine.Profiler.StaleCompletedDiscardedEventCount}, " +
            $"Overflow={Engine.Profiler.OverflowDiscardedEventCount}, " +
            $"PendingDiscarded={Engine.Profiler.PendingCompletedDiscardedEventCount}, Roots=[{roots}].");
    }

    private static Engine.CodeProfiler.ProfilerNodeSnapshot FindNode(
        Engine.CodeProfiler.ProfilerNodeSnapshot root,
        string name)
    {
        if (TryFindNode(root, name, out Engine.CodeProfiler.ProfilerNodeSnapshot? node))
            return node!;

        throw new InvalidOperationException($"Profiler node '{name}' was not found below '{root.Name}'.");
    }

    private static bool TryFindNode(
        Engine.CodeProfiler.ProfilerNodeSnapshot root,
        string name,
        out Engine.CodeProfiler.ProfilerNodeSnapshot? node)
    {
        if (string.Equals(root.Name, name, StringComparison.Ordinal))
        {
            node = root;
            return true;
        }

        foreach (Engine.CodeProfiler.ProfilerNodeSnapshot child in root.Children)
        {
            if (TryFindNode(child, name, out node))
                return true;
        }

        node = null;
        return false;
    }

    private sealed class ProfilerSession : IDisposable
    {
        private readonly bool _wasEnabled = Engine.Profiler.EnableFrameLogging;
        private readonly int _previousStatsIntervalMs = Engine.Profiler.StatsThreadIntervalMs;
        private readonly int _previousSnapshotIntervalMs = Engine.Profiler.SnapshotIntervalMs;

        public ProfilerSession()
        {
            Engine.Profiler.EnableFrameLogging = false;
            Engine.Profiler.StatsThreadIntervalMs = 1;
            Engine.Profiler.SnapshotIntervalMs = 100;
            Engine.Profiler.EnableFrameLogging = true;
        }

        public void Dispose()
        {
            Engine.Profiler.EnableFrameLogging = false;
            Engine.Profiler.StatsThreadIntervalMs = _previousStatsIntervalMs;
            Engine.Profiler.SnapshotIntervalMs = _previousSnapshotIntervalMs;
            Engine.Profiler.EnableFrameLogging = _wasEnabled;
        }
    }
}