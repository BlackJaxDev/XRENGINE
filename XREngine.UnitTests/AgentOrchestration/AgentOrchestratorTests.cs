using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AgentOrchestratorTests
{
    [TestCase("gpt-5.6-luna")]
    [TestCase("gpt-5.6-terra")]
    [TestCase("gpt-5.6-sol")]
    public async Task PreservesExplicitModelSelection(string model)
    {
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(FinalTurn(model, "complete"));
        var orchestrator = new AgentOrchestrator(modelClient);

        AgentRunResult result = await orchestrator.RunAsync(
            "run",
            CreateRequest(model),
            new RecordingAgentToolProvider());

        result.Status.ShouldBe(AgentRunStatus.Completed);
        result.RequestedModel.ShouldBe(model);
        result.ActualModel.ShouldBe(model);
    }

    [Test]
    public async Task RejectsProviderModelSubstitution()
    {
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(FinalTurn("gpt-5.6-sol", "wrong tier"));

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-luna"),
            new RecordingAgentToolProvider());

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Failure!.Category.ShouldBe(AgentFailureCategory.ModelSubstitution);
        result.RequestedModel.ShouldBe("gpt-5.6-luna");
        result.ActualModel.ShouldBe("gpt-5.6-sol");
    }

    [Test]
    public async Task PreservesCallIdsAndRequiresPostMutationEvidence()
    {
        AgentToolDefinition mutate = new()
        {
            Name = "set_transform",
            IsReadOnly = false,
        };
        AgentToolDefinition read = new()
        {
            Name = "get_transform",
            IsReadOnly = true,
        };
        AgentToolDefinition capture = new()
        {
            Name = "capture_viewport_screenshot",
            IsReadOnly = true,
        };
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall
            {
                CallId = "mutation_call",
                Name = "set_transform",
                ArgumentsJson = """{"x":1}""",
            }));
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall
            {
                CallId = "read_call",
                Name = "get_transform",
                ArgumentsJson = "{}",
            }));
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall
            {
                CallId = "capture_call",
                Name = "capture_viewport_screenshot",
                ArgumentsJson = "{}",
            }));
        modelClient.Enqueue(FinalTurn("gpt-5.6-terra", "verified"));
        var provider = new RecordingAgentToolProvider(
            [mutate, read, capture],
            call => call.Name == "capture_viewport_screenshot"
                ? new AgentToolResult
                {
                    Content = "captured",
                    ImagePath = "mcp-captures/verified.png",
                }
                : new AgentToolResult { Content = "ok" });

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest(
                "gpt-5.6-terra",
                new AgentToolPolicy
                {
                    AllowMutation = true,
                    AllowedTools =
                    [
                        "set_transform",
                        "get_transform",
                        "capture_viewport_screenshot",
                    ],
                    RequireMutationEvidence = true,
                }),
            provider);

        result.Status.ShouldBe(AgentRunStatus.Completed);
        provider.Calls.Select(static call => call.CallId)
            .ShouldBe(["mutation_call", "read_call", "capture_call"]);
        result.ToolEvidence.Count.ShouldBe(3);
        result.ToolEvidence[0].IsMutation.ShouldBeTrue();
        result.ToolEvidence[2].IsVisualEvidence.ShouldBeTrue();
    }

    [Test]
    public async Task FailsMutationWithoutReadback()
    {
        AgentToolDefinition mutate = new() { Name = "set_transform", IsReadOnly = false };
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall { CallId = "call_1", Name = "set_transform" }));
        modelClient.Enqueue(FinalTurn("gpt-5.6-terra", "done"));

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest(
                "gpt-5.6-terra",
                new AgentToolPolicy
                {
                    AllowMutation = true,
                    AllowedTools = ["set_transform"],
                }),
            new RecordingAgentToolProvider([mutate]));

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Failure!.Category.ShouldBe(AgentFailureCategory.MutationEvidenceMissing);
    }

    [Test]
    public async Task DuplicateCallIdsAreTerminal()
    {
        AgentToolDefinition read = new() { Name = "get_node", IsReadOnly = true };
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall { CallId = "duplicate", Name = "get_node" }));
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall { CallId = "duplicate", Name = "get_node" }));

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-terra"),
            new RecordingAgentToolProvider([read]));

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Failure!.Category.ShouldBe(AgentFailureCategory.ProviderError);
    }

    [Test]
    public async Task CancellationStopsPendingModelTurn()
    {
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return FinalTurn("gpt-5.6-terra", "unreachable");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-terra"),
            new RecordingAgentToolProvider(),
            cancellationToken: cancellation.Token);

        result.Status.ShouldBe(AgentRunStatus.Cancelled);
        result.Failure!.Category.ShouldBe(AgentFailureCategory.Cancelled);
    }

    [Test]
    public async Task TruncatesOversizedToolOutputBeforeContinuation()
    {
        AgentToolDefinition read = new() { Name = "get_large_result", IsReadOnly = true };
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(ToolTurn(
            "gpt-5.6-terra",
            new AgentToolCall { CallId = "large", Name = "get_large_result" }));
        modelClient.Enqueue(FinalTurn("gpt-5.6-terra", "bounded"));

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-terra") with
            {
                Budget = new AgentRunBudget
                {
                    MaxTurns = 3,
                    MaxToolCalls = 2,
                    MaxOutputTokens = 32,
                    MaxToolResultBytes = 1_024,
                    MaxElapsedSeconds = 10,
                },
            },
            new RecordingAgentToolProvider(
                [read],
                _ => new AgentToolResult { Content = new string('x', 4_096) }));

        result.Status.ShouldBe(AgentRunStatus.Completed);
        modelClient.Requests[1].ToolOutputs.Single().Content
            .ShouldContain("[tool result truncated by broker budget]");
    }

    [Test]
    public async Task RetriesOnlyRetryableProviderFailureWithinBudget()
    {
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue((_, _) => throw new AgentModelException(
            AgentFailureCategory.Transport,
            "temporary",
            retryable: true,
            retryAfter: TimeSpan.Zero));
        modelClient.Enqueue(FinalTurn("gpt-5.6-terra", "recovered"));
        var events = new List<AgentRunEvent>();

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-terra") with
            {
                Budget = new AgentRunBudget
                {
                    MaxTurns = 2,
                    MaxToolCalls = 0,
                    MaxOutputTokens = 32,
                    MaxToolResultBytes = 1_024,
                    MaxElapsedSeconds = 10,
                    MaxRetries = 1,
                },
            },
            new RecordingAgentToolProvider(),
            new DelegateAgentRunObserver((runEvent, _) =>
            {
                events.Add(runEvent);
                return ValueTask.CompletedTask;
            }));

        result.Status.ShouldBe(AgentRunStatus.Completed);
        modelClient.Requests.Count.ShouldBe(2);
        events.Count(runEvent => runEvent.Kind == AgentRunEventKind.Retry).ShouldBe(1);
    }

    [Test]
    public async Task EnforcesElapsedTimeBudget()
    {
        var modelClient = new ScriptedAgentModelClient();
        modelClient.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return FinalTurn("gpt-5.6-terra", "unreachable");
        });

        AgentRunResult result = await new AgentOrchestrator(modelClient).RunAsync(
            "run",
            CreateRequest("gpt-5.6-terra") with
            {
                Budget = new AgentRunBudget
                {
                    MaxTurns = 1,
                    MaxToolCalls = 0,
                    MaxOutputTokens = 32,
                    MaxToolResultBytes = 1_024,
                    MaxElapsedSeconds = 1,
                },
            },
            new RecordingAgentToolProvider());

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Failure!.Category.ShouldBe(AgentFailureCategory.BudgetExceeded);
    }

    private static AgentRunRequest CreateRequest(
        string model,
        AgentToolPolicy? toolPolicy = null)
        => new()
        {
            Objective = "test",
            RequestedModel = model,
            EditorSession = "test-session",
            ToolPolicy = toolPolicy ?? new AgentToolPolicy(),
            Budget = new AgentRunBudget
            {
                MaxTurns = 5,
                MaxToolCalls = 8,
                MaxOutputTokens = 32,
                MaxElapsedSeconds = 10,
            },
        };

    private static AgentModelTurnResult FinalTurn(string model, string text)
        => new()
        {
            ActualModel = model,
            OutputText = text,
            OutputItems = [new AgentOutputItem { Kind = AgentOutputItemKind.Text, Text = text }],
            ContinuationJson = "[]",
        };

    private static AgentModelTurnResult ToolTurn(string model, params AgentToolCall[] calls)
        => new()
        {
            ActualModel = model,
            ToolCalls = calls,
            ContinuationJson = "[]",
        };
}
