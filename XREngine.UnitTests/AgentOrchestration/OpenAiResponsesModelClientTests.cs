using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class OpenAiResponsesModelClientTests
{
    [Test]
    public async Task SendsStoreFalseExactModelAndReplaysToolAndReasoningItems()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse(
            """
            data: {"type":"response.created","response":{"id":"resp_1","model":"gpt-5.6-terra"}}

            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_1","name":"ping","arguments":"{}"}}

            data: {"type":"response.completed","response":{"id":"resp_1","model":"gpt-5.6-terra","output":[{"type":"reasoning","encrypted_content":"opaque"},{"type":"function_call","call_id":"call_1","name":"ping","arguments":"{}"}],"usage":{"input_tokens":4,"output_tokens":2,"total_tokens":6}}}

            data: [DONE]

            """);
        handler.EnqueueSse(
            """
            data: {"type":"response.completed","response":{"id":"resp_2","model":"gpt-5.6-terra","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}],"usage":{"input_tokens":8,"output_tokens":1,"total_tokens":9}}}

            data: [DONE]

            """);
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiResponsesModelClient(
            httpClient,
            () => "test-key",
            new Uri("http://localhost/v1/responses"));
        AgentRunRequest run = CreateRun();
        AgentToolDefinition tool = new()
        {
            Name = "ping",
            InputSchemaJson = """{"type":"object","properties":{}}""",
        };

        AgentModelTurnResult first = await client.CreateResponseAsync(
            new AgentModelTurnRequest
            {
                Run = run,
                Prompt = "test",
                Tools = [tool],
            },
            NullAgentRunObserver.Instance,
            CancellationToken.None);
        AgentModelTurnResult second = await client.CreateResponseAsync(
            new AgentModelTurnRequest
            {
                Run = run,
                Prompt = "test",
                Tools = [tool],
                ContinuationJson = first.ContinuationJson,
                ToolOutputs =
                [
                    new AgentModelToolOutput { CallId = "call_1", Content = "pong" },
                ],
                TurnIndex = 1,
            },
            NullAgentRunObserver.Instance,
            CancellationToken.None);

        using JsonDocument firstPayload = JsonDocument.Parse(handler.RequestBodies[0]);
        firstPayload.RootElement.GetProperty("store").GetBoolean().ShouldBeFalse();
        firstPayload.RootElement.GetProperty("model").GetString().ShouldBe("gpt-5.6-terra");
        using JsonDocument secondPayload = JsonDocument.Parse(handler.RequestBodies[1]);
        string secondJson = secondPayload.RootElement.GetRawText();
        secondJson.ShouldContain("encrypted_content");
        secondJson.ShouldContain("function_call_output");
        secondJson.ShouldContain("pong");
        second.OutputText.ShouldBe("done");
    }

    [Test]
    public async Task PrematureStreamEndIsRetryableTransportFailure()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse(
            """
            data: {"type":"response.created","response":{"id":"resp_partial","model":"gpt-5.6-terra"}}

            data: [DONE]

            """);
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiResponsesModelClient(
            httpClient,
            () => "test-key",
            new Uri("http://localhost/v1/responses"));

        AgentModelException exception = await Should.ThrowAsync<AgentModelException>(() =>
            client.CreateResponseAsync(
                new AgentModelTurnRequest
                {
                    Run = CreateRun(),
                    Prompt = "test",
                    Tools = [],
                },
                NullAgentRunObserver.Instance,
                CancellationToken.None));

        exception.Category.ShouldBe(AgentFailureCategory.Transport);
        exception.Retryable.ShouldBeTrue();
    }

    [Test]
    public async Task ProviderErrorsRedactTheExactApiKey()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"credential test-key was rejected","api_key":"test-key"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiResponsesModelClient(
            httpClient,
            () => "test-key",
            new Uri("http://localhost/v1/responses"));

        AgentModelException exception = await Should.ThrowAsync<AgentModelException>(() =>
            client.CreateResponseAsync(
                new AgentModelTurnRequest
                {
                    Run = CreateRun(),
                    Prompt = "test",
                    Tools = [],
                },
                NullAgentRunObserver.Instance,
                CancellationToken.None));

        exception.Category.ShouldBe(AgentFailureCategory.Authentication);
        exception.Message.ShouldNotContain("test-key");
        exception.DiagnosticDetail.ShouldNotContain("test-key");
        exception.Message.ShouldContain("[REDACTED]");
    }

    private static AgentRunRequest CreateRun()
        => new()
        {
            Objective = "test",
            RequestedModel = "gpt-5.6-terra",
            Budget = new AgentRunBudget { MaxOutputTokens = 32 },
        };
}
