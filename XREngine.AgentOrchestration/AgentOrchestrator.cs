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
        elapsedBudget.CancelAfter(TimeSpan.FromSeconds(request.Budget.MaxElapsedSeconds));
        CancellationToken runToken = elapsedBudget.Token;

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
            var finalText = new StringBuilder();
            var outputItems = new List<AgentOutputItem>();
            var evidence = new List<AgentToolEvidence>();
            var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
            AgentTokenUsage usage = new();
            string actualModel = string.Empty;
            int toolCallCount = 0;
            int turnCount = 0;
            int lastMutationEvidenceIndex = -1;
            int lastVerificationEvidenceIndex = -1;

            for (int turn = 0; turn < request.Budget.MaxTurns; turn++)
            {
                runToken.ThrowIfCancellationRequested();
                long remainingOutputTokens =
                    request.Budget.MaxOutputTokens - usage.OutputTokens;
                if (remainingOutputTokens <= 0)
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
                        finalText.ToString());
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
                    runToken);

                actualModel = turnResult.ActualModel;
                usage += turnResult.Usage;
                continuation = turnResult.ContinuationJson;
                toolOutputs = [];

                if (usage.OutputTokens > request.Budget.MaxOutputTokens)
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
                        finalText.ToString());
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
                        toolCallCount);
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
                        finalText.ToString());
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
                        finalText.ToString());
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
                            finalText.ToString());
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
                            finalText.ToString());
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
                    finalText.ToString());
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
                    finalText.ToString());
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
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            bool callerCancelled = cancellationToken.IsCancellationRequested;
            return FailureResult(
                runId,
                request,
                stopwatch,
                callerCancelled ? AgentFailureCategory.Cancelled : AgentFailureCategory.BudgetExceeded,
                callerCancelled ? "The run was cancelled." : "The run exceeded its elapsed-time budget.",
                status: callerCancelled ? AgentRunStatus.Cancelled : AgentRunStatus.Failed);
        }
        catch (AgentModelException exception)
        {
            return FailureResult(
                runId,
                request,
                stopwatch,
                exception.Category,
                exception.Message,
                providerStatus: exception.ProviderStatus,
                retryable: exception.Retryable,
                diagnosticDetail: exception.DiagnosticDetail);
        }
        catch (AgentToolProviderException exception)
        {
            return FailureResult(
                runId,
                request,
                stopwatch,
                exception.Category,
                exception.Message,
                diagnosticDetail: exception.DiagnosticDetail);
        }
        catch (Exception exception)
        {
            return FailureResult(
                runId,
                request,
                stopwatch,
                AgentFailureCategory.Internal,
                "The agent run failed unexpectedly.",
                diagnosticDetail: exception.Message);
        }
    }

    private async Task<AgentModelTurnResult> ExecuteModelWithRetryAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _modelClient.CreateResponseAsync(request, observer, cancellationToken);
            }
            catch (AgentModelException exception) when (exception.Retryable && attempt < maxRetries)
            {
                TimeSpan delay = exception.RetryAfter
                    ?? TimeSpan.FromMilliseconds(Math.Min(8_000, 400 * Math.Pow(2, attempt))
                        + Random.Shared.Next(50, 251));
                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Retry,
                        Message = $"Retrying provider request after {delay.TotalMilliseconds:0} ms.",
                    },
                    cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }
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
        => string.Equals(requestedModel, actualModel, StringComparison.Ordinal)
            || actualModel.StartsWith(requestedModel + "-", StringComparison.Ordinal);

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
        string diagnosticDetail = "")
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
