using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Exchanges bounded ACP JSON-RPC messages with one owned Gemini process.
/// </summary>
internal sealed class GeminiRpcProcess(ProcessStartInfo startInfo) : IAsyncDisposable
{
    private readonly CodexRpcProcess _stdio = new(startInfo);
    private long _nextId;

    public async Task<JsonObject> CallAsync(string method, JsonObject parameters, CancellationToken token,
        Func<JsonObject, CancellationToken, Task>? onMessage = null)
    {
        long id = ++_nextId;
        await _stdio.WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
        }, token);
        while (true)
        {
            JsonObject message = await _stdio.ReadAsync(token);
            if (message["jsonrpc"]?.GetValue<string>() != "2.0")
                throw new InvalidDataException("Gemini returned a message without the ACP JSON-RPC version.");
            if (message["method"] is not null)
            {
                if (onMessage is not null)
                    await onMessage(message, token);
                else if (message["id"] is { } requestId)
                    await DenyAsync(requestId, token);
                continue;
            }
            if (message["id"]?.ToString() != id.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Gemini returned an unexpected ACP response identifier.");
            if (message["error"] is JsonObject error)
            {
                if (error["message"]?.GetValue<string>() is { } detail
                    && detail.Contains("This client is no longer supported for Gemini Code Assist for individuals", StringComparison.Ordinal))
                    throw new InvalidOperationException("Gemini CLI no longer accepts personal Google subscriptions. Use an eligible organization account.");
                string code = error["code"]?.ToString() ?? "unknown";
                throw new InvalidOperationException($"Gemini rejected {method} (RPC {code}). Check the account, model, and CLI version.");
            }
            return message["result"] as JsonObject
                ?? throw new InvalidDataException($"Gemini returned an invalid {method} result.");
        }
    }

    public Task DenyAsync(JsonNode id, CancellationToken token)
        => _stdio.WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "The host does not authorize this request." },
        }, token);

    public Task WriteAsync(JsonObject message, CancellationToken token) => _stdio.WriteAsync(message, token);

    public ValueTask DisposeAsync() => _stdio.DisposeAsync();
}
