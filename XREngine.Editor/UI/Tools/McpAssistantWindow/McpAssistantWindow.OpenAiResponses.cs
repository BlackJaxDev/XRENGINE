using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAgentBroker.Orchestration;
using XREngine.Data.Core;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private async Task StreamOpenAiResponsesAsync(
        string prompt,
        ChatMessage target,
        CancellationToken cancellationToken,
        McpUsageEntry usage,
        bool useSubscription = false,
        bool? currentPromptLikelyNeedsTools = null)
    {
        ProviderType selectedProvider = (ProviderType)ProviderIndex;
        bool requestLikelyNeedsTools = !IsReadOnlyRequest && (currentPromptLikelyNeedsTools ?? IsLikelySceneMutationPrompt(prompt));
        usage.RequireToolUse = requestLikelyNeedsTools;

        List<AgentToolDefinition> tools = ConvertAssistantFunctionTools(
            BuildLocalAssistantFunctionTools(),
            usesMcpSchema: false);
        if (AttachMcpServer)
        {
            JsonArray? mcpTools = await FetchMcpToolListAsync(cancellationToken);
            if (mcpTools is null || mcpTools.Count == 0)
            {
                usage.McpDiscoveryFailed = true;
                usage.Result = "MCP Fetch Failed";
                target.Status = ChatMessageStatus.Failed;
                target.StatusDetail = "Editor tools are enabled, but the local MCP server returned no tools.";
                return;
            }

            tools.AddRange(ConvertAssistantFunctionTools(mcpTools, usesMcpSchema: true));
            usage.McpPayloadIncluded = true;
            usage.ToolChoice = requestLikelyNeedsTools ? "required" : "auto";
        }

        var provider = new DelegateAgentToolProvider(
            _ => Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools),
            async (call, token) =>
            {
                ToolCallResult result = await ExecuteMcpToolCallAsync(
                    call.Name,
                    call.ArgumentsJson,
                    token);
                return new AgentToolResult
                {
                    Content = result.Text,
                    IsError = result.IsError,
                    ImageDataUri = result.HasImage
                        ? $"data:image/png;base64,{result.ImageBase64}"
                        : null,
                    ImagePath = result.ImagePath,
                };
            });

        if (useSubscription)
            throw new InvalidOperationException(SubscriptionEditorConnection.UnavailableMessage);

        string model = string.IsNullOrWhiteSpace(OpenAiModel) ? "gpt-4o" : OpenAiModel;
        string? screenshotBase64 = Interlocked.Exchange(ref _pendingScreenshotBase64, null);
        var request = new AgentRunRequest
        {
            Objective = prompt,
            RequestedModel = model,
            ReasoningEffort = string.IsNullOrEmpty(_activeReasoningEffort) ? "medium" : _activeReasoningEffort,
            UseCompactHandoffPrompt = false,
            RequireToolUse = requestLikelyNeedsTools && AttachMcpServer,
            SystemInstructions = BuildSystemInstructions(
                selectedProvider,
                requestLikelyNeedsTools,
                attachMcp: AttachMcpServer,
                keepCameraOnWorkingArea: AutoCameraView),
            InitialImageDataUri = screenshotBase64 is null
                ? null
                : $"data:image/png;base64,{screenshotBase64}",
            HostedTools = !IsReadOnlyRequest && ModelSupportsImageGeneration(model)
                ? [AgentHostedTool.WebSearch, AgentHostedTool.ImageGeneration]
                : [AgentHostedTool.WebSearch],
            ToolPolicy = new AgentToolPolicy
            {
                AllowMutation = !IsReadOnlyRequest,
                AllowedTools = tools.Select(static tool => tool.Name).ToArray(),
                RequireMutationEvidence = false,
            },
            Budget = new AgentRunBudget
            {
                MaxTurns = 10,
                MaxToolCalls = 64,
                // Viewport PNG data is larger than the broker's text-only default.
                MaxToolResultBytes = 4 * 1024 * 1024,
                MaxOutputTokens = 16_384,
                MaxElapsedSeconds = 600,
                MaxRetries = 2,
            },
        };

        var modelText = new StringBuilder();
        int segmentStart = 0;
        var toolLog = new StringBuilder();
        var entriesByCallId = new Dictionary<string, ToolCallEntry>(StringComparer.Ordinal);
        var observer = new DelegateAgentRunObserver((runEvent, _) =>
        {
            switch (runEvent.Kind)
            {
                case AgentRunEventKind.TextDelta:
                    modelText.Append(runEvent.Message);
                    target.Content = BuildAssistantContent(modelText, toolLog);
                    SyncTextSegment(target, modelText, segmentStart);
                    break;
                case AgentRunEventKind.ToolStarted:
                {
                    usage.ToolEventCount++;
                    var entry = new ToolCallEntry
                    {
                        ToolName = runEvent.ToolName ?? string.Empty,
                        ArgsSummary = SummarizeToolArguments(
                            runEvent.ToolName ?? string.Empty,
                            runEvent.Message),
                    };
                    entry.SetInvocation(runEvent.Message ?? "{}");
                    entriesByCallId[runEvent.CallId ?? Guid.NewGuid().ToString("N")] = entry;
                    AddToolCallSegmented(target, entry);
                    segmentStart = modelText.Length;
                    target.Content = BuildAssistantContent(
                        modelText,
                        toolLog,
                        $"Executing {runEvent.ToolName}...");
                    break;
                }
                case AgentRunEventKind.ToolCompleted:
                {
                    usage.McpEventCount++;
                    AgentToolResult result = runEvent.ToolResult ?? new AgentToolResult();
                    if (runEvent.CallId is not null
                        && entriesByCallId.TryGetValue(runEvent.CallId, out ToolCallEntry? entry))
                    {
                        entry.ResultSummary = SummarizeToolResult(result.Content);
                        entry.ContextResultSummary = SummarizeToolResultForContext(result.Content);
                        entry.IsError = result.IsError;
                        entry.ResultFilePath = result.ImagePath;
                        entry.IsComplete = true;
                        entry.UpdateOutcome(result.Content);
                    }

                    AppendToolCallLog(
                        toolLog,
                        runEvent.ToolName ?? string.Empty,
                        string.Empty,
                        result.Content);
                    break;
                }
            }

            return ValueTask.CompletedTask;
        });

        var executor = new AgentOrchestrator(new OpenAiResponsesModelClient(SharedHttp, () => OpenAiApiKey));
        AgentRunResult runResult = await executor.RunAsync(
            Guid.NewGuid().ToString("N"),
            request,
            provider,
            observer,
            cancellationToken);

        if (modelText.Length == 0 && !string.IsNullOrEmpty(runResult.FinalText))
            modelText.Append(runResult.FinalText);
        foreach (AgentOutputItem item in runResult.OutputItems)
        {
            if (item.Kind != AgentOutputItemKind.Image || string.IsNullOrWhiteSpace(item.DataUri))
                continue;
            string? path = PersistAssistantImage(item.DataUri);
            if (path is not null)
                modelText.Append($"\n\n[Image generated: {path}]");
        }

        target.Content = BuildAssistantContent(modelText, toolLog);
        SyncTextSegment(target, modelText, segmentStart);
        target.Status = runResult.Status switch
        {
            AgentRunStatus.Completed => ChatMessageStatus.Completed,
            AgentRunStatus.Cancelled => ChatMessageStatus.Stopped,
            AgentRunStatus.Failed => ChatMessageStatus.Failed,
            _ => ChatMessageStatus.Incomplete,
        };
        target.StatusDetail = runResult.Failure?.Summary ?? (target.Status switch
        {
            ChatMessageStatus.Stopped => "The request stopped. Any completed changes may remain in the editor.",
            ChatMessageStatus.Failed => "The assistant request failed.",
            ChatMessageStatus.Incomplete => "The assistant did not finish this request.",
            _ => null,
        });
        usage.Result = runResult.Status.ToString();
        usage.Note = $"requested_model={runResult.RequestedModel}; actual_model={runResult.ActualModel}; turns={runResult.TurnCount}; tool_calls={runResult.ToolCallCount}; tokens={runResult.Usage.TotalTokens}";
    }

    private List<AgentToolDefinition> ConvertAssistantFunctionTools(
        JsonArray source,
        bool usesMcpSchema)
    {
        List<AgentToolDefinition> tools = [];
        foreach (JsonNode? node in source)
        {
            if (node is not JsonObject tool)
                continue;
            string name = tool["name"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            JsonNode? schema = tool[usesMcpSchema ? "inputSchema" : "parameters"];
            bool readOnly = IsReadOnlyAssistantTool(name);
            bool destructive = ReadBooleanAnnotation(tool, "destructiveHint");
            tools.Add(new AgentToolDefinition
            {
                Name = name,
                Description = tool["description"]?.GetValue<string>() ?? string.Empty,
                InputSchemaJson = schema?.ToJsonString()
                    ?? """{"type":"object","properties":{}}""",
                IsReadOnly = readOnly,
                IsDestructive = destructive,
            });
        }

        return tools;
    }

    private static bool ReadBooleanAnnotation(JsonObject tool, string propertyName)
        => tool["annotations"] is JsonObject annotations
            && annotations[propertyName] is JsonValue value
            && value.TryGetValue(out bool result)
            && result;

    private static string? PersistAssistantImage(string dataUri)
    {
        const string marker = ";base64,";
        int markerIndex = dataUri.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        try
        {
            string runRoot = XREnvironment.GetValue(XREngineEnvironmentVariables.AgentValidationRunRoot)
                ?? Path.Combine(
                    Environment.CurrentDirectory,
                    "Build",
                    "_AgentValidation",
                    "editor-assistant-output");
            string outputDirectory = Path.Combine(runRoot, "mcp-captures", "GeneratedImages");
            Directory.CreateDirectory(outputDirectory);
            string path = Path.Combine(
                outputDirectory,
                $"Generated_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(dataUri[(markerIndex + marker.Length)..]));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProviderErrorContent(string content, out string? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;
        if (TryExtractJsonErrorSummary(content, out string? jsonSummary))
        {
            summary = jsonSummary;
            return true;
        }
        if (content.Contains("--- Retry Failed", StringComparison.OrdinalIgnoreCase)
            || content.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            || content.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || content.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || content.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
        {
            summary = "Provider request failed.";
            return true;
        }
        return false;
    }

    private static string? TryExtractEventErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return root.TryGetProperty("message", out JsonElement topLevelMessage)
            && topLevelMessage.ValueKind == JsonValueKind.String
                ? topLevelMessage.GetString()
                : null;
    }

    private static string? ExtractOpenAiErrorSummary(string content)
        => TryExtractJsonErrorSummary(content, out string? summary) ? summary : null;

    private static bool TryExtractJsonErrorSummary(string content, out string? summary)
    {
        summary = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? message = error.TryGetProperty("message", out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
            string? code = error.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString()
                    : null;
            summary = string.Equals(code, "insufficient_quota", StringComparison.OrdinalIgnoreCase)
                ? "OpenAI quota/billing issue (insufficient_quota)."
                : !string.IsNullOrWhiteSpace(message)
                    ? $"OpenAI error: {message}"
                    : "OpenAI request failed.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractOpenAiResponseText(string body)
        => OpenAiResponsesStreamParser.ExtractResponseText(body);

    private static string ExtractAnthropicResponseText(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out JsonElement type)
                        && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                        && item.TryGetProperty("text", out JsonElement text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }
                if (builder.Length > 0)
                    return builder.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return body;
    }

    private static string ExtractOpenAiCompatibleResponseText(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || !message.TryGetProperty("content", out JsonElement content))
            {
                return body;
            }

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? body;
            if (content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out JsonElement text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }
                if (builder.Length > 0)
                    return builder.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return body;
    }
}
