using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Runs Codex through its native app-server protocol using the user's ChatGPT
/// login. Host tools remain subject to XRENGINE authorization and evidence rules.
/// </summary>
public sealed class CodexRunExecutor(CodexRuntimeOptions options) : IAgentRunExecutor
{
    private readonly CodexRuntimeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Starts Codex's own browser login in the isolated XRENGINE profile. The
    /// callback opens a provider authorization URL; no token passes through it.
    /// </summary>
    public async Task<CodexConnectionInfo> SignInAsync(Action<Uri> openAuthorizationPage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openAuthorizationPage);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using CodexRuntimeProfile profile = await CodexRuntimeProfile.OpenAsync(_options, timeout.Token, signingIn: true);
        JsonObject login = await profile.Process.CallAsync("account/login/start", new JsonObject { ["type"] = "chatgpt" }, timeout.Token);
        string loginId = login["loginId"]?.GetValue<string>() ?? throw new InvalidDataException("Codex did not return a login identifier.");
        if (!Uri.TryCreate(login["authUrl"]?.GetValue<string>(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.Host is not ("auth.openai.com" or "auth0.openai.com" or "chatgpt.com"))
            throw new InvalidDataException("Codex returned an unsupported sign-in address.");
        try
        {
            openAuthorizationPage(uri);
            while (true)
            {
                JsonObject message = await profile.Process.ReadAsync(timeout.Token);
                if (message["id"] is { } id)
                    await profile.Process.DenyAsync(id, timeout.Token);
                if (message["method"]?.GetValue<string>() != "account/login/completed"
                    || message["params"]?["loginId"]?.GetValue<string>() != loginId)
                    continue;
                if (message["params"]?["success"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("ChatGPT sign-in did not complete. Use Sign in to ChatGPT to try again.");
                return await ReadConnectionAsync(profile.Process, profile.Version, timeout.Token);
            }
        }
        finally
        {
            if (timeout.IsCancellationRequested)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await profile.Process.CallAsync("account/login/cancel", new JsonObject { ["loginId"] = loginId }, cancel.Token);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
                {
                    // The owned process still closes when this sign-in operation is disposed.
                }
            }
        }
    }

    /// <summary>Checks the runtime account and model catalog without sending a generation request.</summary>
    public async Task<CodexConnectionInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using CodexRuntimeProfile profile = await CodexRuntimeProfile.OpenAsync(_options, timeout.Token);
        return await ReadConnectionAsync(profile.Process, profile.Version, timeout.Token);
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = lifetime.Token;
        var toolSession = new AgentRuntimeToolSession(request, toolProvider, observer);
        var state = new CodexRunState(request, toolSession, observer);
        CodexRuntimeProfile? profile = null;
        AgentRunStatus status = AgentRunStatus.Failed;
        AgentFailure? failure = null;
        try
        {
            IReadOnlyList<string> errors = AgentRequestValidator.Validate(request);
            if (errors.Count > 0 || request.ExecutionMode != AgentExecutionMode.CodexAppServer)
                throw new AgentToolProviderException(AgentFailureCategory.Validation,
                    errors.Count > 0 ? string.Join("; ", errors) : "The Codex executor requires Codex execution mode.");

            if (request.Budget.MaxElapsedSeconds > 0)
                lifetime.CancelAfter(TimeSpan.FromSeconds(request.Budget.MaxElapsedSeconds));

            await toolSession.InitializeAsync(token);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            profile = await CodexRuntimeProfile.OpenAsync(_options, startup.Token);
            state.Process = profile.Process;
            CodexConnectionInfo connection = await ReadConnectionAsync(profile.Process, profile.Version, startup.Token);
            state.RuntimeInfo = new AgentRuntimeInfo
            {
                Name = "Codex app-server",
                Version = connection.Version,
                AuthenticationMode = connection.AuthenticationMode,
                AccountFingerprint = connection.AccountFingerprint,
                ModelEvidence = "thread/start response and model/rerouted notifications",
                EnforcedControls = ["ChatGPT account", "exact model", "host tool policy", "tool count", "tool result bytes", "elapsed time", "owned process cancellation"],
                UnsupportedControls = ["native model-turn limit", "native transport retry limit", "hard output-token cap"],
            };
            if (!string.IsNullOrWhiteSpace(_options.ExpectedAccountFingerprint)
                && !string.Equals(_options.ExpectedAccountFingerprint, connection.AccountFingerprint, StringComparison.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                    "The Codex account changed. Clear the conversation before sending it to this account.");
            CodexModelInfo? model = connection.Models.FirstOrDefault(model => model.Id == request.RequestedModel);
            if (model is null)
                throw new AgentToolProviderException(AgentFailureCategory.ModelUnavailable, "The exact requested model is not available in the Codex account catalog.");
            if (!request.UseProviderDefaultReasoningEffort
                && !model.SupportedReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal))
                throw new AgentToolProviderException(AgentFailureCategory.Validation, "The requested reasoning effort is not supported by this Codex model.");

            await profile.PrepareModelAsync(request.RequestedModel, startup.Token);
            state.Process = profile.Process;
            CodexConnectionInfo isolatedConnection = await ReadConnectionAsync(profile.Process, profile.Version, startup.Token);
            if (connection.AccountFingerprint != isolatedConnection.AccountFingerprint)
                throw new AgentToolProviderException(AgentFailureCategory.Authentication, "The Codex account changed during startup. Retry after checking the connection.");

            JsonObject thread = await profile.Process.CallAsync("thread/start",
                profile.CreateThreadParameters(request, toolSession.Tools), startup.Token, state.OnMessageAsync);
            state.ThreadId = thread["thread"]?["id"]?.GetValue<string>()
                ?? throw new InvalidDataException("Codex did not return a thread identifier.");
            state.ActualModel = thread["model"]?.GetValue<string>() ?? string.Empty;
            state.VerifyModel();
            profile.VerifyThread(thread);
            await observer.OnEventAsync(new AgentRunEvent
            {
                Kind = AgentRunEventKind.Diagnostic,
                Message = $"Codex {connection.Version}; ChatGPT account; model {state.ActualModel}. Native turn/retry counts and output-token caps are unavailable.",
            }, token);

            var input = new JsonArray();
            string prompt = request.UseCompactHandoffPrompt ? AgentPromptBuilder.Build(request) : request.Objective;
            input.Add(new JsonObject { ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray() });
            foreach (AgentContextFileSnapshot snapshot in request.ContextFileSnapshots)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "text", ["text"] = AgentContextFileInputBuilder.Build(snapshot)["text"]!.GetValue<string>(),
                    ["text_elements"] = new JsonArray(),
                });
            }
            if (!string.IsNullOrWhiteSpace(request.InitialImageDataUri))
            {
                ValidateImage(request.InitialImageDataUri);
                input.Add(new JsonObject { ["type"] = "image", ["url"] = request.InitialImageDataUri });
            }
            var turnParameters = new JsonObject
            {
                ["threadId"] = state.ThreadId, ["input"] = input, ["model"] = request.RequestedModel,
                ["environments"] = new JsonArray(),
            };
            if (!request.UseProviderDefaultReasoningEffort)
                turnParameters["effort"] = request.ReasoningEffort;
            JsonObject turn = await profile.Process.CallAsync("turn/start", turnParameters, token, state.OnMessageAsync);
            state.TurnId = turn["turn"]?["id"]?.GetValue<string>() ?? state.TurnId;
            while (!state.Completed)
                await state.OnMessageAsync(await profile.Process.ReadAsync(token), token);
            toolSession.ValidateCompletion();
            status = AgentRunStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            bool timeout = !cancellationToken.IsCancellationRequested;
            status = timeout ? AgentRunStatus.Failed : AgentRunStatus.Cancelled;
            failure = new AgentFailure
            {
                Category = timeout ? AgentFailureCategory.BudgetExceeded : AgentFailureCategory.Cancelled,
                Summary = timeout ? "The Codex operation exceeded its startup or run time limit." : "The Codex run was cancelled.",
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
                    ? "Codex could not start. Select an installed Codex executable and use Sign in to ChatGPT in the editor connection settings."
                    : exception is JsonException or InvalidDataException ? "Codex returned an invalid protocol message." : exception.Message,
            };
        }
        catch (Exception)
        {
            failure = new AgentFailure { Category = AgentFailureCategory.Internal, Summary = "The Codex runtime or host tool could not complete the request." };
        }
        finally
        {
            if (profile is not null)
            {
                try
                {
                    try
                    {
                        if (!state.Completed && state.ThreadId.Length > 0 && state.TurnId.Length > 0)
                            await profile.Process.InterruptAsync(state.ThreadId, state.TurnId);
                    }
                    finally
                    {
                        await profile.DisposeAsync();
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or TimeoutException or InvalidOperationException or Win32Exception)
                {
                    status = AgentRunStatus.Failed;
                    failure = new AgentFailure { Category = AgentFailureCategory.Transport, Summary = "Could not confirm shutdown of the owned Codex process." };
                }
            }
        }

        return new AgentRunResult
        {
            RunId = runId, Status = status, RequestedModel = request.RequestedModel,
            ActualModel = state.ActualModel, FinalText = state.FinalText, Usage = state.Usage,
            ToolCallCount = toolSession.CallCount, ToolEvidence = toolSession.Evidence.ToArray(),
            TurnCountReported = false, RetryCountReported = false,
            RuntimeInfo = state.RuntimeInfo, Failure = failure, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
        };
    }

    private static async Task<CodexConnectionInfo> ReadConnectionAsync(CodexRpcProcess process, string version, CancellationToken token)
    {
        JsonObject accountResponse = await process.CallAsync("account/read", new JsonObject { ["refreshToken"] = false }, token);
        JsonObject? account = accountResponse["account"] as JsonObject;
        string mode = account?["type"]?.GetValue<string>() ?? "signedOut";
        if (mode != "chatgpt")
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Codex subscription access requires a ChatGPT login in the XRENGINE profile. Use Sign in to ChatGPT in the editor connection settings. API-key accounts are not used.");
        string? email = account?["email"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(email))
            throw new AgentToolProviderException(AgentFailureCategory.Authentication,
                "Codex did not provide account identity evidence. Check the connection before sending conversation context.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
            {
                "chatgpt", email, account?["planType"]?.GetValue<string>(), account?["workspaceName"]?.GetValue<string>(),
            }))));
        var models = new List<CodexModelInfo>();
        string? cursor = null;
        for (int page = 0; page < 16; page++)
        {
            JsonObject catalog = await process.CallAsync("model/list", new JsonObject
            {
                ["limit"] = 100, ["includeHidden"] = true, ["cursor"] = cursor,
            }, token);
            if (catalog["data"] is not JsonArray data)
                throw new InvalidDataException("Codex did not return its model catalog.");
            foreach (JsonNode? entry in data)
            {
                string id = entry?["model"]?.GetValue<string>() ?? string.Empty;
                if (id.Length == 0)
                    continue;
                var efforts = new List<string>();
                if (entry?["supportedReasoningEfforts"] is JsonArray supported)
                {
                    foreach (JsonNode? effort in supported)
                    {
                        if (effort?["reasoningEffort"]?.GetValue<string>() is { } value
                            && AgentRequestValidator.IsSupportedReasoningEffort(value)
                            && !efforts.Contains(value, StringComparer.Ordinal))
                            efforts.Add(value);
                    }
                }
                string? defaultEffort = entry?["defaultReasoningEffort"]?.GetValue<string>();
                models.Add(new CodexModelInfo
                {
                    Id = id,
                    SupportedReasoningEfforts = efforts,
                    DefaultReasoningEffort = defaultEffort is not null && efforts.Contains(defaultEffort, StringComparer.Ordinal)
                        ? defaultEffort : null,
                });
            }
            cursor = catalog["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor))
                return new CodexConnectionInfo { Version = version, AuthenticationMode = mode, AccountFingerprint = fingerprint, Models = models };
        }
        throw new InvalidDataException("Codex model discovery exceeded the page limit.");
    }

    internal static void ValidateImage(string image)
    {
        if (!image.StartsWith("data:image/", StringComparison.Ordinal) || image.Length > 4 * 1024 * 1024)
            throw new AgentToolProviderException(AgentFailureCategory.Validation, "Codex accepts only bounded inline image data for this operation.");
    }
}
