using System.Diagnostics;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

internal sealed partial class AgentRunRegistry
{
    private async Task<AgentRunResult> RunSwarmAsync(BrokerRunRecord record, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        var runner = new AgentSwarmRunner(_orchestrator, _globalConcurrency);
        AgentSwarmRunResult swarm = await runner.RunAsync(
            record.RunId, record.Request, record.Request.ContextFileSnapshots,
            (snapshot, _) =>
            {
                record.UpdateSwarm(snapshot);
                record.UpdateStatus($"Swarm: {snapshot.Nodes.Count} agents, {snapshot.Status}");
                _historyPublisher.QueueUpdate(record);
                return ValueTask.CompletedTask;
            }, cancellationToken);
        record.UpdateSwarm(swarm.Snapshot);
        AgentRunResult result = swarm.Aggregate;
        if (result.Status != AgentRunStatus.Completed)
            return result;

        // The runner owns execution's deadline. Only application gets a new timer,
        // using the remaining allowance so timeout is never mistaken for user cancellation.
        TimeSpan remaining = TimeSpan.FromSeconds(record.Request.Swarm!.MaxElapsedSeconds) - elapsed.Elapsed;
        if (cancellationToken.IsCancellationRequested || remaining <= TimeSpan.Zero)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            AgentRunStatus status = cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            record.UpdateSwarm(swarm.Snapshot with { Status = status });
            return result with
            {
                Status = status,
                Failure = new AgentFailure
                {
                    Category = cancelled ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded,
                    Summary = cancelled ? "The swarm was cancelled." : "The swarm elapsed-time limit was reached.",
                },
            };
        }
        record.SetCodeChanges(swarm.Changes);
        if (!record.Request.Swarm!.AutoApply)
            return result with { FinalText = result.FinalText + " Changes are ready for review; no source files were written." };

        record.UpdateStatus("Applying parent-approved changes");
        _historyPublisher.PublishNow(record);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        SwarmApplyResult application = new SwarmWorkspace(_repositoryPathPolicy).Apply(
            swarm.Changes, record.Request.ContextFileSnapshots, deadline.Token);
        record.SetAppliedPaths(application.AppliedPaths);
        AgentFailureCategory failureCategory = application.Cancelled
            ? cancellationToken.IsCancellationRequested ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded
            : AgentFailureCategory.Internal;
        AgentRunResult final = application.Success
            ? result with { FinalText = $"Applied and read back {application.AppliedPaths.Count} parent-approved changes. Build and runtime validation are still required." }
            : result with
            {
                Status = failureCategory == AgentFailureCategory.Cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                FinalText = $"Code review succeeded but application failed. Paths that may retain changes: {string.Join(", ", application.AppliedPaths)}.",
                Failure = new AgentFailure { Category = failureCategory, Summary = application.Failure ?? "Patch application failed." },
            };
        record.UpdateSwarm(swarm.Snapshot with { Status = final.Status });
        return final;
    }
}
