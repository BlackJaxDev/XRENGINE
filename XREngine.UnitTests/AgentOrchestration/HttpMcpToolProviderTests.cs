using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

[TestFixture]
public class HttpMcpToolProviderTests
{
    [Test]
    public async Task EnforcesReadOnlyDefaultAndPreservesMcpToolErrors()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(request => JsonResponse(
            """
            {"jsonrpc":"2.0","id":"1","result":{"tools":[
              {"name":"get_node","description":"read","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":true,"destructiveHint":false}},
              {"name":"set_node","description":"write","inputSchema":{"type":"object"},"annotations":{"readOnlyHint":false,"destructiveHint":false}}
            ]}}
            """));
        handler.Enqueue(request => JsonResponse(
            """{"jsonrpc":"2.0","id":"2","error":{"code":-32001,"message":"editor rejected secret-value call"}}"""));
        using var client = new HttpClient(handler);
        var provider = new HttpMcpToolProvider(
            client,
            new Uri("http://localhost:5467/mcp/"),
            new AgentToolPolicy(),
            "secret-value");

        IReadOnlyList<AgentToolDefinition> tools = await provider.ListToolsAsync(CancellationToken.None);
        AgentToolResult result = await provider.ExecuteAsync(
            new AgentToolCall { CallId = "call", Name = "get_node", ArgumentsJson = "{}" },
            CancellationToken.None);

        tools.Select(static tool => tool.Name).ShouldBe(["get_node"]);
        result.IsError.ShouldBeTrue();
        result.Content.ShouldContain("editor rejected [REDACTED] call");
        result.Content.ShouldNotContain("secret-value");
    }

    [Test]
    public async Task PreflightVerifiesExactNamedSession()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(request => JsonResponse(
            """
            {"jsonrpc":"2.0","id":"1","result":{"ok":true,"status":{"editorSession":{"name":"other"}}}}
            """));
        using var client = new HttpClient(handler);
        var provider = new HttpMcpToolProvider(
            client,
            new Uri("http://localhost:5467/mcp/"),
            new AgentToolPolicy());

        AgentToolProviderException exception = await Should.ThrowAsync<AgentToolProviderException>(
            () => provider.PreflightAsync("expected", CancellationToken.None));

        exception.Category.ShouldBe(AgentFailureCategory.ToolDiscovery);
    }

    [Test]
    public async Task MalformedToolArgumentsRemainAnErrorWithoutCallingEditor()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => JsonResponse(
            """
            {"jsonrpc":"2.0","id":"1","result":{"tools":[{"name":"get_node","annotations":{"readOnlyHint":true},"inputSchema":{"type":"object"}}]}}
            """));
        using var client = new HttpClient(handler);
        var provider = new HttpMcpToolProvider(
            client,
            new Uri("http://127.0.0.1:5467/mcp/"),
            new AgentToolPolicy());
        await provider.ListToolsAsync(CancellationToken.None);

        AgentToolResult result = await provider.ExecuteAsync(
            new AgentToolCall
            {
                CallId = "malformed",
                Name = "get_node",
                ArgumentsJson = "{not-json",
            },
            CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Content.ShouldContain("not valid JSON");
        handler.RequestBodies.Count.ShouldBe(1);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
