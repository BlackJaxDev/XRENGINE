using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class AnthropicMessagesModelClientTests
{
    [Test]
    public async Task SendsAnthropicHeadersAndRequiredOutputLimit()
    {
        var handler = new QueueHttpMessageHandler();
        string? apiKeyHeader = null;
        string? workspaceHeader = null;
        string? versionHeader = null;
        handler.Enqueue(request =>
        {
            apiKeyHeader = request.Headers.GetValues("x-api-key").Single();
            workspaceHeader = request.Headers.GetValues("anthropic-workspace-id").Single();
            versionHeader = request.Headers.GetValues("anthropic-version").Single();
            return StreamResponse(TextStream());
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key",
            new Uri("http://localhost/v1/messages"), () => "wrkspc_123ABC");

        AgentModelTurnResult result = await client.CreateResponseAsync(
            Request(), NullAgentRunObserver.Instance, CancellationToken.None);

        apiKeyHeader.ShouldBe("test-key");
        workspaceHeader.ShouldBe("wrkspc_123ABC");
        versionHeader.ShouldBe("2023-06-01");
        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies.Single());
        JsonElement root = body.RootElement;
        root.GetProperty("model").GetString().ShouldBe("claude-haiku-5-5");
        root.GetProperty("max_tokens").GetInt32().ShouldBe(128_000);
        root.GetProperty("stream").GetBoolean().ShouldBeTrue();
        root.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("adaptive");
        root.GetProperty("output_config").GetProperty("effort").GetString().ShouldBe("medium");
        root.TryGetProperty("store", out _).ShouldBeFalse();
        root.TryGetProperty("input", out _).ShouldBeFalse();
        root.TryGetProperty("reasoning", out _).ShouldBeFalse();
        result.OutputText.ShouldBe("done");
    }

    [Test]
    public async Task ReplaysSignedThinkingToolCallAndImageResult()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse(ToolStream());
        handler.EnqueueSse(TextStream());
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key",
            new Uri("http://localhost/v1/messages"));
        AgentToolDefinition tool = new() { Name = "ping" };
        AgentModelTurnResult first = await client.CreateResponseAsync(
            Request(tools: [tool]), NullAgentRunObserver.Instance, CancellationToken.None);
        AgentModelTurnResult second = await client.CreateResponseAsync(
            Request(tools: [tool], continuation: first.ContinuationJson,
                outputs: [new AgentModelToolOutput
                {
                    CallId = "tool_1",
                    Content = "pong",
                    ImageDataUri = "data:image/png;base64,aGVsbG8=",
                }]), NullAgentRunObserver.Instance, CancellationToken.None);

        first.ToolCalls.Single().CallId.ShouldBe("tool_1");
        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = body.RootElement.GetProperty("messages");
        messages[1].GetProperty("role").GetString().ShouldBe("assistant");
        messages[1].GetProperty("content")[0].GetProperty("signature").GetString().ShouldBe("signed");
        JsonElement result = messages[2].GetProperty("content")[0];
        result.GetProperty("tool_use_id").GetString().ShouldBe("tool_1");
        result.GetProperty("content")[1].GetProperty("source").GetProperty("data")
            .GetString().ShouldBe("aGVsbG8=");
        second.OutputText.ShouldBe("done");
    }

    [Test]
    public async Task RejectsModelSubstitutionBeforePublishingText()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse(TextStream("claude-opus-5-5"));
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key",
            new Uri("http://localhost/v1/messages"));
        List<AgentRunEvent> events = [];
        var observer = new DelegateAgentRunObserver((runEvent, _) =>
        {
            events.Add(runEvent);
            return ValueTask.CompletedTask;
        });

        AgentModelException error = await Should.ThrowAsync<AgentModelException>(() =>
            client.CreateResponseAsync(Request(), observer, CancellationToken.None));

        error.Category.ShouldBe(AgentFailureCategory.ModelSubstitution);
        events.ShouldNotContain(item => item.Kind == AgentRunEventKind.TextDelta);
    }

    [Test]
    public async Task TruncatedStreamKeepsTransportAttempt()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse("""
            data: {"type":"message_start","message":{"id":"msg_1","model":"claude-haiku-5-5"}}

            """);
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key",
            new Uri("http://localhost/v1/messages"));

        AgentModelException error = await Should.ThrowAsync<AgentModelException>(() =>
            client.CreateResponseAsync(Request(), NullAgentRunObserver.Instance, CancellationToken.None));

        error.Category.ShouldBe(AgentFailureCategory.Transport);
        error.ProviderAttempt.ShouldNotBeNull();
        error.ProviderAttempt.ResponseId.ShouldBe("msg_1");
    }

    [Test]
    public async Task CancellationKeepsProviderAttempt()
    {
        var handler = new QueueHttpMessageHandler();
        handler.EnqueueSse(TextStream());
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key",
            new Uri("http://localhost/v1/messages"));
        using var cancellation = new CancellationTokenSource();
        var observer = new DelegateAgentRunObserver((runEvent, token) =>
        {
            if (runEvent.Kind == AgentRunEventKind.Status)
                throw new OperationCanceledException(token);
            return ValueTask.CompletedTask;
        });

        try
        {
            await client.CreateResponseAsync(Request(), observer, cancellation.Token);
            Assert.Fail("The provider turn did not stop after cancellation.");
        }
        catch (AgentModelOperationCanceledException exception)
        {
            exception.ProviderAttempt.Outcome.ShouldBe("cancelled");
        }
    }

    [TestCase("claude-fable-5-1")]
    [TestCase("claude-opus-5-5")]
    [TestCase("claude-sonnet-5-5")]
    public async Task RejectsForcedToolsForUnsupportedModels(string model)
    {
        var handler = new QueueHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicMessagesModelClient(httpClient, () => "test-key");

        AgentModelException error = await Should.ThrowAsync<AgentModelException>(() =>
            client.CreateResponseAsync(Request(model: model, requireTool: true),
                NullAgentRunObserver.Instance, CancellationToken.None));

        error.Category.ShouldBe(AgentFailureCategory.Validation);
        handler.RequestBodies.ShouldBeEmpty();
    }

    private static AgentModelTurnRequest Request(
        string model = "claude-haiku-5-5", bool requireTool = false,
        IReadOnlyList<AgentToolDefinition>? tools = null,
        string? continuation = null, IReadOnlyList<AgentModelToolOutput>? outputs = null)
        => new()
        {
            Run = new AgentRunRequest
            {
                RequestedModel = model,
                ReasoningEffort = "MEDIUM",
                RequireToolUse = requireTool,
            },
            Prompt = "test",
            Tools = tools ?? [],
            ContinuationJson = continuation,
            ToolOutputs = outputs ?? [],
        };

    private static HttpResponseMessage StreamResponse(string events)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8),
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static string TextStream(string model = "claude-haiku-5-5")
        => """
            data: {"type":"message_start","message":{"id":"msg_1","model":"MODEL","usage":{"input_tokens":2,"output_tokens":1}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}

            """.Replace("MODEL", model, StringComparison.Ordinal);

    private static string ToolStream()
        => """
            data: {"type":"message_start","message":{"id":"msg_tool","model":"claude-haiku-5-5","usage":{"input_tokens":3,"output_tokens":1}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"check"}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signed"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tool_1","name":"ping","input":{}}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":4}}

            data: {"type":"message_stop"}

            """;
}
