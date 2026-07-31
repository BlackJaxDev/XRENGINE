using NUnit.Framework;
using Shouldly;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class SessionRunLeaseManagerTests
{
    [Test]
    public async Task ReadOnlyRunsOverlapAndMutationsSerialize()
    {
        var manager = new SessionRunLeaseManager();
        await using AgentSessionLease firstRead = await manager.AcquireAsync(
            "session",
            mutation: false,
            CancellationToken.None);
        await using AgentSessionLease secondRead = await manager.AcquireAsync(
            "session",
            mutation: false,
            CancellationToken.None);

        Task<AgentSessionLease> mutationTask = manager.AcquireAsync(
            "session",
            mutation: true,
            CancellationToken.None);
        mutationTask.IsCompleted.ShouldBeFalse();

        await firstRead.DisposeAsync();
        await secondRead.DisposeAsync();
        await using AgentSessionLease firstMutation = await mutationTask.WaitAsync(TimeSpan.FromSeconds(1));
        Task<AgentSessionLease> secondMutationTask = manager.AcquireAsync(
            "session",
            mutation: true,
            CancellationToken.None);
        secondMutationTask.IsCompleted.ShouldBeFalse();

        await firstMutation.DisposeAsync();
        await using AgentSessionLease secondMutation =
            await secondMutationTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
