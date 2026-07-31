using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class LiveOpenAiAgentSmokeTests
{
    [Test]
    [Explicit("Makes a separately billed OpenAI API request when explicitly selected.")]
    public async Task CompletesOneStrictlyBudgetedPublicApiTurn()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("XRE_RUN_LIVE_AGENT_BROKER_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Ignore("Set XRE_RUN_LIVE_AGENT_BROKER_TESTS=1 to authorize the live smoke test.");
        }

        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            Assert.Ignore("OPENAI_API_KEY is not set.");

        string model = Environment.GetEnvironmentVariable("XRE_LIVE_AGENT_MODEL")
            ?? "gpt-5.6-luna";
        string[] approvedModels = ["gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol"];
        approvedModels.ShouldContain(model);

        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var orchestrator = new AgentOrchestrator(
            new OpenAiResponsesModelClient(httpClient, () => apiKey));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        AgentRunResult result = await orchestrator.RunAsync(
            $"live-{Guid.NewGuid():N}",
            new AgentRunRequest
            {
                Objective = "Reply with exactly: broker live smoke passed",
                RequestedModel = model,
                ReasoningEffort = "low",
                UseCompactHandoffPrompt = false,
                Budget = new AgentRunBudget
                {
                    MaxTurns = 1,
                    MaxToolCalls = 0,
                    MaxOutputTokens = 64,
                    MaxToolResultBytes = 1_024,
                    MaxElapsedSeconds = 30,
                    MaxRetries = 0,
                    MaxConcurrency = 1,
                },
            },
            new RecordingAgentToolProvider(),
            cancellationToken: timeout.Token);

        result.Status.ShouldBe(AgentRunStatus.Completed);
        result.RequestedModel.ShouldBe(model);
        result.ActualModel.ShouldStartWith(model);
        result.FinalText.ToLowerInvariant().ShouldContain("broker live smoke passed");
        result.ToolCallCount.ShouldBe(0);
    }
}
