using System.Diagnostics;
using System.Text;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Executes a bounded provider/tool loop independently of any UI or host process.
/// </summary>
public sealed class AgentOrchestrator(IAgentModelClient modelClient)
{
    private readonly IAgentModelClient _modelClient =
        modelClient ?? throw new ArgumentNullException(nameof(modelClient));

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
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> validationErrors = AgentRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
            return FailureResult(
                runId,
                request,
                stopwatch,
                AgentFailureCategory.Validation,
                string.Join("; ", validationErrors));

        using var elapsedBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Budget.MaxElapsedSeconds > 0)
            elapsedBudget.CancelAfter(TimeSpan.FromSeconds(request.Budget.MaxElapsedSeconds));
        CancellationToken runToken = elapsedBudget.Token;
        var finalText = new StringBuilder();
        var outputItems = new List<AgentOutputItem>();
        var evidence = new List<AgentToolEvidence>();
        var providerAttempts = new List<AgentProviderAttemptDiagnostic>();
        AgentTokenUsage usage = new();
        string actualModel = string.Empty;
        int toolCallCount = 0;
        int turnCount = 0;

        try
        {
            await observer.OnEventAsync(
                new AgentRunEvent { Kind = AgentRunEventKind.Status, Message = "running" },
                runToken);

            IReadOnlyList<AgentToolDefinition> tools = await toolProvider.ListToolsAsync(runToken);
            Dictionary<string, AgentToolDefinition> toolsByName = tools.ToDictionary(
                static tool => tool.Name,
                StringComparer.Ordinal);

            string prompt = request.UseCompactHandoffPrompt
                ? AgentPromptBuilder.Build(request)
                : request.Objective;
            string? continuation = null;
            IReadOnlyList<AgentModelToolOutput> toolOutputs = [];
            var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
            int lastMutationEvidenceIndex = -1;
            int lastVerificationEvidenceIndex = -1;

            for (int turn = 0; turn < request.Budget.MaxTurns; turn++)
            {
                runToken.ThrowIfCancellationRequested();
                bool hasOutputTokenLimit = request.Budget.MaxOutputTokens > 0;
                long remainingOutputTokens = hasOutputTokenLimit
                    ? request.Budget.MaxOutputTokens - usage.OutputTokens
                    : 0;
                if (hasOutputTokenLimit && remainingOutputTokens <= 0)
                {
                    return FailureResult(
                        runId,
                        request,
                        stopwatch,
                        AgentFailureCategory.BudgetExceeded,
                        "The run exhausted its output-token budget.",
                        actualModel,
                        usage,
                        evidence,
                        turnCount,
                        toolCallCount,
                        finalText.ToString(),
                        providerAttempts: providerAttempts,
                        retryCount: CountRetries(providerAttempts));
                }

                turnCount++;
                bool forceText = turn == request.Budget.MaxTurns - 1;
                AgentModelTurnResult turnResult = await ExecuteModelWithRetryAsync(
                    new AgentModelTurnRequest
                    {
                        Run = request,
                        Prompt = prompt,
                        Tools = tools,
                        ContinuationJson = continuation,
                        ToolOutputs = toolOutputs,
                        TurnIndex = turn,
                        ForceTextResponse = forceText,
                        MaxOutputTokens = (int)remainingOutputTokens,
                    },
                    observer,
                    request.Budget.MaxRetries,
                    providerAttempts,
                    runToken);

                actualModel = turnResult.ActualModel;
                usage += turnResult.Usage;
                continuation = turnResult.ContinuationJson;
                toolOutputs = [];

                if (hasOutputTokenLimit && usage.OutputTokens > request.Budget.MaxOutputTokens)
                {
                    return FailureResult(
                        runId,
                        request,
                        stopwatch,
                        AgentFailureCategory.BudgetExceeded,
                        "The provider exceeded the run's output-token budget.",
                        actualModel,
                        usage,
                        evidence,
                        turnCount,
                        toolCallCount,
                        finalText.ToString(),
                        providerAttempts: providerAttempts,
                        retryCount: CountRetries(providerAttempts));
                }

                if (!IsRequestedModel(request.RequestedModel, actualModel))
                {
                    return FailureResult(
                        runId,
                        request,
                        stopwatch,
                        AgentFailureCategory.ModelSubstitution,
                        $"Provider returned model '{actualModel}' for explicitly requested model '{request.RequestedModel}'.",
                        actualModel,
                        usage,
                        evidence,
                        turnCount,
                        toolCallCount,
                        providerAttempts: providerAttempts,
                        retryCount: CountRetries(providerAttempts));
                }

                if (!string.IsNullOrEmpty(turnResult.OutputText))
                    finalText.Append(turnResult.OutputText);
                outputItems.AddRange(turnResult.OutputItems);

                await observer.OnEventAsync(
                    new AgentRunEvent { Kind = AgentRunEventKind.Usage, Usage = turnResult.Usage },
                    runToken);

                if (turnResult.ToolCalls.Count == 0)
                    break;

                if (forceText)
                {
                    return FailureResult(
                        runId,
                        request,
                        stopwatch,
                        AgentFailureCategory.BudgetExceeded,
                        "The model requested tools on the final allowed turn.",
                        actualModel,
                        usage,
                        evidence,
                        turnCount,
                        toolCallCount,
                        finalText.ToString(),
                        providerAttempts: providerAttempts,
                        retryCount: CountRetries(providerAttempts));
                }

                if (toolCallCount + turnResult.ToolCalls.Count > request.Budget.MaxToolCalls)
                {
                    return FailureResult(
                        runId,
                        request,
                        stopwatch,
                        AgentFailureCategory.BudgetExceeded,
                        "The run exceeded its tool-call budget.",
                        actualModel,
                        usage,
                        evidence,
                        turnCount,
                        toolCallCount,
                        finalText.ToString(),
                        providerAttempts: providerAttempts,
                        retryCount: CountRetries(providerAttempts));
                }

                List<AgentModelToolOutput> nextOutputs = [];
                foreach (AgentToolCall call in turnResult.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.CallId) || !seenCallIds.Add(call.CallId))
                    {
                        return FailureResult(
                            runId,
                            request,
                            stopwatch,
                            AgentFailureCategory.ProviderError,
                            $"The provider emitted an empty or duplicate function call ID '{call.CallId}'.",
                            actualModel,
                            usage,
                            evidence,
                            turnCount,
                            toolCallCount,
                            finalText.ToString(),
                            providerAttempts: providerAttempts,
                            retryCount: CountRetries(providerAttempts));
                    }

                    if (!toolsByName.TryGetValue(call.Name, out AgentToolDefinition? definition))
                    {
                        return FailureResult(
                            runId,
                            request,
                            stopwatch,
                            AgentFailureCategory.ToolDenied,
                            $"The model requested unavailable tool '{call.Name}'.",
                            actualModel,
                            usage,
                            evidence,
                            turnCount,
                            toolCallCount,
                            finalText.ToString(),
                            providerAttempts: providerAttempts,
                            retryCount: CountRetries(providerAttempts));
                    }

                    toolCallCount++;
                    await observer.OnEventAsync(
                        new AgentRunEvent
                        {
                            Kind = AgentRunEventKind.ToolStarted,
                            ToolName = call.Name,
                            CallId = call.CallId,
                            Message = call.ArgumentsJson,
                        },
                        runToken);

                    AgentToolResult rawResult = await toolProvider.ExecuteAsync(call, runToken);
                    AgentToolResult boundedResult = BoundToolResult(rawResult, request.Budget.MaxToolResultBytes);
                    bool mutation = !definition.IsReadOnly;
                    bool verification = definition.IsReadOnly && IsVerificationTool(definition.Name);
                    var toolEvidence = new AgentToolEvidence
                    {
                        CallId = call.CallId,
                        ToolName = call.Name,
                        ArgumentsSummary = Summarize(call.ArgumentsJson, 1_024),
                        ResultSummary = Summarize(boundedResult.Content, 4_096),
                        IsError = boundedResult.IsError,
                        IsMutation = mutation,
                        IsVisualEvidence = !string.IsNullOrWhiteSpace(boundedResult.ImagePath)
                            || !string.IsNullOrWhiteSpace(boundedResult.ImageDataUri),
                        EvidencePath = boundedResult.ImagePath,
                    };
                    evidence.Add(toolEvidence);

                    int evidenceIndex = evidence.Count - 1;
                    if (mutation)
                        lastMutationEvidenceIndex = evidenceIndex;
                    else if (verification && !boundedResult.IsError)
                        lastVerificationEvidenceIndex = evidenceIndex;

                    await observer.OnEventAsync(
                        new AgentRunEvent
                        {
                            Kind = AgentRunEventKind.ToolCompleted,
                            ToolName = call.Name,
                            CallId = call.CallId,
                            ToolResult = boundedResult,
                            ToolEvidence = toolEvidence,
                        },
                        runToken);

                    nextOutputs.Add(new AgentModelToolOutput
                    {
                        CallId = call.CallId,
                        Content = boundedResult.Content,
                        ImageDataUri = boundedResult.ImageDataUri,
                    });
                }

                toolOutputs = nextOutputs;
            }

            if (lastMutationEvidenceIndex >= 0
                && request.ToolPolicy.RequireMutationEvidence
                && lastVerificationEvidenceIndex <= lastMutationEvidenceIndex)
            {
                return FailureResult(
                    runId,
                    request,
                    stopwatch,
                    AgentFailureCategory.MutationEvidenceMissing,
                    "A mutation completed without a later read-back or capture tool call.",
                    actualModel,
                    usage,
                    evidence,
                    turnCount,
                    toolCallCount,
                    finalText.ToString(),
                    providerAttempts: providerAttempts,
                    retryCount: CountRetries(providerAttempts));
            }

            if (turnCount >= request.Budget.MaxTurns && toolOutputs.Count > 0)
            {
                return FailureResult(
                    runId,
                    request,
                    stopwatch,
                    AgentFailureCategory.BudgetExceeded,
                    "The run reached its maximum turn count.",
                    actualModel,
                    usage,
                    evidence,
                    turnCount,
                    toolCallCount,
                    finalText.ToString(),
                    providerAttempts: providerAttempts,
                    retryCount: CountRetries(providerAttempts));
            }

            return new AgentRunResult
            {
                RunId = runId,
                Status = AgentRunStatus.Completed,
                RequestedModel = request.RequestedModel,
                ActualModel = actualModel,
                FinalText = finalText.ToString(),
                OutputItems = outputItems,
                ToolEvidence = evidence,
                Usage = usage,
                ToolCallCount = toolCallCount,
                TurnCount = turnCount,
                RetryCount = CountRetries(providerAttempts),
                ProviderAttempts = providerAttempts.ToArray(),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException exception)
        {
            if (exception is AgentModelOperationCanceledException providerCancellation)
                AddOrReplaceProviderAttempt(providerAttempts, providerCancellation.ProviderAttempt);
            bool callerCancelled = cancellationToken.IsCancellationRequested;
            bool elapsedTimeExceeded = !callerCancelled
                && request.Budget.MaxElapsedSeconds > 0
                && elapsedBudget.IsCancellationRequested;
            actualModel = LatestActualModel(actualModel, providerAttempts);
            return FailureResult(
                runId,
                request,
                stopwatch,
                callerCancelled
                    ? AgentFailureCategory.Cancelled
                    : elapsedTimeExceeded
                        ? AgentFailureCategory.BudgetExceeded
                        : AgentFailureCategory.ProviderError,
                callerCancelled
                    ? "The run was cancelled."
                    : elapsedTimeExceeded
                        ? "The run exceeded its elapsed-time budget."
                        : "The provider operation was cancelled unexpectedly.",
                actualModel,
                usage,
                evidence,
                turnCount,
                toolCallCount,
                finalText.ToString(),
                status: callerCancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                providerAttempts: providerAttempts,
                retryCount: CountRetries(providerAttempts));
        }
        catch (AgentModelException exception)
        {
            actualModel = LatestActualModel(actualModel, providerAttempts);
            return FailureResult(
                runId,
                request,
                stopwatch,
                exception.Category,
                exception.Message,
                actualModel,
                usage,
                evidence,
                turnCount,
                toolCallCount,
                finalText.ToString(),
                providerStatus: exception.ProviderStatus,
                retryable: exception.Retryable,
                diagnosticDetail: exception.DiagnosticDetail,
                providerAttempts: providerAttempts,
                retryCount: CountRetries(providerAttempts));
        }
        catch (AgentToolProviderException exception)
        {
            return FailureResult(
                runId,
                request,
                stopwatch,
                exception.Category,
                exception.Message,
                actualModel,
                usage,
                evidence,
                turnCount,
                toolCallCount,
                finalText.ToString(),
                diagnosticDetail: exception.DiagnosticDetail,
                providerAttempts: providerAttempts,
                retryCount: CountRetries(providerAttempts));
        }
        catch (Exception exception)
        {
            return FailureResult(
                runId,
                request,
                stopwatch,
                AgentFailureCategory.Internal,
                "The agent run failed unexpectedly.",
                actualModel,
                usage,
                evidence,
                turnCount,
                toolCallCount,
                finalText.ToString(),
                diagnosticDetail: exception.Message,
                providerAttempts: providerAttempts,
                retryCount: CountRetries(providerAttempts));
        }
    }

    private async Task<AgentModelTurnResult> ExecuteModelWithRetryAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        int maxRetries,
        List<AgentProviderAttemptDiagnostic> providerAttempts,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                AgentModelTurnResult result = await _modelClient.CreateResponseAsync(
                    request with { AttemptNumber = attempt + 1 },
                    observer,
                    cancellationToken);
                AgentProviderAttemptDiagnostic diagnostic = NormalizeAttemptDiagnostic(
                    result.ProviderAttempt,
                    request,
                    attempt + 1,
                    retried: false,
                    fallbackOutcome: "completed");
                providerAttempts.Add(diagnostic);
                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Diagnostic,
                        Message = $"Provider turn {request.TurnIndex + 1} attempt {attempt + 1} completed.",
                        ProviderAttempt = diagnostic,
                    },
                    cancellationToken);
                return result with { ProviderAttempt = diagnostic };
            }
            catch (AgentModelException exception)
            {
                bool willRetry = exception.Retryable && attempt < maxRetries;
                AgentProviderAttemptDiagnostic diagnostic = NormalizeAttemptDiagnostic(
                    exception.ProviderAttempt,
                    request,
                    attempt + 1,
                    willRetry,
                    fallbackOutcome: "provider_error",
                    exception);
                providerAttempts.Add(diagnostic);
                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Diagnostic,
                        Message = $"Provider turn {request.TurnIndex + 1} attempt {attempt + 1} failed with {exception.Category}.",
                        ProviderAttempt = diagnostic,
                    },
                    cancellationToken);
                if (!willRetry)
                    throw;

                TimeSpan delay = exception.RetryAfter
                    ?? TimeSpan.FromMilliseconds(Math.Min(8_000, 400 * Math.Pow(2, attempt))
                        + Random.Shared.Next(50, 251));
                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Retry,
                        Message = $"Retrying provider turn {request.TurnIndex + 1} after attempt {attempt + 1}/{maxRetries + 1} and {delay.TotalMilliseconds:0} ms.",
                        ProviderAttempt = diagnostic,
                    },
                    cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static AgentProviderAttemptDiagnostic NormalizeAttemptDiagnostic(
        AgentProviderAttemptDiagnostic? diagnostic,
        AgentModelTurnRequest request,
        int attemptNumber,
        bool retried,
        string fallbackOutcome,
        AgentModelException? exception = null)
    {
        diagnostic ??= new AgentProviderAttemptDiagnostic();
        return diagnostic with
        {
            TurnNumber = request.TurnIndex + 1,
            AttemptNumber = attemptNumber,
            UsedBackgroundMode = request.Run.UseBackgroundMode,
            Outcome = string.IsNullOrWhiteSpace(diagnostic.Outcome)
                ? fallbackOutcome
                : diagnostic.Outcome,
            FailureCategory = diagnostic.FailureCategory ?? exception?.Category,
            ProviderStatus = diagnostic.ProviderStatus ?? exception?.ProviderStatus,
            Retryable = diagnostic.Retryable || exception?.Retryable == true,
            Retried = retried,
        };
    }

    private static int CountRetries(IReadOnlyList<AgentProviderAttemptDiagnostic> attempts)
        => attempts.Count(static attempt => attempt.Retried);

    private static void AddOrReplaceProviderAttempt(
        List<AgentProviderAttemptDiagnostic> attempts,
        AgentProviderAttemptDiagnostic diagnostic)
    {
        int existingIndex = attempts.FindIndex(candidate =>
            candidate.TurnNumber == diagnostic.TurnNumber
            && candidate.AttemptNumber == diagnostic.AttemptNumber);
        if (existingIndex >= 0)
            attempts[existingIndex] = diagnostic;
        else
            attempts.Add(diagnostic);
    }

    private static string LatestActualModel(
        string currentActualModel,
        IReadOnlyList<AgentProviderAttemptDiagnostic> attempts)
    {
        if (!string.IsNullOrWhiteSpace(currentActualModel))
            return currentActualModel;

        for (int index = attempts.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(attempts[index].ActualModel))
                return attempts[index].ActualModel;
        }

        return string.Empty;
    }

    private static AgentToolResult BoundToolResult(AgentToolResult result, int maximumBytes)
    {
        if (!string.IsNullOrWhiteSpace(result.ImageDataUri)
            && Encoding.UTF8.GetByteCount(result.ImageDataUri) > maximumBytes)
        {
            bool hasPath = !string.IsNullOrWhiteSpace(result.ImagePath);
            result = result with
            {
                Content = result.Content
                    + "\n[inline image omitted by broker budget"
                    + (hasPath ? "; use the evidence path]" : "]"),
                ImageDataUri = null,
                IsError = result.IsError || !hasPath,
                IsTruncated = true,
            };
        }

        int byteCount = Encoding.UTF8.GetByteCount(result.Content);
        if (byteCount <= maximumBytes)
            return result;

        const string marker = "\n[tool result truncated by broker budget]";
        int markerBytes = Encoding.UTF8.GetByteCount(marker);
        int targetBytes = Math.Max(0, maximumBytes - markerBytes);
        int low = 0;
        int high = result.Content.Length;
        while (low < high)
        {
            int midpoint = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(result.Content.AsSpan(0, midpoint)) <= targetBytes)
                low = midpoint;
            else
                high = midpoint - 1;
        }

        return result with
        {
            Content = result.Content[..low] + marker,
            IsTruncated = true,
        };
    }

    private static bool IsRequestedModel(string requestedModel, string actualModel)
        => string.Equals(requestedModel, actualModel, StringComparison.Ordinal);

    private static bool IsVerificationTool(string toolName)
        => toolName.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("find_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("capture_", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("inspect", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("validate", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("query", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("screenshot", StringComparison.OrdinalIgnoreCase);

    private static string Summarize(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";

    private static AgentRunResult FailureResult(
        string runId,
        AgentRunRequest request,
        Stopwatch stopwatch,
        AgentFailureCategory category,
        string summary,
        string actualModel = "",
        AgentTokenUsage? usage = null,
        IReadOnlyList<AgentToolEvidence>? evidence = null,
        int turnCount = 0,
        int toolCallCount = 0,
        string finalText = "",
        AgentRunStatus status = AgentRunStatus.Failed,
        int? providerStatus = null,
        bool retryable = false,
        string diagnosticDetail = "",
        IReadOnlyList<AgentProviderAttemptDiagnostic>? providerAttempts = null,
        int retryCount = 0)
        => new()
        {
            RunId = runId,
            Status = status,
            RequestedModel = request.RequestedModel,
            ActualModel = actualModel,
            FinalText = finalText,
            ToolEvidence = evidence ?? [],
            Usage = usage ?? new AgentTokenUsage(),
            ToolCallCount = toolCallCount,
            TurnCount = turnCount,
            RetryCount = retryCount,
            ProviderAttempts = providerAttempts ?? [],
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Failure = new AgentFailure
            {
                Category = category,
                Summary = summary,
                Retryable = retryable,
                ProviderStatus = providerStatus,
                DiagnosticDetail = diagnosticDetail,
            },
        };
}
