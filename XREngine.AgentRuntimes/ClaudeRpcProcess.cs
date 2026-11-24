using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>Exchanges bounded JSON lines with one owned Claude Code process.</summary>
internal sealed class ClaudeRpcProcess : IAsyncDisposable
{
    private const int MaximumLineChars = 8 * 1024 * 1024;
    private readonly Process _process;
    private readonly Task _stderrDrain;

    public ClaudeRpcProcess(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo };
        if (!_process.Start())
            throw new Win32Exception("Claude Code did not start.");
        _stderrDrain = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }

    public async Task WriteAsync(JsonObject message, CancellationToken token)
    {
        await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
    }

    public async Task<JsonObject> ReadAsync(CancellationToken token)
    {
        var line = new System.Text.StringBuilder();
        char[] buffer = new char[1];
        while (line.Length < MaximumLineChars)
        {
            int count = await _process.StandardOutput.ReadAsync(buffer, token);
            if (count == 0)
                throw new EndOfStreamException("Claude Code closed its protocol stream.");
            if (buffer[0] == '\n')
                break;
            line.Append(buffer[0]);
        }
        if (line.Length >= MaximumLineChars)
            throw new InvalidDataException("Claude Code returned an oversized protocol message.");
        return JsonNode.Parse(line.ToString()) as JsonObject
            ?? throw new InvalidDataException("Claude Code returned an invalid protocol message.");
    }

    public async Task<JsonObject> InitializeAsync(CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        await WriteAsync(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = id,
            ["request"] = new JsonObject { ["subtype"] = "initialize", ["hooks"] = null },
        }, token);
        while (true)
        {
            JsonObject frame = await ReadAsync(token);
            if (frame["type"]?.GetValue<string>() != "control_response"
                || frame["response"]?["request_id"]?.GetValue<string>() != id)
                continue;
            if (frame["response"]?["subtype"]?.GetValue<string>() != "success")
                throw new InvalidDataException("Claude Code rejected the initialization request.");
            return frame["response"]?["response"] as JsonObject
                ?? throw new InvalidDataException("Claude Code returned no model catalog.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        await _stderrDrain;
        _process.Dispose();
    }
}
