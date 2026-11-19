using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Owns one stdio app-server process. Bounds each protocol message and drains
/// stderr without retaining provider output that can contain account data.
/// </summary>
internal sealed class CodexRpcProcess : IAsyncDisposable
{
    private const int MaximumMessageCharacters = 8 * 1024 * 1024;
    private readonly Process _process;
    private readonly Task _stderrDrain;
    private readonly char[] _readBuffer = new char[4096];
    private int _readOffset;
    private int _readCount;
    private long _nextId;

    public CodexRpcProcess(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo };
        try
        {
            if (!_process.Start())
                throw new IOException("The Codex app server did not start.");
            _stderrDrain = DrainErrorsAsync();
        }
        catch
        {
            _process.Dispose();
            throw;
        }
    }

    public async Task<JsonObject> CallAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken,
        Func<JsonObject, CancellationToken, Task>? onMessage = null)
    {
        long id = ++_nextId;
        await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
        while (true)
        {
            JsonObject message = await ReadAsync(cancellationToken);
            if (message["method"] is not null)
            {
                if (onMessage is not null)
                    await onMessage(message, cancellationToken);
                else if (message["id"] is not null)
                    await DenyAsync(message["id"]!, cancellationToken);
                continue;
            }
            if (message["id"]?.ToString() != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Codex returned an unexpected response identifier.");
            if (message["error"] is JsonObject error)
            {
                // Raw provider errors can include input or account data. Retain only the code.
                string code = error["code"]?.ToString() ?? "unknown";
                throw new InvalidOperationException($"Codex rejected {method} (RPC {code}). Check the selected model, account, and runtime version.");
            }
            return message["result"] as JsonObject
                ?? throw new InvalidDataException($"Codex returned an invalid {method} result.");
        }
    }

    public Task DenyAsync(JsonNode id, CancellationToken cancellationToken)
        => WriteAsync(new JsonObject
        {
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "This host does not authorize this request." },
        }, cancellationToken);

    public async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        string json = message.ToJsonString();
        if (json.Length > MaximumMessageCharacters)
            throw new InvalidDataException("The Codex input exceeds the protocol message limit.");
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task<JsonObject> ReadAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await _process.StandardOutput.ReadAsync(_readBuffer.AsMemory(), cancellationToken);
                _readOffset = 0;
                if (_readCount == 0)
                    throw new IOException("The Codex app server closed its output before the operation completed.");
            }
            int newline = Array.IndexOf(_readBuffer, '\n', _readOffset, _readCount - _readOffset);
            int end = newline < 0 ? _readCount : newline;
            if (line.Length + end - _readOffset > MaximumMessageCharacters)
                throw new InvalidDataException("Codex exceeded the protocol message limit.");
            line.Append(_readBuffer, _readOffset, end - _readOffset);
            _readOffset = newline < 0 ? end : end + 1;
            if (newline < 0)
                continue;
            if (line.Length == 0)
                continue;
            return JsonNode.Parse(line.ToString(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                ?? throw new InvalidDataException("Codex returned a non-object protocol message.");
        }
    }

    /// <summary>Requests native interruption before process teardown. No request is retried.</summary>
    public async Task InterruptAsync(string threadId, string turnId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await CallAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Disposal still terminates this owned process if native interruption fails.
        }
    }

    private async Task DrainErrorsAsync()
    {
        char[] buffer = new char[4096];
        while (await _process.StandardError.ReadAsync(buffer.AsMemory()) > 0)
        {
            // Do not retain stderr. Account responses and startup errors can contain secrets.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                // A broken input pipe does not prove that the process exited.
            }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(grace.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(termination.Token);
            }
            await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _process.Dispose();
        }
    }
}
