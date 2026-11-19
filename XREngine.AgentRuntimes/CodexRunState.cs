using System.Text;
using System.Text.Json.Nodes;
using XREngine.AgentOrchestration;

namespace XREngine.AgentRuntimes;

/// <summary>Converts native session events to host progress and checked tool calls.</summary>
internal sealed class CodexRunState(AgentRunRequest request, AgentRuntimeToolSession tools, IAgentRunObserver observer)
{
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private readonly StringBuilder _text = new();
    private readonly HashSet<string> _streamedItems = new(StringComparer.Ordinal);
    private string? _finalAnswer;
    public CodexRpcProcess? Process { get; set; }
    public string ThreadId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string ActualModel { get; set; } = string.Empty;
    public AgentRuntimeInfo? RuntimeInfo { get; set; }
    public AgentTokenUsage Usage { get; private set; } = new();
    public bool Completed { get; private set; }
    public string FinalText => _finalAnswer ?? _text.ToString();

    public void VerifyModel()
    {
        if (!string.Equals(request.RequestedModel, ActualModel, StringComparison.Ordinal))
            throw new AgentToolProviderException(AgentFailureCategory.ModelSubstitution, "Codex reported a model different from the exact requested model.");
    }

    public async Task OnMessageAsync(JsonObject message, CancellationToken token)
    {
        string method = message["method"]?.GetValue<string>() ?? string.Empty;
        JsonObject parameters = message["params"] as JsonObject ?? new JsonObject();
        if (parameters["threadId"]?.GetValue<string>() is { } thread && ThreadId.Length > 0 && thread != ThreadId)
            throw new InvalidDataException("Codex sent an event for another thread.");
        if (message["id"] is { } id)
        {
            if (method != "item/tool/call" || Process is null)
            {
                if (Process is not null)
                    await Process.DenyAsync(id, token);
                throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "Codex requested a native permission or tool outside the host tool contract.");
            }
            VerifyModel();
            if (parameters["namespace"] is not null)
                throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "Namespaced native tools are not authorized by this host.");
            var call = new AgentToolCall
            {
                CallId = parameters["callId"]?.GetValue<string>() ?? string.Empty,
                Name = parameters["tool"]?.GetValue<string>() ?? string.Empty,
                ArgumentsJson = parameters["arguments"]?.ToJsonString() ?? "null",
            };
            AgentToolResult result = await tools.ExecuteAsync(call, token);
            var content = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = result.Content });
            if (!string.IsNullOrWhiteSpace(result.ImageDataUri))
            {
                CodexRunExecutor.ValidateImage(result.ImageDataUri);
                content.Add(new JsonObject { ["type"] = "inputImage", ["imageUrl"] = result.ImageDataUri });
            }
            await Process.WriteAsync(new JsonObject
            {
                ["id"] = id.DeepClone(),
                ["result"] = new JsonObject { ["contentItems"] = content, ["success"] = !result.IsError },
            }, token);
            return;
        }

        switch (method)
        {
            case "turn/started":
                TurnId = parameters["turn"]?["id"]?.GetValue<string>() ?? TurnId;
                break;
            case "model/rerouted":
                ActualModel = parameters["toModel"]?.GetValue<string>() ?? string.Empty;
                VerifyModel();
                break;
            case "account/updated":
                throw new AgentToolProviderException(AgentFailureCategory.Authentication, "The Codex account changed during this run.");
            case "item/agentMessage/delta":
                _streamedItems.Add(parameters["itemId"]?.GetValue<string>() ?? string.Empty);
                await AppendAsync(parameters["delta"]?.GetValue<string>() ?? string.Empty, token);
                break;
            case "item/started":
            case "item/completed":
                if (parameters["item"] is JsonObject item)
                {
                    string type = item["type"]?.GetValue<string>() ?? string.Empty;
                    if (type is not ("userMessage" or "agentMessage" or "reasoning" or "plan" or "contextCompaction" or "dynamicToolCall"))
                        throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "Codex emitted a native tool outside the isolated runtime profile.");
                    if (type == "agentMessage" && method == "item/completed")
                    {
                        string text = item["text"]?.GetValue<string>() ?? string.Empty;
                        if (!_streamedItems.Contains(item["id"]?.GetValue<string>() ?? string.Empty))
                            await AppendAsync(text, token);
                        if (item["phase"]?.GetValue<string>() == "final_answer")
                        {
                            if (text.Length > MaximumOutputCharacters)
                                throw new AgentToolProviderException(AgentFailureCategory.BudgetExceeded, "Codex exceeded the host output-size limit.");
                            _finalAnswer = text;
                        }
                    }
                }
                break;
            case "thread/tokenUsage/updated":
                if (parameters["tokenUsage"]?["total"] is JsonObject total)
                {
                    Usage = new AgentTokenUsage
                    {
                        InputTokens = total["inputTokens"]?.GetValue<long>() ?? 0,
                        OutputTokens = total["outputTokens"]?.GetValue<long>() ?? 0,
                        TotalTokens = total["totalTokens"]?.GetValue<long>() ?? 0,
                    };
                    await observer.OnEventAsync(new AgentRunEvent { Kind = AgentRunEventKind.Usage, Usage = Usage }, token);
                }
                break;
            case "turn/completed":
                string status = parameters["turn"]?["status"]?.GetValue<string>() ?? string.Empty;
                if (status == "interrupted")
                    throw new OperationCanceledException(token);
                if (status != "completed")
                    throw ProviderFailure(parameters["turn"]?["error"]);
                Completed = true;
                break;
            case "error":
                if (parameters["willRetry"]?.GetValue<bool>() == true)
                    await observer.OnEventAsync(new AgentRunEvent { Kind = AgentRunEventKind.Diagnostic, Message = "Codex is retrying its provider connection. No host tool call is retried." }, token);
                else
                    throw ProviderFailure(parameters["error"]);
                break;
            case "hook/started":
            case "mcpServer/startupStatus/updated":
            case "thread/environment/connected":
                throw new AgentToolProviderException(AgentFailureCategory.ToolDenied, "Codex started an ambient capability outside the isolated runtime profile.");
        }
    }

    private async Task AppendAsync(string delta, CancellationToken token)
    {
        if (_text.Length + delta.Length > MaximumOutputCharacters)
            throw new AgentToolProviderException(AgentFailureCategory.BudgetExceeded, "Codex exceeded the host output-size limit.");
        _text.Append(delta);
        await observer.OnEventAsync(new AgentRunEvent { Kind = AgentRunEventKind.TextDelta, Message = delta }, token);
    }

    private static AgentToolProviderException ProviderFailure(JsonNode? error)
    {
        string code = error?["codexErrorInfo"]?.ToString() ?? string.Empty;
        bool quota = code.Contains("usageLimit", StringComparison.OrdinalIgnoreCase)
            || code.Contains("rateLimit", StringComparison.OrdinalIgnoreCase);
        return new AgentToolProviderException(quota ? AgentFailureCategory.ProviderRateLimit : AgentFailureCategory.ProviderError,
            quota ? "The Codex account reached a usage limit. No API fallback was used."
                : "The Codex turn failed. Check account access, model availability, and the provider service. No API fallback was used.");
    }
}
