using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
[NonParallelizable]
public class BrokerHistoryPersistenceTests
{
    private string _temporaryRoot = string.Empty;
    private string _apiKeyVariable = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"xrengine-broker-history-{Guid.NewGuid():N}");
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
    public async Task BlockedHistoryReplacementPreservesProviderResultAndRetriesAfterReaderCloses()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("The reproduced Windows file replacement denial requires Windows sharing semantics.");

        var handler = new HoldingResponsesHttpMessageHandler(1);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var store = new BrokerHistoryStore(new BrokerUiPaths(_temporaryRoot));
        TextWriter originalError = Console.Error;
        using var diagnostics = new StringWriter();
        TextWriter synchronizedDiagnostics = TextWriter.Synchronized(diagnostics);
        Console.SetError(synchronizedDiagnostics);
        try
        {
            await using var registry = new AgentRunRegistry(new BrokerConfiguration
            {
                RepositoryRoot = _temporaryRoot,
                ApiKeyEnvironmentVariable = _apiKeyVariable,
            }, httpClient);
            string runId = registry.Start(new AgentRunRequest
            {
                Objective = "Complete the independent fake run while its history file is held open.",
                RequestedModel = AgentModelCatalog.Sol61,
                ReasoningEffort = "max",
                Budget = new AgentRunBudget { MaxTurns = 1, MaxToolCalls = 0, MaxRetries = 0 },
            });
            await handler.AllEntered.WaitAsync(TimeSpan.FromSeconds(10));

            using (var reader = new FileStream(
                Path.Combine(store.Paths.RunsDirectory, $"{runId}.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                handler.Release();
                await WaitUntilAsync(() => registry.Get(runId).Status == AgentRunStatus.Completed
                    && DiagnosticText(diagnostics, synchronizedDiagnostics).Contains(runId));
                AgentRunSnapshot snapshot = registry.Get(runId);
                snapshot.Result!.FinalText.ShouldBe("fake complete");
                snapshot.Result.Failure.ShouldBeNull();
                snapshot.Result.TurnCount.ShouldBe(1);
                snapshot.ActualModel.ShouldBe(AgentModelCatalog.Sol61);
                snapshot.RequestedReasoningEffort.ShouldBe("max");
                snapshot.Usage.TotalTokens.ShouldBe(7);
                snapshot.ProviderAttempts.Single().TerminalStatus.ShouldBe("completed");
                store.LoadRecords().Single().Status.ShouldNotBe(AgentRunStatus.Completed);
            }

            await WaitUntilAsync(() => store.LoadRecords().Any(record => record.Status == AgentRunStatus.Completed));
            BrokerHistoryRecord saved = store.LoadRecords().Single();
            saved.ResponseText.ShouldBe("fake complete");
            saved.FailureSummary.ShouldBeEmpty();
            string warning = DiagnosticText(diagnostics, synchronizedDiagnostics);
            warning.ShouldContain("history write deferred");
            warning.ShouldMatch(@"(UnauthorizedAccessException|IOException), HResult 0x[0-9a-f]{8}, path");
            warning.ShouldContain($"{runId}.json");
            handler.RequestCount.ShouldBe(1);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Test]
    public async Task HistoryRefreshReadersAllowConcurrentAtomicRecordReplacement()
    {
        var store = new BrokerHistoryStore(new BrokerUiPaths(_temporaryRoot));
        var record = new BrokerHistoryRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            Status = AgentRunStatus.Running,
            ResponseText = new string('x', 131_072),
        };
        store.SaveRecord(record);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] operations = Enumerable.Range(0, 20).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            for (int iteration = 0; iteration < 20; iteration++)
            {
                if (worker == 0)
                    store.SaveRecord(record with { UpdatedUtc = DateTimeOffset.UtcNow });
                else
                {
                    // A transient replacement can omit a record for one refresh; any visible record is complete.
                    foreach (BrokerHistoryRecord loaded in store.LoadRecords())
                        loaded.ResponseText.Length.ShouldBe(record.ResponseText.Length);
                }
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));
        store.LoadRecords().Single().ResponseText.ShouldBe(record.ResponseText);
    }

    private static string DiagnosticText(StringWriter diagnostics, TextWriter synchronizedDiagnostics)
    {
        lock (synchronizedDiagnostics)
            return diagnostics.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
