using System.Diagnostics;
using System.Net;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class BrokerRunIntegrationTests
{
    [Test]
    public async Task FakeEditorAndResponsesRunStartsPromptlyAndPollsToTerminal()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"xrengine-agent-broker-test-{Guid.NewGuid():N}");
        string sessionName = "fake-session";
        string sessionRoot = Path.Combine(
            temporaryRoot,
            "Build",
            "_AgentValidation",
            "mcp-sessions",
            sessionName);
        string apiKeyEnvironmentVariable = $"XRE_TEST_OPENAI_KEY_{Guid.NewGuid():N}";

        try
        {
            Directory.CreateDirectory(sessionRoot);
            await File.WriteAllTextAsync(Path.Combine(temporaryRoot, "AGENTS.md"), "# Test");
            await File.WriteAllTextAsync(
                Path.Combine(sessionRoot, "session.json"),
                """{"name":"fake-session","endpoint":"http://127.0.0.1:59999/mcp/"}""");
            Environment.SetEnvironmentVariable(apiKeyEnvironmentVariable, "test-key");

            var handler = new QueueHttpMessageHandler();
            handler.Enqueue(_ => JsonResponse(
                """
                {"jsonrpc":"2.0","id":"ping","result":{"status":{"editorSession":{"name":"fake-session"}}}}
                """));
            handler.Enqueue(_ => JsonResponse(
                """{"jsonrpc":"2.0","id":"tools","result":{"tools":[]}}"""));
            handler.EnqueueSse(
                """
                data: {"type":"response.created","response":{"id":"resp_fake","model":"gpt-5.6-luna"}}

                data: {"type":"response.output_text.delta","delta":"fake complete"}

                data: {"type":"response.completed","response":{"id":"resp_fake","model":"gpt-5.6-luna","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"fake complete"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

                data: [DONE]

                """);
            using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var configuration = new BrokerConfiguration
            {
                RepositoryRoot = temporaryRoot,
                ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
                MaximumConcurrentRuns = 1,
                MaximumRetainedRuns = 4,
                RetentionMinutes = 5,
            };
            await using var registry = new AgentRunRegistry(configuration, httpClient);
            var stopwatch = Stopwatch.StartNew();

            string runId = registry.Start(new AgentRunRequest
            {
                Objective = "Complete the fake run.",
                RequestedModel = "gpt-5.6-luna",
                ReasoningEffort = "low",
                EditorSession = sessionName,
                Budget = new AgentRunBudget
                {
                    MaxTurns = 1,
                    MaxToolCalls = 0,
                    MaxOutputTokens = 32,
                    MaxToolResultBytes = 1_024,
                    MaxElapsedSeconds = 5,
                    MaxRetries = 0,
                },
            });

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
            AgentRunSnapshot snapshot = registry.Get(runId);
            using var pollingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (snapshot.Status is AgentRunStatus.Queued or AgentRunStatus.Running)
            {
                await Task.Delay(10, pollingTimeout.Token);
                snapshot = registry.Get(runId);
            }

            snapshot.Status.ShouldBe(AgentRunStatus.Completed);
            snapshot.RequestedModel.ShouldBe("gpt-5.6-luna");
            snapshot.ActualModel.ShouldBe("gpt-5.6-luna");
            snapshot.Result!.FinalText.ShouldBe("fake complete");
            snapshot.Usage.TotalTokens.ShouldBe(7);
            handler.RequestBodies.Count.ShouldBe(3);
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyEnvironmentVariable, null);
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
