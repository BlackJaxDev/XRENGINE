using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

/// <summary>
/// Checks that the MCP server state refresh applies only settled preferences and loses no request.
/// </summary>
[TestFixture]
public sealed class McpServerStateRefreshSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static void WaitUntil(Func<bool> condition)
        => SpinWait.SpinUntil(condition, Timeout).ShouldBeTrue();

    [Test]
    public void RequestDuringApply_IsAppliedWithTheLatestValue()
    {
        bool enabled = false;
        var applied = new ConcurrentQueue<bool>();
        McpServerStateRefreshScheduler? scheduler = null;
        scheduler = new McpServerStateRefreshScheduler(
            isComposing: () => false,
            applyServerState: () =>
            {
                applied.Enqueue(enabled);
                // A change arrives after this pass has read the preferences.
                if (applied.Count == 1)
                {
                    enabled = true;
                    scheduler!.Request();
                }
            },
            settleDelay: () => Task.CompletedTask,
            reportError: ex => throw ex);

        scheduler.Request();

        WaitUntil(() => !scheduler.IsPending && applied.Count >= 2);
        applied.Last().ShouldBeTrue();
    }

    [Test]
    public void Composition_DefersApplyUntilTheComposedRequest()
    {
        bool composing = true;
        bool enabled = false; // The global value before the session value is applied.
        var applied = new ConcurrentQueue<bool>();
        var scheduler = new McpServerStateRefreshScheduler(
            isComposing: () => Volatile.Read(ref composing),
            applyServerState: () => applied.Enqueue(enabled),
            settleDelay: () => Task.CompletedTask,
            reportError: ex => throw ex);

        // A property change during the composition requests a refresh.
        scheduler.Request();
        WaitUntil(() => !scheduler.IsPending);
        applied.ShouldBeEmpty();

        // The composition ends with the session value, and the composed event requests a refresh.
        enabled = true;
        Volatile.Write(ref composing, false);
        scheduler.Request();
        WaitUntil(() => !scheduler.IsPending && !applied.IsEmpty);
        applied.ShouldBe([true]);
    }

    [Test]
    public void RequestsWhileAPassWaits_CoalesceIntoOneMorePass()
    {
        var firstPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int delays = 0;
        int applies = 0;
        var scheduler = new McpServerStateRefreshScheduler(
            isComposing: () => false,
            applyServerState: () => Interlocked.Increment(ref applies),
            settleDelay: () => Interlocked.Increment(ref delays) == 1 ? firstPass.Task : Task.CompletedTask,
            reportError: ex => throw ex);

        scheduler.Request();
        WaitUntil(() => Volatile.Read(ref delays) == 1);
        for (int index = 0; index < 100; ++index)
            scheduler.Request();
        firstPass.SetResult();

        WaitUntil(() => !scheduler.IsPending && Volatile.Read(ref applies) >= 2);
        Volatile.Read(ref applies).ShouldBe(2);
    }

    [Test]
    public void ApplyFailure_IsReportedAndALaterRequestStillApplies()
    {
        int attempts = 0;
        var errors = new ConcurrentQueue<Exception>();
        var scheduler = new McpServerStateRefreshScheduler(
            isComposing: () => false,
            applyServerState: () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("listener start failed");
            },
            settleDelay: () => Task.CompletedTask,
            reportError: errors.Enqueue);

        scheduler.Request();
        WaitUntil(() => !scheduler.IsPending && !errors.IsEmpty);
        errors.Single().ShouldBeOfType<InvalidOperationException>();

        scheduler.Request();
        WaitUntil(() => !scheduler.IsPending && Volatile.Read(ref attempts) == 2);
    }
}
