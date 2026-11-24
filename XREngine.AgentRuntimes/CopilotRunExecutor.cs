using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LocalAgentBroker.Orchestration;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Runs the installed Copilot CLI with its signed-in GitHub account. The host
/// allows only the tools declared for this run and checks the selected model.
/// </summary>
public sealed class CopilotRunExecutor(LocalCliRuntimeOptions options) : ISubscriptionAgentRuntime
{
    private readonly LocalCliRuntimeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<SubscriptionConnectionInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using CopilotRuntimeProfile profile = await CopilotRuntimeProfile.OpenAsync(_options, false, timeout.Token);
        await using var process = await OpenAsync(profile, timeout.Token);
        return await ReadConnectionAsync(process, timeout.Token);
    }

    public async Task<SubscriptionConnectionInfo> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using CopilotRuntimeProfile profile = await CopilotRuntimeProfile.OpenAsync(_options, true, timeout.Token);
        string executable = string.IsNullOrWhiteSpace(_options.ExecutablePath) ? "copilot" : _options.ExecutablePath;
        using var login = new Process
        {
            StartInfo = CopilotRpcProcess.CreateStartInfo(executable, profile.WorkDirectory, server: false),
        };
        if (!login.Start())
            throw new IOException("The Copilot CLI did not start its sign-in flow.");
        try
        {
            // The native web flow opens the browser. Discard its output because
            // the CLI can print account or authorization data.
            login.StandardInput.Close();
            Task output = login.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            Task errors = login.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await login.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
            if (login.ExitCode != 0)
                throw new InvalidOperationException("Copilot sign-in did not complete. Check the browser authorization and try again.");
        }
        finally
        {
            if (!login.HasExited)
            {
                login.Kill(entireProcessTree: true);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await login.WaitForExitAsync(stop.Token);
            }
        }
        await using var process = await OpenAsync(profile, timeout.Token);
        return await ReadConnectionAsync(process, timeout.Token);
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
        Stopwatch elapsed = Stopwatch.StartNew();
        var toolSession = new AgentRuntimeToolSession(request, toolProvider, observer);
        var content = new StringBuilder();
        AgentFailure? failure = null;
        AgentRunStatus status = AgentRunStatus.Failed;
        string actualModel = string.Empty;
        SubscriptionConnectionInfo? connection = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            IReadOnlyList<string> errors = AgentRequestValidator.Validate(request);
            if (errors.Count > 0 || request.ExecutionMode != AgentExecutionMode.CopilotCli)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    errors.Count > 0 ? string.Join("; ", errors) : "The Copilot executor requires Copilot CLI mode.");
            if (request.Budget.MaxTurns != 0 || request.Budget.MaxRetries != 0 || request.Budget.MaxOutputTokens != 0)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "Copilot CLI requires zero native turn, retry, and output-token caps.");
            if (request.Budget.MaxElapsedSeconds > 0)
                lifetime.CancelAfter(TimeSpan.FromSeconds(request.Budget.MaxElapsedSeconds));

            await toolSession.InitializeAsync(lifetime.Token);
            await using CopilotRuntimeProfile profile = await CopilotRuntimeProfile.OpenAsync(_options, false, lifetime.Token);
            await using var process = await OpenAsync(profile, lifetime.Token);
            connection = await ReadConnectionAsync(process, lifetime.Token);
            if (!string.IsNullOrWhiteSpace(_options.ExpectedAccountFingerprint)
                && connection.AccountFingerprint != _options.ExpectedAccountFingerprint)
                throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                    "The Copilot account changed. Start a new chat before sending context to it.");
            SubscriptionModelInfo model = connection.Models.FirstOrDefault(model => model.Id == request.RequestedModel)
                ?? throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "The selected model is not available from this Copilot subscription.");
            if (request.EnforceReasoningEffort
                && !model.SupportedReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "The selected Copilot model does not advertise this reasoning effort.");

            JsonObject session = await process.CallAsync("session.create",
                CreateSessionParameters(request, toolSession.Tools), lifetime.Token);
            string sessionId = ReadRequiredString(session, "sessionId");
            JsonObject currentModel = await process.CallAsync("session.model.getCurrent",
                new JsonObject { ["sessionId"] = sessionId }, lifetime.Token);
            actualModel = currentModel["modelId"]?.ToString() ?? string.Empty;
            if (actualModel != request.RequestedModel)
                throw new AgentToolProviderException(AgentFailureCategory.ModelSubstitution,
                    "The Copilot CLI selected a different model before the request started.");

            JsonObject send = new()
            {
                ["sessionId"] = sessionId,
                ["prompt"] = BuildPrompt(request),
            };
            if (request.InitialImageDataUri is { } image)
                send["attachments"] = CreateImageAttachments(image);
            Task<JsonObject> responseTask = process.CallAsync("session.sendAndWait", send, lifetime.Token);
            using var eventStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            while (!responseTask.IsCompleted)
            {
                Task<JsonObject> eventTask = process.ReadEventAsync(eventStop.Token).AsTask();
                Task completed = await Task.WhenAny(responseTask, eventTask);
                if (completed == responseTask)
                {
                    eventStop.Cancel();
                    break;
                }
                AgentToolProviderException? toolFailure = await HandleEventAsync(
                    process, await eventTask, sessionId, toolSession, observer, content, lifetime.Token);
                if (toolFailure is not null)
                    throw toolFailure;
            }
            JsonObject response = await responseTask;
            if (response["aborted"]?.GetValue<bool>() == true)
                throw new OperationCanceledException("Copilot stopped the request before completion.");
            if (response["response"]?["data"]?["content"] is JsonValue finalValue)
            {
                string final = finalValue.GetValue<string>();
                content.Clear();
                content.Append(final);
            }
            JsonObject finishedModel = await process.CallAsync("session.model.getCurrent",
                new JsonObject { ["sessionId"] = sessionId }, lifetime.Token);
            actualModel = finishedModel["modelId"]?.ToString() ?? string.Empty;
            if (actualModel != request.RequestedModel)
                throw new AgentToolProviderException(AgentFailureCategory.ModelSubstitution,
                    "The Copilot CLI changed models during the request.");
            toolSession.ValidateCompletion();
            status = AgentRunStatus.Completed;
        }
        catch (AgentToolProviderException exception)
        {
            failure = new AgentFailure { Category = exception.Category, Summary = exception.Message };
        }
        catch (OperationCanceledException)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            status = cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            failure = new AgentFailure
            {
                Category = cancelled ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded,
                Summary = cancelled ? "The Copilot request was canceled." : "The Copilot request exceeded its startup or run time limit.",
            };
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidDataException or InvalidOperationException)
        {
            failure = new AgentFailure
            {
                Category = exception is Win32Exception or IOException ? AgentFailureCategory.Transport : AgentFailureCategory.ProviderError,
                Summary = exception is Win32Exception ? "Install the Copilot CLI and sign in to your GitHub account." : exception.Message,
            };
        }
        catch (Exception)
        {
            failure = new AgentFailure
            {
                Category = AgentFailureCategory.Internal,
                Summary = "The Copilot CLI or host tool could not complete the request.",
            };
        }
        return new AgentRunResult
        {
            RunId = runId, Status = status, RequestedModel = request.RequestedModel,
            ActualModel = actualModel, FinalText = content.ToString(),
            ToolCallCount = toolSession.CallCount, ToolEvidence = toolSession.Evidence.ToArray(),
            TurnCountReported = false, RetryCountReported = false,
            RuntimeInfo = connection is null ? null : new AgentRuntimeInfo
            {
                Name = "Copilot CLI", Version = connection.Version,
                AuthenticationMode = connection.AuthenticationMode,
                AccountFingerprint = connection.AccountFingerprint,
                ModelEvidence = "account catalog and session.model.getCurrent before and after the request",
                EnforcedControls = request.EnforceReasoningEffort
                    ? ["signed-in account", "exact model", "selected reasoning effort", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"]
                    : ["signed-in account", "exact model", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"],
                UnsupportedControls = ["native model-turn limit", "native transport retry limit", "hard output-token cap", "actual reasoning effort evidence", "text verbosity"],
            },
            Failure = failure, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
        };
    }

    private async Task<CopilotRpcProcess> OpenAsync(CopilotRuntimeProfile profile, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        string executable = string.IsNullOrWhiteSpace(_options.ExecutablePath) ? "copilot" : _options.ExecutablePath;
        var process = new CopilotRpcProcess(CopilotRpcProcess.CreateStartInfo(executable, profile.WorkDirectory, server: true));
        try
        {
            JsonObject handshake = await process.CallAsync("connect", new JsonObject
            {
                ["supportedTaskKinds"] = new JsonArray("agent"),
            }, startup.Token);
            if (handshake["protocolVersion"] is not JsonValue protocol
                || !protocol.TryGetValue(out int version) || version < 1)
                throw new InvalidDataException("The Copilot CLI did not provide a supported protocol version.");
            return process;
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    private static async Task<SubscriptionConnectionInfo> ReadConnectionAsync(CopilotRpcProcess process, CancellationToken token)
    {
        JsonObject auth = await process.CallAsync("auth.getStatus", new JsonObject(), token);
        string? authType = auth["authType"]?.ToString();
        if (auth["isAuthenticated"]?.GetValue<bool>() != true || authType is not ("user" or "gh-cli"))
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Copilot subscription access needs a signed-in GitHub account in the Copilot CLI.");
        string? login = auth["login"]?.ToString();
        string? host = auth["host"]?.ToString();
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(host))
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Copilot did not provide account identity. Check the CLI sign-in.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host + "\n" + login)));
        JsonObject catalog = await process.CallAsync("models.list", new JsonObject(), token);
        if (catalog["models"] is not JsonArray models)
            throw new InvalidDataException("Copilot did not return a model list.");
        var available = new List<SubscriptionModelInfo>();
        foreach (JsonNode? item in models)
        {
            if (item is not JsonObject model || model["provider"] is null
                || model["policy"]?["state"]?.ToString() == "disabled")
                continue;
            string? id = model["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id) || id == "auto" || id.Length > 512)
                continue;
            string name = model["name"]?.ToString() ?? id;
            if (name.Length > 512)
                name = id;
            var efforts = new List<string>();
            if (model["supportedReasoningEfforts"] is JsonArray advertisedEfforts)
            {
                foreach (JsonNode? advertised in advertisedEfforts)
                {
                    if (advertised is JsonValue value && value.TryGetValue(out string? effort)
                        && AgentRequestValidator.IsSupportedReasoningEffort(effort)
                        && !efforts.Contains(effort!, StringComparer.Ordinal))
                        efforts.Add(effort!);
                }
            }
            string? defaultEffort = model["defaultReasoningEffort"]?.ToString();
            available.Add(new SubscriptionModelInfo(id, name)
            {
                SupportedReasoningEfforts = efforts,
                DefaultReasoningEffort = defaultEffort is not null && efforts.Contains(defaultEffort, StringComparer.Ordinal)
                    ? defaultEffort : null,
            });
            if (available.Count > 4096)
                throw new InvalidDataException("Copilot returned too many account models.");
        }
        JsonObject status = await process.CallAsync("status.get", new JsonObject(), token);
        return new SubscriptionConnectionInfo
        {
            AccountFingerprint = fingerprint,
            Version = status["version"]?.ToString() ?? "unknown",
            AuthenticationMode = "GitHub account",
            Models = available.OrderBy(static model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static JsonObject CreateSessionParameters(AgentRunRequest? request, IReadOnlyCollection<AgentToolDefinition> tools)
    {
        var declarations = new JsonArray();
        var allowlist = new JsonArray();
        foreach (AgentToolDefinition tool in tools)
        {
            declarations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.InputSchemaJson),
                ["skipPermission"] = true,
                ["overridesBuiltInTool"] = true,
            });
            allowlist.Add("custom:" + tool.Name);
        }
        var parameters = new JsonObject
        {
            ["model"] = request?.RequestedModel,
            ["tools"] = declarations,
            ["availableTools"] = allowlist,
            ["toolFilterPrecedence"] = "excluded",
            ["streaming"] = true,
            ["enableSessionTelemetry"] = false,
            ["enableConfigDiscovery"] = false,
            ["enableSkills"] = false,
            ["enableFileHooks"] = false,
            ["enableHostGitOperations"] = false,
            ["enableSessionStore"] = false,
            ["enableOnDemandInstructionDiscovery"] = false,
            ["skipEmbeddingRetrieval"] = true,
            ["customAgentsLocalOnly"] = true,
            ["mcpOAuthTokenStorage"] = "in-memory",
            ["memory"] = new JsonObject { ["enabled"] = false },
            ["systemMessage"] = new JsonObject
            {
                ["mode"] = "append", ["content"] = request?.SystemInstructions ?? string.Empty,
            },
        };
        if (request?.EnforceReasoningEffort == true)
            parameters["reasoningEffort"] = request.ReasoningEffort;
        return parameters;
    }

    private static JsonArray CreateImageAttachments(string dataUri)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUri.Length > 4 * 1024 * 1024
            || !dataUri.StartsWith(prefix, StringComparison.Ordinal))
            throw new AgentToolProviderException(AgentFailureCategory.Validation,
                "Copilot requires a PNG screenshot smaller than 4 MiB.");
        return [new JsonObject
        {
            ["type"] = "blob", ["mimeType"] = "image/png", ["data"] = dataUri[prefix.Length..],
        }];
    }

    private static string BuildPrompt(AgentRunRequest request)
    {
        var prompt = new StringBuilder(request.UseCompactHandoffPrompt
            ? AgentPromptBuilder.Build(request) : request.Objective);
        if (!request.UseCompactHandoffPrompt && !string.IsNullOrWhiteSpace(request.AdditionalInstructions))
            prompt.AppendLine().AppendLine("Additional caller instructions:")
                .AppendLine(request.AdditionalInstructions.Trim());
        foreach (AgentContextFileSnapshot snapshot in request.ContextFileSnapshots)
            prompt.AppendLine().AppendLine(AgentContextFileInputBuilder.Build(snapshot)["text"]?.GetValue<string>());
        return prompt.ToString();
    }

    private static async Task<AgentToolProviderException?> HandleEventAsync(
        CopilotRpcProcess process,
        JsonObject notification,
        string sessionId,
        AgentRuntimeToolSession tools,
        IAgentRunObserver observer,
        StringBuilder text,
        CancellationToken token)
    {
        if (notification["params"] is not JsonObject parameters
            || parameters["sessionId"]?.ToString() != sessionId
            || parameters["event"] is not JsonObject activity
            || activity["agentId"] is not null)
            return null;
        string? type = activity["type"]?.ToString();
        JsonObject? data = activity["data"] as JsonObject;
        if (type == "assistant.message_delta")
        {
            string chunk = data?["deltaContent"]?.ToString() ?? string.Empty;
            text.Append(chunk);
            await observer.OnEventAsync(new AgentRunEvent { Kind = AgentRunEventKind.TextDelta, Message = chunk }, token);
        }
        else if (type == "external_tool.requested" && data is not null)
        {
            string requestId = ReadRequiredString(data, "requestId");
            string name = ReadRequiredString(data, "toolName");
            var response = new JsonObject { ["sessionId"] = sessionId, ["requestId"] = requestId };
            AgentToolProviderException? policyFailure = null;
            try
            {
                AgentToolResult result = await tools.ExecuteAsync(new AgentToolCall
                {
                    CallId = ReadRequiredString(data, "toolCallId"),
                    Name = name,
                    ArgumentsJson = data["arguments"]?.ToJsonString() ?? "{}",
                }, token);
                var body = new JsonObject
                {
                    ["textResultForLlm"] = result.Content,
                    ["resultType"] = result.IsError ? "failure" : "success",
                };
                if (result.ImageDataUri is { } image)
                {
                    int marker = image.IndexOf(";base64,", StringComparison.Ordinal);
                    if (marker > 0 && image.StartsWith("data:image/", StringComparison.Ordinal))
                        body["binaryResultsForLlm"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "image", ["mimeType"] = image[5..marker],
                            ["data"] = image[(marker + 8)..],
                        });
                }
                response["result"] = body;
            }
            catch (AgentToolProviderException exception)
            {
                response["error"] = exception.Message;
                policyFailure = exception;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                response["error"] = "The host tool failed.";
                policyFailure = new AgentToolProviderException(AgentFailureCategory.ToolError,
                    "The host tool failed. Check the editor or repository tool status.");
            }
            await process.CallAsync("session.tools.handlePendingToolCall", response, token);
            return policyFailure;
        }
        else if (type == "session.error")
            throw new InvalidOperationException("The Copilot CLI reported a session error.");
        return null;
    }

    private static string ReadRequiredString(JsonObject source, string name)
        => source[name]?.ToString() is { Length: > 0 } value ? value
            : throw new InvalidDataException($"Copilot omitted {name} from a protocol response.");
}
