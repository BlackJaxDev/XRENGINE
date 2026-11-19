using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Runs an authenticated Gemini CLI ACP session with exact model and host tool checks.
/// </summary>
public sealed class GeminiRunExecutor(LocalCliRuntimeOptions options) : ISubscriptionAgentRuntime
{
    private readonly LocalCliRuntimeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<SubscriptionConnectionInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using GeminiRuntimeProfile profile = await GeminiRuntimeProfile.OpenAsync(_options, false, timeout.Token);
        string version = await profile.ReadVersionAsync(_options, timeout.Token);
        await profile.ReadAccountFingerprintAsync(timeout.Token);
        await using var process = new GeminiRpcProcess(profile.CreateStartInfo(_options, "--acp"));
        await InitializeAsync(process, timeout.Token);
        JsonObject session = await NewSessionAsync(process, profile, null, timeout.Token);
        return await ReadConnectionAsync(profile, session, version, timeout.Token);
    }

    public async Task<SubscriptionConnectionInfo> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using GeminiRuntimeProfile profile = await GeminiRuntimeProfile.OpenAsync(_options, true, timeout.Token);
        string version = await profile.ReadVersionAsync(_options, timeout.Token);
        await using var process = new GeminiRpcProcess(profile.CreateStartInfo(_options, "--acp"));
        await InitializeAsync(process, timeout.Token);
        // Gemini CLI owns its Google browser flow. No authorization token enters the host.
        await process.CallAsync("authenticate", new JsonObject { ["methodId"] = "oauth-personal" }, timeout.Token);
        JsonObject session = await NewSessionAsync(process, profile, null, timeout.Token);
        return await ReadConnectionAsync(profile, session, version, timeout.Token);
    }

    public async Task<AgentRunResult> RunAsync(
        string runId,
        AgentRunRequest request,
        IAgentToolProvider toolProvider,
        IAgentRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolProvider);
        observer ??= NullAgentRunObserver.Instance;
        var watch = Stopwatch.StartNew();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Budget.MaxElapsedSeconds > 0)
            lifetime.CancelAfter(TimeSpan.FromSeconds(request.Budget.MaxElapsedSeconds));
        CancellationToken token = lifetime.Token;
        var toolSession = new AgentRuntimeToolSession(request, toolProvider, observer);
        AgentRunStatus status = AgentRunStatus.Failed;
        AgentFailure? failure = null;
        AgentRuntimeInfo? runtime = null;
        AgentTokenUsage usage = new();
        string actualModel = string.Empty;
        var text = new StringBuilder();
        try
        {
            IReadOnlyList<string> errors = AgentRequestValidator.Validate(request);
            if (errors.Count > 0 || request.ExecutionMode != AgentExecutionMode.GeminiCli)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    errors.Count > 0 ? string.Join("; ", errors) : "Gemini CLI requires Gemini execution mode.");
            if (request.Budget.MaxTurns != 0 || request.Budget.MaxRetries != 0 || request.Budget.MaxOutputTokens != 0)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "Gemini CLI cannot enforce the requested native turn, retry, or output-token cap.");
            if (request.RequestedModel == "auto")
                throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "Choose an exact Gemini model from the account list.");

            await toolSession.InitializeAsync(token);
            await using var bridge = new AgentRuntimeMcpBridge(toolSession);
            bridge.Start();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            await using GeminiRuntimeProfile profile = await GeminiRuntimeProfile.OpenAsync(_options, false, startup.Token);
            string version = await profile.ReadVersionAsync(_options, startup.Token);
            await profile.ReadAccountFingerprintAsync(startup.Token);
            await using var process = new GeminiRpcProcess(profile.CreateStartInfo(_options, "--acp"));
            await InitializeAsync(process, startup.Token);
            JsonObject session = await NewSessionAsync(process, profile, bridge, startup.Token);
            SubscriptionConnectionInfo connection = await ReadConnectionAsync(profile, session, version, startup.Token);
            if (!string.IsNullOrWhiteSpace(_options.ExpectedAccountFingerprint)
                && !string.Equals(_options.ExpectedAccountFingerprint, connection.AccountFingerprint, StringComparison.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                    "The Gemini account changed. Check the connection and start a new chat.");
            if (!connection.Models.Any(model => string.Equals(model.Id, request.RequestedModel, StringComparison.Ordinal)))
                throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "The exact Gemini model is not in this account's CLI catalog.");
            if (request.EnforceReasoningEffort)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "This Gemini connection does not advertise a reasoning effort control.");
            string sessionId = session["sessionId"]?.GetValue<string>()
                ?? throw new InvalidDataException("Gemini did not return an ACP session ID.");
            await process.CallAsync("session/set_model", new JsonObject
            {
                ["sessionId"] = sessionId, ["modelId"] = request.RequestedModel,
            }, startup.Token);
            actualModel = request.RequestedModel;
            runtime = new AgentRuntimeInfo
            {
                Name = "Gemini CLI ACP", Version = version, AuthenticationMode = "Google login",
                AccountFingerprint = connection.AccountFingerprint,
                ModelEvidence = "ACP session model catalog, set_model, and per-model prompt usage",
                EnforcedControls = ["isolated Google login", "exact model check", "native tools disabled", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"],
                UnsupportedControls = ["native turn cap", "native retry cap", "hard output-token cap", "reasoning effort", "text verbosity"],
            };

            var prompt = new JsonArray();
            var body = new StringBuilder("Follow the editor request below. Use only the tools supplied by the editor.\n\n");
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
                body.AppendLine(request.SystemInstructions).AppendLine();
            body.AppendLine(request.UseCompactHandoffPrompt ? AgentPromptBuilder.Build(request) : request.Objective);
            foreach (AgentContextFileSnapshot snapshot in request.ContextFileSnapshots)
                body.AppendLine(AgentContextFileInputBuilder.Build(snapshot)["text"]?.GetValue<string>());
            prompt.Add(new JsonObject { ["type"] = "text", ["text"] = body.ToString() });
            if (!string.IsNullOrWhiteSpace(request.InitialImageDataUri))
            {
                const string prefix = "data:image/png;base64,";
                if (!request.InitialImageDataUri.StartsWith(prefix, StringComparison.Ordinal)
                    || request.InitialImageDataUri.Length > 16 * 1024 * 1024)
                    throw new AgentToolProviderException(AgentFailureCategory.Validation,
                        "Gemini requires a bounded PNG screenshot input.");
                prompt.Add(new JsonObject
                {
                    ["type"] = "image", ["mimeType"] = "image/png",
                    ["data"] = request.InitialImageDataUri[prefix.Length..],
                });
            }

            async Task OnMessage(JsonObject message, CancellationToken callbackToken)
            {
                string method = message["method"]?.GetValue<string>() ?? string.Empty;
                if (method == "session/request_permission" && message["id"] is { } id)
                {
                    string? server = message["params"]?["toolCall"]?["_meta"]?["gemini-cli"]?["mcp"]?["server_name"]?.GetValue<string>();
                    string? name = message["params"]?["toolCall"]?["_meta"]?["gemini-cli"]?["mcp"]?["tool_name"]?.GetValue<string>();
                    bool allowed = server == "xrengine-run" && toolSession.Tools.Any(tool => tool.Name == name);
                    JsonNode outcome = new JsonObject { ["outcome"] = "cancelled" };
                    if (allowed && message["params"]?["options"] is JsonArray options)
                    {
                        string? optionId = options.OfType<JsonObject>()
                            .FirstOrDefault(option => option["kind"]?.GetValue<string>() == "allow_once")?["optionId"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(optionId))
                            outcome = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId };
                    }
                    await process.WriteAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                        ["result"] = new JsonObject { ["outcome"] = outcome },
                    }, callbackToken);
                    return;
                }
                if (method != "session/update")
                {
                    if (message["id"] is { } unknownId)
                        await process.DenyAsync(unknownId, callbackToken);
                    return;
                }
                JsonNode? update = message["params"]?["update"];
                if (update?["sessionUpdate"]?.GetValue<string>() != "agent_message_chunk")
                    return;
                string delta = update["content"]?["text"]?.GetValue<string>() ?? string.Empty;
                if (delta.Length == 0)
                    return;
                text.Append(delta);
                await observer.OnEventAsync(new AgentRunEvent
                {
                    Kind = AgentRunEventKind.TextDelta, Message = delta,
                }, callbackToken);
            }

            JsonObject response = await process.CallAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId, ["prompt"] = prompt,
            }, token, OnMessage);
            if (response["stopReason"]?.GetValue<string>() != "end_turn")
                throw new AgentToolProviderException(AgentFailureCategory.ProviderError,
                    "Gemini did not finish its model turn.");
            JsonArray modelUsage = response["_meta"]?["quota"]?["model_usage"] as JsonArray
                ?? throw new InvalidDataException("Gemini did not report the model used for this turn.");
            if (modelUsage.Count == 0 || modelUsage.OfType<JsonObject>()
                .Any(entry => entry["model"]?.GetValue<string>() != request.RequestedModel))
                throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "Gemini routed the request to a different model. The result was rejected.");
            JsonNode? counts = response["usage"];
            usage = new AgentTokenUsage
            {
                InputTokens = counts?["inputTokens"]?.GetValue<long>() ?? 0,
                OutputTokens = counts?["outputTokens"]?.GetValue<long>() ?? 0,
                TotalTokens = counts?["totalTokens"]?.GetValue<long>() ?? 0,
            };
            if (bridge.Failure is { } toolFailure)
                throw toolFailure;
            toolSession.ValidateCompletion();
            status = AgentRunStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            bool wasCancelled = cancellationToken.IsCancellationRequested;
            status = wasCancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            failure = new AgentFailure
            {
                Category = wasCancelled ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded,
                Summary = wasCancelled ? "The Gemini run was cancelled." : "Gemini exceeded its startup or run time limit.",
            };
        }
        catch (AgentToolProviderException exception)
        {
            failure = new AgentFailure { Category = exception.Category, Summary = exception.Message };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or Win32Exception or JsonException or ArgumentException)
        {
            failure = new AgentFailure
            {
                Category = exception is Win32Exception ? AgentFailureCategory.Transport : AgentFailureCategory.ProviderError,
                Summary = exception is Win32Exception
                    ? "Gemini CLI could not start. Select an installed CLI and check the Google sign-in."
                    : exception is JsonException or InvalidDataException
                        ? "Gemini returned an invalid protocol message."
                        : exception.Message,
            };
        }
        catch (Exception)
        {
            failure = new AgentFailure { Category = AgentFailureCategory.Internal, Summary = "Gemini or a host tool could not complete the request." };
        }

        return new AgentRunResult
        {
            RunId = runId, Status = status, Failure = failure,
            RequestedModel = request.RequestedModel, ActualModel = actualModel,
            FinalText = text.ToString(), RuntimeInfo = runtime, Usage = usage,
            ToolCallCount = toolSession.CallCount, ToolEvidence = toolSession.Evidence.ToArray(),
            TurnCountReported = false, RetryCountReported = false,
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
        };
    }

    private static async Task InitializeAsync(GeminiRpcProcess process, CancellationToken token)
    {
        JsonObject response = await process.CallAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "XRENGINE", ["version"] = "1" },
        }, token);
        if (response["protocolVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Gemini returned an unsupported ACP protocol version.");
    }

    private static Task<JsonObject> NewSessionAsync(GeminiRpcProcess process, GeminiRuntimeProfile profile, AgentRuntimeMcpBridge? bridge, CancellationToken token)
    {
        var servers = new JsonArray();
        if (bridge is not null)
            servers.Add(new JsonObject
            {
                ["name"] = "xrengine-run", ["type"] = "http", ["url"] = bridge.Url,
                ["headers"] = new JsonArray(new JsonObject { ["name"] = "Authorization", ["value"] = "Bearer " + bridge.Bearer }),
            });
        return process.CallAsync("session/new", new JsonObject
        {
            ["cwd"] = profile.WorkDirectory,
            ["mcpServers"] = servers,
        }, token);
    }

    private static async Task<SubscriptionConnectionInfo> ReadConnectionAsync(GeminiRuntimeProfile profile, JsonObject session, string version, CancellationToken token)
    {
        JsonArray available = session["models"]?["availableModels"] as JsonArray
            ?? throw new InvalidDataException("Gemini did not return its account model catalog.");
        if (available.Count is < 1 or > 512)
            throw new InvalidDataException("Gemini returned an unsupported model catalog size.");
        var models = new List<SubscriptionModelInfo>(available.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject item in available.OfType<JsonObject>())
        {
            string? id = item["modelId"]?.GetValue<string>();
            string? name = item["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id == "auto" || !seen.Add(id))
                continue;
            models.Add(new SubscriptionModelInfo(id, string.IsNullOrWhiteSpace(name) ? id : name));
        }
        if (models.Count == 0)
            throw new InvalidDataException("Gemini did not offer an exact selectable model.");
        return new SubscriptionConnectionInfo
        {
            AccountFingerprint = await profile.ReadAccountFingerprintAsync(token),
            AuthenticationMode = "Google login", Version = version, Models = models,
        };
    }
}
