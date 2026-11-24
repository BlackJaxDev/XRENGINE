using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Exchanges bounded ACP JSON-RPC messages with one owned Antigravity process.
/// </summary>
internal sealed class AntigravityRpcProcess(ProcessStartInfo startInfo) : IAsyncDisposable
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
                throw new InvalidDataException("Antigravity returned a message without the ACP JSON-RPC version.");
            if (message["method"] is not null)
            {
                if (onMessage is not null)
                    await onMessage(message, token);
                else if (message["id"] is { } requestId)
                    await DenyAsync(requestId, token);
                continue;
            }
            if (message["id"]?.ToString() != id.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Antigravity returned an unexpected ACP response identifier.");
            if (message["error"] is JsonObject error)
            {
                string code = error["code"]?.ToString() ?? "unknown";
                if (error["message"]?.GetValue<string>() == "Authentication required")
                    throw new InvalidOperationException("Sign in with Google through the Assistant connection settings.");
                throw new InvalidOperationException($"Antigravity rejected {method} (RPC {code}). Check the account, model, and CLI version.");
            }
            return message["result"] as JsonObject
                ?? throw new InvalidDataException($"Antigravity returned an invalid {method} result.");
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
