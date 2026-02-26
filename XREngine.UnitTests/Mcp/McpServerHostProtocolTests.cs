using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

[TestFixture]
public class McpServerHostProtocolTests
{
    private static MethodInfo HandleRpcMethod => typeof(McpServerHost)
        .GetMethod("HandleRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HandleRpcAsync method not found.");

    private static MethodInfo TryResolveCorsOriginMethod => typeof(McpServerHost)
        .GetMethod("TryResolveCorsOrigin", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TryResolveCorsOrigin method not found.");

    private static MethodInfo ReadRequestBodyAsyncMethod => typeof(McpServerHost)
        .GetMethod("ReadRequestBodyAsync", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ReadRequestBodyAsync method not found.");

    private static MethodInfo CanInvokeToolMethod => typeof(McpServerHost)
        .GetMethod("CanInvokeTool", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CanInvokeTool method not found.");

    [Test]
    public async Task HandleRpcAsync_ReturnsInitializeCapabilities()
    {
        using var document = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        object result = await InvokeHandleRpcAsync(document.RootElement, new EditorPreferences());

        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("result").GetProperty("capabilities").TryGetProperty("resources", out _).ShouldBeTrue();
        payload.GetProperty("result").GetProperty("capabilities").TryGetProperty("prompts", out _).ShouldBeTrue();
        payload.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
    }

    [Test]
    public async Task HandleRpcAsync_ReturnsMethodNotFoundForUnknownMethod()
    {
        using var document = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"does/not/exist\"}");
        object result = await InvokeHandleRpcAsync(document.RootElement, new EditorPreferences());

        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Test]
    public async Task HandleRpcAsync_ReturnsInvalidRequestForMissingMethod()
    {
        using var document = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1}");
        object result = await InvokeHandleRpcAsync(document.RootElement, new EditorPreferences());

        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
    }

    [Test]
    public async Task HandleRpcAsync_ReturnsInvalidParamsForToolsCallMissingParams()
    {
        using var document = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}");
        object result = await InvokeHandleRpcAsync(document.RootElement, new EditorPreferences());

        var payload = JsonSerializer.SerializeToElement(result);
        payload.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    [Test]
    public async Task HandleRpcAsync_PingIncludesStatusWhenEnabled()
    {
        using var document = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        var prefs = new EditorPreferences { McpServerIncludeStatusInPing = true };

        object result = await InvokeHandleRpcAsync(document.RootElement, prefs);
        var payload = JsonSerializer.SerializeToElement(result);

        payload.GetProperty("result").TryGetProperty("status", out _).ShouldBeTrue();
    }

    [Test]
    public void TryResolveCorsOrigin_AllowsMatchingOriginAndBlocksUnknownOrigin()
    {
        var allowCall = new object?[] { "https://app.local", "https://app.local;https://tools.local", null };
        bool allow = (bool)(TryResolveCorsOriginMethod.Invoke(null, allowCall) ?? false);

        allow.ShouldBeTrue();
        allowCall[2].ShouldBe("https://app.local");

        var denyCall = new object?[] { "https://evil.local", "https://app.local", null };
        bool deny = (bool)(TryResolveCorsOriginMethod.Invoke(null, denyCall) ?? true);
        deny.ShouldBeFalse();
    }

    [Test]
    public async Task ReadRequestBodyAsync_RejectsPayloadOverLimit()
    {
        using var stream = new MemoryStream(new byte[8]);

        var invocation = ReadRequestBodyAsyncMethod.Invoke(null, [stream, 4, CancellationToken.None]);
        invocation.ShouldNotBeNull();

        var task = invocation as Task<byte[]>;
        task.ShouldNotBeNull();

        await Should.ThrowAsync<InvalidDataException>(async () => _ = await task!);
    }

    [Test]
    public async Task ReadRequestBodyAsync_RespectsCancellation()
    {
        using var stream = new MemoryStream(new byte[16]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var invocation = ReadRequestBodyAsyncMethod.Invoke(null, [stream, 128, cts.Token]);
        invocation.ShouldNotBeNull();

        var task = invocation as Task<byte[]>;
        task.ShouldNotBeNull();

        await Should.ThrowAsync<OperationCanceledException>(async () => _ = await task!);
    }

    [Test]
    public void CanInvokeTool_BlocksMutatingToolsInReadOnlyMode()
    {
        var prefs = new EditorPreferences { McpServerReadOnly = true };
        var call = new object?[] { "create_scene_node", prefs, null };

        bool allowed = (bool)(CanInvokeToolMethod.Invoke(null, call) ?? true);

        allowed.ShouldBeFalse();
        ((string?)call[2]).ShouldContain("read-only");
    }

    private static async Task<object> InvokeHandleRpcAsync(JsonElement root, EditorPreferences prefs)
    {
        object host = McpServerHost.Instance;
        object? invocation = HandleRpcMethod.Invoke(host, [root, CancellationToken.None, prefs]);
        invocation.ShouldNotBeNull();

        var task = invocation as Task<object?>;
        task.ShouldNotBeNull();

        object? value = await task!;
        value.ShouldNotBeNull();
        return value!;
    }
}
