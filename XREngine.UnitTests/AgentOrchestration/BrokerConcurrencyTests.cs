using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
[NonParallelizable]
public class BrokerConcurrencyTests
{
    private string _temporaryRoot = string.Empty;
    private string _apiKeyVariable = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"xrengine-broker-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        _apiKeyVariable = $"XRE_TEST_OPENAI_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(_apiKeyVariable, "test-key");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(_apiKeyVariable, null);
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    [Test]
    public async Task OneHundredIndependentRunsEnterTogetherAndCapacityRejectsTheNextRun()
    {
        var handler = new HoldingResponsesHttpMessageHandler(100);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var registry = new AgentRunRegistry(Configuration(100, 100), httpClient);
        string[] runIds = Enumerable.Range(0, 100).Select(_ => registry.Start(Request())).ToArray();
        await handler.AllEntered.WaitAsync(TimeSpan.FromSeconds(10));

        handler.RequestCount.ShouldBe(100);
        handler.PeakRequests.ShouldBe(100);
        registry.List(100).Count.ShouldBe(100);
        foreach (string runId in runIds)
            registry.Get(runId).Status.ShouldBe(AgentRunStatus.Running);
        Should.Throw<InvalidOperationException>(() => registry.Start(Request()))
            .Message.ShouldContain("full of active runs");

        handler.Release();
        await WaitForTerminalAsync(registry, runIds);
        foreach (string runId in runIds)
        {
            AgentRunSnapshot snapshot = registry.Get(runId);
            snapshot.Status.ShouldBe(AgentRunStatus.Completed);
            snapshot.ActualModel.ShouldBe(AgentModelCatalog.Sol61);
            snapshot.RequestedReasoningEffort.ShouldBe("max");
        }
        handler.RequestCount.ShouldBe(100);
    }

    [Test]
    public async Task ExcessAdmittedRunsStayQueuedAndCancelWithoutProviderRequests()
    {
        var handler = new HoldingResponsesHttpMessageHandler(20);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var registry = new AgentRunRegistry(Configuration(20, 22), httpClient);
        string[] runIds = Enumerable.Range(0, 22).Select(_ => registry.Start(Request())).ToArray();
        await handler.AllEntered.WaitAsync(TimeSpan.FromSeconds(10));

        string[] queued = runIds.Where(runId => registry.Get(runId).Status == AgentRunStatus.Queued).ToArray();
        queued.Length.ShouldBe(2);
        handler.RequestCount.ShouldBe(20);
        handler.PeakRequests.ShouldBe(20);
        foreach (string runId in queued)
            registry.Cancel(runId).ShouldBeTrue();
        await WaitForTerminalAsync(registry, queued);
        foreach (string runId in queued)
            registry.Get(runId).Status.ShouldBe(AgentRunStatus.Cancelled);
        handler.RequestCount.ShouldBe(20);

        handler.Release();
        await WaitForTerminalAsync(registry, runIds);
        registry.List(100).Count(item => item.Status == AgentRunStatus.Completed).ShouldBe(20);
        handler.RequestCount.ShouldBe(20);
        handler.PeakRequests.ShouldBe(20);
    }

    private BrokerConfiguration Configuration(int concurrency, int capacity)
        => new()
        {
            RepositoryRoot = _temporaryRoot,
            ApiKeyEnvironmentVariable = _apiKeyVariable,
            MaximumConcurrentRuns = concurrency,
            MaximumRetainedRuns = capacity,
        };

    private static AgentRunRequest Request()
        => new()
        {
            Objective = "Complete the independent fake concurrency run.",
            RequestedModel = AgentModelCatalog.Sol61,
            ReasoningEffort = "max",
            Budget = new AgentRunBudget { MaxTurns = 1, MaxToolCalls = 0, MaxRetries = 0 },
        };

    private static async Task WaitForTerminalAsync(AgentRunRegistry registry, IEnumerable<string> runIds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (runIds.Any(runId => registry.Get(runId).Status is AgentRunStatus.Queued or AgentRunStatus.Running))
            await Task.Delay(10, timeout.Token);
    }
}
