using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Owns one Copilot CLI stdio server. It reads bounded JSON-RPC frames and
/// routes replies separately from session events so host tools can answer calls.
/// </summary>
internal sealed class CopilotRpcProcess : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumBodyBytes = 8 * 1024 * 1024;
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Channel<JsonObject> _events = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Task _readerTask;
    private readonly Task _stderrTask;
    private long _nextId;

    public CopilotRpcProcess(ProcessStartInfo start)
    {
        _process = new Process { StartInfo = start };
        try
        {
            if (!_process.Start())
                throw new IOException("The Copilot CLI did not start.");
            _readerTask = ReadMessagesAsync();
            _stderrTask = DrainErrorsAsync();
        }
        catch
        {
            _process.Dispose();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string workDirectory, bool server)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || executable.IndexOfAny(['\r', '\n', '\0']) >= 0
            || Path.GetExtension(executable) is ".cmd" or ".bat" or ".ps1")
            throw new ArgumentException("Select the installed Copilot executable.", nameof(executable));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
        };
        start.ArgumentList.Add("--no-auto-update");
        if (server)
        {
            start.ArgumentList.Add("--headless");
            start.ArgumentList.Add("--stdio");
        }
        else
        {
            start.ArgumentList.Add("login");
            start.ArgumentList.Add("--web-flow");
        }
        start.Environment.Clear();
        foreach (string name in new[]
        {
            "SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "LOCALAPPDATA",
            "APPDATA", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "PROGRAMDATA",
            "ProgramFiles", "ProgramFiles(x86)", "COMSPEC", "HTTPS_PROXY", "HTTP_PROXY",
            "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR",
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["COPILOT_HOME"] = CopilotRuntimeProfile.HomeDirectory;
        start.Environment["COPILOT_PLUGIN_DIR_ONLY"] = "true";
        start.Environment["COPILOT_MCP_TOOL_CACHE"] = "false";

        return start;
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("A Copilot request identifier was repeated.");
        try
        {
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
            }, cancellationToken);
            JsonObject response = await completion.Task.WaitAsync(cancellationToken);
            if (response["error"] is JsonObject error)
            {
                string code = error["code"]?.ToString() ?? "unknown";
                throw new InvalidOperationException($"The Copilot CLI rejected {method} (RPC {code}). Check the account and CLI version.");
            }
            return response["result"] as JsonObject
                ?? throw new InvalidDataException($"The Copilot CLI returned an invalid {method} result.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask<JsonObject> ReadEventAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAsync(cancellationToken);

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (body.Length > MaximumBodyBytes)
            throw new InvalidDataException("The Copilot protocol message exceeds the size limit.");
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writer.WaitAsync(cancellationToken);
        try
        {
            Stream output = _process.StandardInput.BaseStream;
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(body, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            Stream input = _process.StandardOutput.BaseStream;
            while (true)
            {
                JsonObject message = await ReadFrameAsync(input);
                if (message["method"] is not null && message["id"] is JsonNode requestId)
                {
                    await SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32601,
                            ["message"] = "This host does not authorize this request.",
                        },
                    }, CancellationToken.None);
                    continue;
                }
                if (message["id"] is JsonValue idValue && idValue.TryGetValue(out long id))
                {
                    if (_pending.TryGetValue(id, out TaskCompletionSource<JsonObject>? completion))
                        completion.TrySetResult(message);
                    continue;
                }
                if (message["method"]?.ToString() == "session.event")
                {
                    if (!_events.Writer.TryWrite(message))
                        throw new InvalidDataException("The Copilot CLI sent too many unhandled session events.");
                }
                // Other notifications are ignored. The host does not grant
                // server requests for filesystem, shell, or account actions.
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or InvalidDataException or JsonException or ChannelClosedException or ObjectDisposedException)
        {
            var failure = new IOException("The Copilot CLI closed or returned an invalid protocol frame.", exception);
            foreach (TaskCompletionSource<JsonObject> completion in _pending.Values)
                completion.TrySetException(failure);
            _events.Writer.TryComplete(failure);
        }
    }

    private static async Task<JsonObject> ReadFrameAsync(Stream input)
    {
        byte[] header = new byte[MaximumHeaderBytes];
        byte[] oneByte = new byte[1];
        int length = 0;
        while (true)
        {
            int count = await input.ReadAsync(oneByte);
            if (count == 0)
                throw new EndOfStreamException();
            if (length >= header.Length)
                throw new InvalidDataException("The Copilot protocol header exceeds the size limit.");
            header[length++] = oneByte[0];
            if (length >= 4 && header[length - 4] == '\r' && header[length - 3] == '\n'
                && header[length - 2] == '\r' && header[length - 1] == '\n')
                break;
        }
        string[] lines = Encoding.ASCII.GetString(header, 0, length - 4).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int bodyLength = -1;
        foreach (string line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out bodyLength))
                throw new InvalidDataException("The Copilot protocol content length is invalid.");
        }
        if (bodyLength < 1 || bodyLength > MaximumBodyBytes)
            throw new InvalidDataException("The Copilot protocol body exceeds the size limit.");
        byte[] body = new byte[bodyLength];
        await input.ReadExactlyAsync(body);
        return JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
            ?? throw new InvalidDataException("The Copilot CLI returned a non-object protocol message.");
    }

    private async Task DrainErrorsAsync()
    {
        char[] buffer = new char[4096];
        while (await _process.StandardError.ReadAsync(buffer.AsMemory()) > 0)
        {
            // Stderr can contain account data. Do not retain it.
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
                // A closed input pipe does not prove the process exited.
            }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(termination.Token);
            }
            await Task.WhenAll(_readerTask, _stderrTask).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _writer.Dispose();
            _process.Dispose();
        }
    }
}
