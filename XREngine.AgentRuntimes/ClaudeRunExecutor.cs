using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.AgentRuntimes;

/// <summary>Runs the user's Claude Code subscription through the native CLI.</summary>
public sealed class ClaudeRunExecutor(LocalCliRuntimeOptions options) : ISubscriptionAgentRuntime
{
    private readonly LocalCliRuntimeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<SubscriptionConnectionInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using ClaudeRuntimeProfile profile = await ClaudeRuntimeProfile.OpenAsync(_options, false, timeout.Token);
        return await ReadConnectionAsync(profile, timeout.Token);
    }

    public async Task<SubscriptionConnectionInfo> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using ClaudeRuntimeProfile profile = await ClaudeRuntimeProfile.OpenAsync(_options, true, timeout.Token);
        ProcessStartInfo start = profile.CreateStartInfo(_options, "auth", "login");
        start.RedirectStandardInput = false;
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        start.CreateNoWindow = false;
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new Win32Exception("Claude Code did not start its login flow.");
        try
        {
            // Claude Code opens its own browser and receives the provider response.
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Claude Code sign-in did not complete. Use Sign in to Claude to try again.");
            return await ReadConnectionAsync(profile, timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public async Task<AgentRunResult> RunAsync(string runId, AgentRunRequest request,
        IAgentToolProvider toolProvider, IAgentRunObserver? observer = null,
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
        var tools = new AgentRuntimeToolSession(request, toolProvider, observer);
        AgentRunStatus status = AgentRunStatus.Failed;
        AgentFailure? failure = null;
        AgentRuntimeInfo? runtime = null;
        AgentTokenUsage usage = new();
        int turnCount = 0;
        bool turnCountReported = false;
        string actualModel = string.Empty;
        var finalText = new StringBuilder();
        try
        {
            IReadOnlyList<string> errors = AgentRequestValidator.Validate(request);
            if (errors.Count > 0 || request.ExecutionMode != AgentExecutionMode.ClaudeCli)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    errors.Count > 0 ? string.Join("; ", errors) : "Claude Code requires Claude execution mode.");
            if (request.Budget.MaxRetries != 0 || request.Budget.MaxOutputTokens != 0)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "Claude Code cannot enforce the requested retry or output-token cap.");
            await tools.InitializeAsync(token);
            await using var bridge = new AgentRuntimeMcpBridge(tools);
            bridge.Start();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            await using ClaudeRuntimeProfile profile = await ClaudeRuntimeProfile.OpenAsync(_options, false, startup.Token);
            SubscriptionConnectionInfo connection = await ReadConnectionAsync(profile, startup.Token);
            if (!string.IsNullOrWhiteSpace(_options.ExpectedAccountFingerprint)
                && !string.Equals(_options.ExpectedAccountFingerprint, connection.AccountFingerprint, StringComparison.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                    "The Claude account changed. Check the connection and start a new chat.");
            SubscriptionModelInfo model = connection.Models.FirstOrDefault(entry => entry.Id == request.RequestedModel)
                ?? throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "The exact Claude model is not in this account's CLI catalog.");
            if (request.EnforceReasoningEffort
                && !model.SupportedReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "The selected Claude model does not advertise this effort level.");

            var config = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["xrengine-run"] = new JsonObject
                    {
                        ["type"] = "http", ["url"] = bridge.Url,
                        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + bridge.Bearer },
                    },
                },
            };
            var arguments = new List<string>
            {
                "--print", "--output-format", "stream-json", "--input-format", "stream-json",
                "--include-partial-messages", "--verbose", "--model", model.Id,
                "--tools", "", "--allowedTools", "mcp__xrengine-run__*",
                "--mcp-config", config.ToJsonString(), "--strict-mcp-config",
                "--setting-sources=", "--disable-slash-commands", "--permission-mode", "bypassPermissions",
            };
            if (request.EnforceReasoningEffort)
            {
                arguments.Add("--effort");
                arguments.Add(request.ReasoningEffort);
            }
            await using var process = new ClaudeRpcProcess(profile.CreateStartInfo(_options, [.. arguments]));
            JsonObject init = await process.InitializeAsync(startup.Token);
            IReadOnlyList<SubscriptionModelInfo> runModels = ReadModels(init);
            SubscriptionModelInfo runModel = runModels.FirstOrDefault(entry => entry.Id == model.Id)
                ?? throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                    "The selected Claude model disappeared during startup.");
            if (request.EnforceReasoningEffort
                && !runModel.SupportedReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    "The selected Claude effort is no longer advertised by the CLI.");
            runtime = new AgentRuntimeInfo
            {
                Name = "Claude Code CLI", Version = connection.Version,
                AuthenticationMode = connection.AuthenticationMode,
                AccountFingerprint = connection.AccountFingerprint,
                ModelEvidence = "CLI initialize catalog and assistant message model",
                EnforcedControls = request.EnforceReasoningEffort
                    ? ["isolated Claude login", "exact model check", "selected effort flag", "native tools disabled", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"]
                    : ["isolated Claude login", "exact model check", "native tools disabled", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"],
                UnsupportedControls = ["native turn cap", "native retry cap", "hard output-token cap", "actual effort evidence", "text verbosity"],
            };
            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
                prompt.AppendLine(request.SystemInstructions).AppendLine();
            prompt.AppendLine(request.UseCompactHandoffPrompt ? AgentPromptBuilder.Build(request) : request.Objective);
            foreach (AgentContextFileSnapshot snapshot in request.ContextFileSnapshots)
                prompt.AppendLine(AgentContextFileInputBuilder.Build(snapshot)["text"]?.GetValue<string>());
            JsonNode content = BuildPromptContent(prompt.ToString(), request.InitialImageDataUri);
            await process.WriteAsync(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            }, token);
            bool completed = false;
            while (!completed)
            {
                JsonObject frame = await process.ReadAsync(token);
                string type = frame["type"]?.GetValue<string>() ?? string.Empty;
                if (type == "assistant")
                {
                    if (frame["is_api_error_message"]?.GetValue<bool>() == true)
                        throw CreateProviderFailure(frame);
                    string reportedModel = frame["message"]?["model"]?.GetValue<string>() ?? string.Empty;
                    if (reportedModel.Length == 0 || reportedModel != model.Id)
                        throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable,
                            "Claude Code routed the request to a different model. The result was rejected.");
                    actualModel = reportedModel;
                }
                else if (type == "stream_event"
                    && frame["event"]?["type"]?.GetValue<string>() == "content_block_delta"
                    && frame["event"]?["delta"]?["type"]?.GetValue<string>() == "text_delta")
                {
                    string delta = frame["event"]?["delta"]?["text"]?.GetValue<string>() ?? string.Empty;
                    finalText.Append(delta);
                    await observer.OnEventAsync(new AgentRunEvent { Kind = AgentRunEventKind.TextDelta, Message = delta }, token);
                }
                else if (type == "result")
                {
                    if (frame["is_error"]?.GetValue<bool>() == true || frame["subtype"]?.GetValue<string>() != "success")
                        throw CreateProviderFailure(frame);
                    if (actualModel.Length == 0)
                        throw new InvalidDataException("Claude Code did not report the model used for this turn.");
                    if (finalText.Length == 0)
                        finalText.Append(frame["result"]?.GetValue<string>() ?? string.Empty);
                    JsonNode? counts = frame["usage"];
                    usage = new AgentTokenUsage
                    {
                        InputTokens = counts?["input_tokens"]?.GetValue<long>() ?? 0,
                        OutputTokens = counts?["output_tokens"]?.GetValue<long>() ?? 0,
                    };
                    usage = usage with { TotalTokens = usage.InputTokens + usage.OutputTokens };
                    if (frame["num_turns"] is JsonValue turns && turns.TryGetValue(out int reportedTurns))
                    {
                        turnCount = reportedTurns;
                        turnCountReported = true;
                    }
                    completed = true;
                }
                else if (type == "control_request")
                    await process.WriteAsync(new JsonObject
                    {
                        ["type"] = "control_response",
                        ["response"] = new JsonObject
                        {
                            ["subtype"] = "error",
                            ["request_id"] = frame["request_id"]?.GetValue<string>(),
                            ["error"] = "Unsupported host request.",
                        },
                    }, token);
            }
            if (bridge.Failure is { } toolFailure)
                throw toolFailure;
            tools.ValidateCompletion();
            status = AgentRunStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            status = cancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed;
            failure = new AgentFailure
            {
                Category = cancelled ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded,
                Summary = cancelled ? "The Claude run was cancelled." : "Claude Code exceeded its startup or run time limit.",
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
                    ? "Claude Code could not start. Select its native CLI in Assistant settings."
                    : exception is JsonException or InvalidDataException
                        ? "Claude Code returned an invalid protocol message." : exception.Message,
            };
        }
        catch (Exception)
        {
            failure = new AgentFailure { Category = AgentFailureCategory.Internal, Summary = "Claude Code or a host tool could not complete the request." };
        }
        return new AgentRunResult
        {
            RunId = runId, Status = status, Failure = failure,
            RequestedModel = request.RequestedModel, ActualModel = actualModel,
            FinalText = finalText.ToString(), RuntimeInfo = runtime, Usage = usage,
            ToolCallCount = tools.CallCount, ToolEvidence = tools.Evidence.ToArray(),
            TurnCount = turnCount, TurnCountReported = turnCountReported, RetryCountReported = false,
            ElapsedMilliseconds = watch.ElapsedMilliseconds,
        };
    }

    private async Task<SubscriptionConnectionInfo> ReadConnectionAsync(ClaudeRuntimeProfile profile, CancellationToken token)
    {
        string versionText = await ReadCommandAsync(profile, token, "--version");
        string version = versionText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        if (!version.Contains("Claude Code", StringComparison.Ordinal))
            throw new InvalidDataException("The selected executable is not Claude Code.");
        string statusText = await ReadCommandAsync(profile, token, "auth", "status");
        JsonObject account = JsonNode.Parse(statusText) as JsonObject
            ?? throw new InvalidDataException("Claude Code returned invalid account status.");
        if (account["loggedIn"]?.GetValue<bool>() != true
            || account["authMethod"]?.GetValue<string>() != "claude.ai"
            || account["apiProvider"]?.GetValue<string>() != "firstParty")
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Sign in to Claude Code with a Claude subscription in Assistant settings. API-key accounts are not used.");
        string email = account["email"]?.GetValue<string>() ?? string.Empty;
        string organization = account["orgId"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(organization))
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Claude Code did not provide account identity. Check the connection before sending conversation context.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { "claude.ai", email.Trim().ToLowerInvariant(), organization }))));
        var arguments = new[]
        {
            "--print", "--output-format", "stream-json", "--input-format", "stream-json",
            "--verbose", "--tools", "", "--setting-sources=", "--strict-mcp-config",
            "--disable-slash-commands",
        };
        await using var process = new ClaudeRpcProcess(profile.CreateStartInfo(_options, arguments));
        JsonObject init = await process.InitializeAsync(token);
        return new SubscriptionConnectionInfo
        {
            AccountFingerprint = fingerprint,
            Version = version,
            AuthenticationMode = "Claude subscription",
            Models = ReadModels(init),
        };
    }

    private static IReadOnlyList<SubscriptionModelInfo> ReadModels(JsonObject init)
    {
        if (init["models"] is not JsonArray catalog || catalog.Count is < 1 or > 512)
            throw new InvalidDataException("Claude Code did not return an account model catalog.");
        var models = new List<SubscriptionModelInfo>(catalog.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject entry in catalog.OfType<JsonObject>())
        {
            string alias = entry["value"]?.GetValue<string>() ?? string.Empty;
            if (alias == "default")
                continue;
            string id = entry["resolvedModel"]?.GetValue<string>() ?? string.Empty;
            string display = entry["displayName"]?.GetValue<string>() ?? id;
            if (id.Length is < 1 or > 160 || !seen.Add(id))
                throw new InvalidDataException("Claude Code returned an invalid model catalog.");
            var efforts = new List<string>();
            if (entry["supportsEffort"]?.GetValue<bool>() != false
                && entry["supportedEffortLevels"] is JsonArray advertisedEfforts)
            {
                foreach (JsonNode? advertised in advertisedEfforts)
                {
                    if (advertised is JsonValue value && value.TryGetValue(out string? effort)
                        && AgentRequestValidator.IsSupportedReasoningEffort(effort)
                        && !efforts.Contains(effort!, StringComparer.Ordinal))
                        efforts.Add(effort!);
                }
            }
            models.Add(new SubscriptionModelInfo(id, display)
            {
                SupportedReasoningEfforts = efforts,
            });
        }
        if (models.Count == 0)
            throw new InvalidDataException("Claude Code returned no exact models for this account.");
        return models;
    }

    private static JsonNode BuildPromptContent(string text, string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return JsonValue.Create(text)!;
        const string prefix = "data:image/png;base64,";
        if (!image.StartsWith(prefix, StringComparison.Ordinal) || image.Length > 16 * 1024 * 1024)
            throw new AgentToolProviderException(AgentFailureCategory.Validation, "Claude Code requires a bounded PNG screenshot input.");
        return new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64", ["media_type"] = "image/png", ["data"] = image[prefix.Length..],
                },
            },
        };
    }

    private static AgentToolProviderException CreateProviderFailure(JsonObject frame)
    {
        int status = frame["api_error_status"]?.GetValue<int>() ?? 0;
        if (status == 429 || frame["error"]?.GetValue<string>() == "rate_limit")
            return new AgentToolProviderException(AgentFailureCategory.ProviderRateLimit,
                "The Claude subscription usage limit is reached. Check usage in Claude Code and try again after it resets.");
        if (status is 401 or 403)
            return new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Claude Code rejected the current subscription sign-in. Check the account connection.");
        return new AgentToolProviderException(AgentFailureCategory.ProviderError,
            "Claude Code did not complete the request. Check its account and connection status.");
    }

    private async Task<string> ReadCommandAsync(ClaudeRuntimeProfile profile, CancellationToken token, params string[] arguments)
    {
        using var process = new Process { StartInfo = profile.CreateStartInfo(_options, arguments) };
        if (!process.Start())
            throw new Win32Exception("Claude Code did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await process.WaitForExitAsync(token);
            string result = await output;
            await stderr;
            if (result.Length > 64 * 1024)
                throw new InvalidDataException("Claude Code returned an oversized status response.");
            if (process.ExitCode != 0 && arguments is not ["auth", "status"])
                throw new InvalidOperationException("Claude Code could not report its runtime status.");
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
