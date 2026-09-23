using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AgentSwarmRunnerTests
{
    [Test]
    public async Task SemaphoreOneHierarchyMergesTwoLeafEditsAndUsesLunaMax()
    {
        var client = new DelegatingModelClient(request => request.Run.SystemInstructions.Contains("reviewer", StringComparison.Ordinal)
            ? Response("{\"approved\":true,\"summary\":\"looks good\"}")
            : request.Run.Objective switch
            {
                "root" => Response("{\"children\":[{\"objective\":\"alpha\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"},{\"objective\":\"beta\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"}]}"),
                "alpha" => Response("{\"path\":\"Source.cs\",\"base_sha256\":\"hash\",\"old_text\":\"alpha\",\"new_text\":\"ALPHA\"}"),
                "beta" => Response("{\"path\":\"Source.cs\",\"base_sha256\":\"hash\",\"old_text\":\"beta\",\"new_text\":\"BETA\"}"),
                _ => throw new InvalidOperationException("Unexpected swarm phase."),
            });
        AgentSwarmRunResult result = await RunAsync(client, new AgentSwarmOptions { AllowedPaths = ["Source.cs"], MaxAgents = 3, MaxChildren = 2, MaxParallelAgents = 2 });

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Completed);
        result.Changes.Count.ShouldBe(2);
        AgentSwarmChangeMerger.Merge(result.Changes, [Snapshot()]).Single().NewText.ShouldBe("ALPHA\nBETA\n");
        client.Requests.ShouldAllBe(request =>
            request.Run.RequestedModel == "gpt-6-luna" && request.Run.ReasoningEffort == "max");
    }

    [Test]
    public async Task ProviderModelSubstitutionRetainsModelSubstitutionFailure()
    {
        var client = new DelegatingModelClient(_ => new AgentModelTurnResult
        {
            ActualModel = "gpt-6-sol",
            OutputText = "{}",
        });

        AgentSwarmRunResult result = await RunAsync(client, new AgentSwarmOptions { AllowedPaths = ["Source.cs"] });

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Failed);
        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.ModelSubstitution);
        result.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task OutputReservationStopsBeforeASecondPaidPhase()
    {
        var client = new DelegatingModelClient(_ => Response("{\"children\":[{\"objective\":\"leaf\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"}]}"));
        AgentSwarmRunResult result = await RunAsync(client, new AgentSwarmOptions
        {
            AllowedPaths = ["Source.cs"],
            MaxOutputTokens = 16,
            MaxPhaseOutputTokens = 16,
        });

        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.BudgetExceeded);
        client.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task NodeAdmissionLimitStopsBeforeLeafProviderCalls()
    {
        var client = new DelegatingModelClient(_ => Response("{\"children\":[{\"objective\":\"one\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"},{\"objective\":\"two\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"}]}"));
        AgentSwarmRunResult result = await RunAsync(client, new AgentSwarmOptions
        {
            AllowedPaths = ["Source.cs"],
            MaxAgents = 2,
            MaxChildren = 2,
        });

        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.BudgetExceeded);
        client.Requests.Count.ShouldBe(1);
    }

    [Test]
    public async Task RejectedParentReviewReturnsNoApprovedChanges()
    {
        var client = new DelegatingModelClient(request => request.Run.SystemInstructions.Contains("reviewer", StringComparison.Ordinal)
            ? Response("{\"approved\":false,\"summary\":\"not acceptable\"}")
            : request.Run.Objective == "root"
                ? Response("{\"children\":[{\"objective\":\"leaf\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"}]}")
                : Response("{\"path\":\"Source.cs\",\"base_sha256\":\"hash\",\"old_text\":\"alpha\",\"new_text\":\"ALPHA\"}"));

        AgentSwarmRunResult result = await RunAsync(client, new AgentSwarmOptions { AllowedPaths = ["Source.cs"] });

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Failed);
        result.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task AggregateArtifactCapRejectsOversizedLeafBeforeReview()
    {
        string[] paths = ["One.cs", "Two.cs", "Three.cs", "Four.cs", "Five.cs"];
        string payload = new('x', 30_000);
        string plan = "{\"children\":[" + string.Join(',', paths.Select(path =>
            $"{{\"objective\":\"{path}\",\"paths\":[\"{path}\"],\"role\":\"leaf\"}}")) + "]}";
        var client = new DelegatingModelClient(request => request.Run.Objective == "root"
            ? Response(plan)
            : Response($"{{\"path\":\"{request.Run.Objective}\",\"base_sha256\":\"hash\",\"old_text\":\"alpha\",\"new_text\":\"{payload}\"}}"));

        AgentSwarmRunResult result = await RunAsync(
            client,
            new AgentSwarmOptions { AllowedPaths = paths, MaxAgents = 6, MaxChildren = 5, MaxParallelAgents = 5 },
            snapshots: paths.Select(path => new AgentContextFileSnapshot { Path = path, Sha256 = "hash", Content = "alpha\n" }).ToArray());

        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.BudgetExceeded);
        result.Changes.ShouldBeEmpty();
        client.Requests.Count.ShouldBe(6);
    }

    [Test]
    public async Task CallerCancellationReturnsCancelledWithoutChanges()
    {
        using var leafStarted = new ManualResetEventSlim();
        var client = new DelegatingModelClient(async (request, cancellationToken) =>
        {
            if (request.Run.Objective == "root")
                return Response("{\"children\":[{\"objective\":\"leaf\",\"paths\":[\"Source.cs\"],\"role\":\"leaf\"}]}");
            leafStarted.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled delay unexpectedly completed.");
        });
        using var cancellation = new CancellationTokenSource();
        Task<AgentSwarmRunResult> run = RunAsync(client, new AgentSwarmOptions { AllowedPaths = ["Source.cs"] }, cancellation.Token);
        leafStarted.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        cancellation.Cancel();

        AgentSwarmRunResult result = await run;

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Cancelled);
        result.Snapshot.Status.ShouldBe(AgentRunStatus.Cancelled);
        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.Cancelled);
        result.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task ElapsedDeadlineIsBudgetFailureInAggregateAndHierarchy()
    {
        var client = new DelegatingModelClient(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The elapsed deadline did not interrupt the response.");
        });

        AgentSwarmRunResult result = await RunAsync(client,
            new AgentSwarmOptions { AllowedPaths = ["Source.cs"], MaxElapsedSeconds = 1 });

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Failed);
        result.Snapshot.Status.ShouldBe(AgentRunStatus.Failed);
        result.Aggregate.Failure!.Category.ShouldBe(AgentFailureCategory.BudgetExceeded);
        result.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task CancellationBeforeInitialProgressLaunchesNoProvider()
    {
        var client = new DelegatingModelClient(_ => throw new InvalidOperationException("No provider call should start."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AgentSwarmRunResult result = await RunAsync(client,
            new AgentSwarmOptions { AllowedPaths = ["Source.cs"] }, cancellation.Token);

        result.Aggregate.Status.ShouldBe(AgentRunStatus.Cancelled);
        result.Snapshot.Status.ShouldBe(AgentRunStatus.Cancelled);
        client.Requests.ShouldBeEmpty();
    }

    private static async Task<AgentSwarmRunResult> RunAsync(
        DelegatingModelClient client,
        AgentSwarmOptions options,
        CancellationToken cancellationToken = default,
        IReadOnlyList<AgentContextFileSnapshot>? snapshots = null)
    {
        using var providerSlots = new SemaphoreSlim(1, 1);
        var runner = new AgentSwarmRunner(new AgentOrchestrator(client), providerSlots);
        return await runner.RunAsync("swarm-test", new AgentRunRequest
        {
            Objective = "root",
            RequestedModel = "gpt-6-luna",
            ReasoningEffort = "max",
            Swarm = options,
            Budget = new AgentRunBudget { MaxOutputTokens = 0, MaxElapsedSeconds = 60 },
        }, snapshots ?? [Snapshot()], cancellationToken: cancellationToken);
    }

    private static AgentContextFileSnapshot Snapshot()
        => new() { Path = "Source.cs", Sha256 = "hash", Content = "alpha\nbeta\n" };

    private static AgentModelTurnResult Response(string output)
        => new() { ActualModel = "gpt-6-luna", OutputText = output };

    private sealed class DelegatingModelClient : IAgentModelClient
    {
        private readonly Func<AgentModelTurnRequest, CancellationToken, Task<AgentModelTurnResult>> _response;
        private readonly object _lock = new();

        public DelegatingModelClient(Func<AgentModelTurnRequest, AgentModelTurnResult> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        public DelegatingModelClient(Func<AgentModelTurnRequest, CancellationToken, Task<AgentModelTurnResult>> response)
            => _response = response;

        public List<AgentModelTurnRequest> Requests { get; } = [];

        public Task<AgentModelTurnResult> CreateResponseAsync(
            AgentModelTurnRequest request,
            IAgentRunObserver observer,
            CancellationToken cancellationToken)
        {
            lock (_lock)
                Requests.Add(request);
            return _response(request, cancellationToken);
        }
    }
}
