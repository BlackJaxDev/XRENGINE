using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Incrementally parses Responses API SSE payloads and preserves complete output items.
/// </summary>
public sealed class OpenAiResponsesStreamParser
{
    private readonly StringBuilder _text = new();
    private readonly Dictionary<int, PendingFunctionCall> _callsByIndex = [];
    private readonly List<PendingFunctionCall> _calls = [];
    private string? _completedResponseJson;

    public string ResponseId { get; private set; } = string.Empty;

    public string ActualModel { get; private set; } = string.Empty;

    public int MalformedEventCount { get; private set; }

    public int ProviderEventCount { get; private set; }

    public string LastProviderEventType { get; private set; } = string.Empty;

    public long? LastSequenceNumber { get; private set; }

    public bool IsTerminal { get; private set; }

    public bool IsCompleted { get; private set; }

    public string TerminalStatus { get; private set; } = string.Empty;

    public string IncompleteReason { get; private set; } = string.Empty;

    public string TerminalErrorMessage { get; private set; } = string.Empty;

    public string Text => _text.ToString();

    public AgentTokenUsage Usage { get; private set; } = new();

    public bool ProcessData(string data, out string textDelta)
    {
        textDelta = string.Empty;
        if (string.IsNullOrWhiteSpace(data) || string.Equals(data.Trim(), "[DONE]", StringComparison.Ordinal))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            string eventType = TryGetString(root, "type") ?? string.Empty;
            ProviderEventCount++;
            LastProviderEventType = BoundEventType(eventType);
            LastSequenceNumber = TryGetInt64(root, "sequence_number") ?? LastSequenceNumber;

            if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            {
                string message = ExtractErrorMessage(root) ?? "The Responses API stream reported a failure.";
                throw new AgentModelException(AgentFailureCategory.ProviderError, message);
            }

            if (string.Equals(eventType, "response.created", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("response", out JsonElement createdResponse))
            {
                CaptureResponseIdentity(createdResponse);
            }

            if (!eventType.StartsWith("response.function_call", StringComparison.OrdinalIgnoreCase)
                && TryExtractTextDelta(root, out string? delta)
                && !string.IsNullOrEmpty(delta))
            {
                textDelta = delta;
                _text.Append(delta);
            }

            if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out JsonElement addedItem))
            {
                CaptureFunctionCall(root, addedItem);
            }

            if (string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("item", out JsonElement completedItem))
            {
                CaptureFunctionCall(root, completedItem);
            }

            if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
                CaptureArgumentDelta(root);

            if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
                CaptureCompletedArguments(root);

            if (IsTerminalEvent(eventType)
                && root.TryGetProperty("response", out JsonElement response))
            {
                CaptureTerminalResponse(response, eventType);
            }

            return !string.IsNullOrEmpty(textDelta);
        }
        catch (JsonException)
        {
            MalformedEventCount++;
            return false;
        }
    }

    public AgentModelTurnResult BuildResult(string existingInputJson)
    {
        if (!IsCompleted)
            throw CreateTerminalException();

        JsonArray input = JsonNode.Parse(existingInputJson) as JsonArray
            ?? throw new AgentModelException(
                AgentFailureCategory.Internal,
                "Provider continuation input was not a JSON array.");

        JsonArray outputItems;
        List<AgentToolCall> toolCalls;
        List<AgentOutputItem> agentOutputItems;
        string outputText = Text;

        if (_completedResponseJson is not null)
        {
            using JsonDocument document = JsonDocument.Parse(_completedResponseJson);
            JsonElement response = document.RootElement;
            outputItems = response.TryGetProperty("output", out JsonElement output)
                && output.ValueKind == JsonValueKind.Array
                ? JsonNode.Parse(output.GetRawText()) as JsonArray ?? []
                : [];
            toolCalls = ExtractToolCalls(outputItems);

            if (string.IsNullOrEmpty(outputText))
                outputText = ExtractResponseText(response);
            agentOutputItems = ExtractAgentOutputItems(outputItems, outputText);
        }
        else
        {
            outputItems = [];
            toolCalls = _calls
                .Where(static call => !string.IsNullOrWhiteSpace(call.CallId))
                .Select(static call => call.ToAgentToolCall())
                .ToList();
            foreach (AgentToolCall call in toolCalls)
            {
                outputItems.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.CallId,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                });
            }
            agentOutputItems = string.IsNullOrEmpty(outputText)
                ? []
                : [new AgentOutputItem { Kind = AgentOutputItemKind.Text, Text = outputText }];
        }

        foreach (JsonNode? outputItem in outputItems)
            input.Add(outputItem?.DeepClone());

        return new AgentModelTurnResult
        {
            ResponseId = ResponseId,
            ActualModel = ActualModel,
            OutputText = outputText,
            ToolCalls = toolCalls,
            OutputItems = agentOutputItems,
            Usage = Usage,
            ContinuationJson = input.ToJsonString(),
        };
    }

    public static AgentModelTurnResult ParseNonStreamingResponse(string body, string existingInputJson)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error)
            && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            string message = error.ValueKind == JsonValueKind.Object
                ? TryGetString(error, "message") ?? "The Responses API returned an error."
                : "The Responses API returned an error.";
            throw new AgentModelException(AgentFailureCategory.ProviderError, message);
        }

        var parser = new OpenAiResponsesStreamParser();
        parser.CaptureTerminalResponse(root, "response." + (TryGetString(root, "status") ?? "completed"));
        if (!parser.IsCompleted)
            throw parser.CreateTerminalException();
        return parser.BuildResult(existingInputJson);
    }

    public AgentModelException CreateTerminalException()
    {
        if (IsCompleted)
        {
            return new AgentModelException(
                AgentFailureCategory.Internal,
                "A completed Responses API result was incorrectly treated as a failure.");
        }

        string status = string.IsNullOrWhiteSpace(TerminalStatus) ? "unknown" : TerminalStatus;
        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentModelException(
                AgentFailureCategory.Cancelled,
                "The Responses API response was cancelled.");
        }

        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            bool outputBudgetReached = IncompleteReason.Contains(
                "max_output_tokens",
                StringComparison.OrdinalIgnoreCase)
                || IncompleteReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase);
            string reason = string.IsNullOrWhiteSpace(IncompleteReason)
                ? "unspecified reason"
                : IncompleteReason;
            string guidance = outputBudgetReached
                ? " The response reached an output-token ceiling. Disable or raise an explicit broker limit; if none was configured, the selected model or provider limit was reached."
                : string.Empty;
            return new AgentModelException(
                outputBudgetReached ? AgentFailureCategory.BudgetExceeded : AgentFailureCategory.ProviderError,
                $"The Responses API response was incomplete: {reason}.{guidance}");
        }

        string message = string.IsNullOrWhiteSpace(TerminalErrorMessage)
            ? $"The Responses API response reached terminal status '{status}'."
            : TerminalErrorMessage;
        return new AgentModelException(AgentFailureCategory.ProviderError, message);
    }

    public static string ExtractResponseText(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ExtractResponseText(document.RootElement);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private void CaptureTerminalResponse(JsonElement response, string eventType)
    {
        _completedResponseJson = response.GetRawText();
        CaptureResponseIdentity(response);
        Usage = ExtractUsage(response);
        TerminalStatus = TryGetString(response, "status") ?? StatusFromEventType(eventType);
        IncompleteReason = ExtractIncompleteReason(response) ?? string.Empty;
        TerminalErrorMessage = ExtractErrorMessage(response) ?? string.Empty;
        IsTerminal = true;
        IsCompleted = string.Equals(TerminalStatus, "completed", StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureResponseIdentity(JsonElement response)
    {
        ResponseId = TryGetString(response, "id") ?? ResponseId;
        ActualModel = TryGetString(response, "model") ?? ActualModel;
    }

    private void CaptureFunctionCall(JsonElement root, JsonElement item)
    {
        if (!string.Equals(TryGetString(item, "type"), "function_call", StringComparison.OrdinalIgnoreCase))
            return;

        int index = TryGetInt32(root, "output_index") ?? -1;
        string callId = TryGetString(item, "call_id") ?? string.Empty;
        string name = TryGetString(item, "name") ?? string.Empty;
        string arguments = TryGetString(item, "arguments") ?? string.Empty;

        PendingFunctionCall? call = null;
        if (index >= 0)
            _callsByIndex.TryGetValue(index, out call);
        call ??= _calls.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(callId)
            && string.Equals(candidate.CallId, callId, StringComparison.Ordinal));

        if (call is null)
        {
            call = new PendingFunctionCall();
            _calls.Add(call);
            if (index >= 0)
                _callsByIndex[index] = call;
        }

        if (!string.IsNullOrEmpty(callId))
            call.CallId = callId;
        if (!string.IsNullOrEmpty(name))
            call.Name = name;
        if (!string.IsNullOrEmpty(arguments))
        {
            call.Arguments.Clear();
            call.Arguments.Append(arguments);
        }
    }

    private void CaptureArgumentDelta(JsonElement root)
    {
        int index = TryGetInt32(root, "output_index") ?? -1;
        string? delta = TryGetString(root, "delta");
        if (index >= 0 && delta is not null && _callsByIndex.TryGetValue(index, out PendingFunctionCall? call))
            call.Arguments.Append(delta);
    }

    private void CaptureCompletedArguments(JsonElement root)
    {
        int index = TryGetInt32(root, "output_index") ?? -1;
        string? arguments = TryGetString(root, "arguments");
        if (index >= 0 && arguments is not null && _callsByIndex.TryGetValue(index, out PendingFunctionCall? call))
        {
            call.Arguments.Clear();
            call.Arguments.Append(arguments);
        }
    }

    private static List<AgentToolCall> ExtractToolCalls(JsonArray outputItems)
    {
        List<AgentToolCall> calls = [];
        foreach (JsonNode? node in outputItems)
        {
            if (node is not JsonObject item
                || !string.Equals(item["type"]?.GetValue<string>(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            calls.Add(new AgentToolCall
            {
                CallId = item["call_id"]?.GetValue<string>() ?? string.Empty,
                Name = item["name"]?.GetValue<string>() ?? string.Empty,
                ArgumentsJson = item["arguments"]?.GetValue<string>() ?? "{}",
            });
        }

        return calls;
    }

    private static List<AgentOutputItem> ExtractAgentOutputItems(JsonArray outputItems, string outputText)
    {
        List<AgentOutputItem> items = [];
        if (!string.IsNullOrEmpty(outputText))
            items.Add(new AgentOutputItem { Kind = AgentOutputItemKind.Text, Text = outputText });

        foreach (JsonNode? node in outputItems)
        {
            if (node is null)
                continue;
            string? base64 = FindBase64Image(node);
            if (!string.IsNullOrWhiteSpace(base64))
            {
                items.Add(new AgentOutputItem
                {
                    Kind = AgentOutputItemKind.Image,
                    DataUri = $"data:image/png;base64,{base64}",
                });
            }
        }

        return items;
    }

    private static string? FindBase64Image(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            foreach ((string propertyName, JsonNode? value) in objectNode)
            {
                if (value is JsonValue jsonValue
                    && propertyName is "result" or "b64_json" or "image_base64"
                    && jsonValue.TryGetValue<string>(out string? base64)
                    && !string.IsNullOrWhiteSpace(base64))
                {
                    return base64;
                }

                if (value is not null)
                {
                    string? nested = FindBase64Image(value);
                    if (nested is not null)
                        return nested;
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (JsonNode? child in arrayNode)
            {
                if (child is null)
                    continue;
                string? nested = FindBase64Image(child);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static bool TryExtractTextDelta(JsonElement root, out string? text)
    {
        text = null;
        string eventType = TryGetString(root, "type") ?? string.Empty;
        if ((string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.text.delta", StringComparison.OrdinalIgnoreCase))
            && root.TryGetProperty("delta", out JsonElement directDelta)
            && directDelta.ValueKind == JsonValueKind.String)
        {
            text = directDelta.GetString();
            return text is not null;
        }

        if (!root.TryGetProperty("delta", out JsonElement delta))
            return false;
        if (delta.ValueKind == JsonValueKind.String)
        {
            text = delta.GetString();
            return text is not null;
        }
        if (delta.ValueKind != JsonValueKind.Object)
            return false;

        foreach (string propertyName in new[] { "text", "content", "value" })
        {
            if (delta.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                text = value.GetString();
                return text is not null;
            }
        }

        return false;
    }

    private static string ExtractResponseText(JsonElement response)
    {
        string? directText = TryGetString(response, "output_text");
        if (!string.IsNullOrEmpty(directText))
            return directText;

        if (!response.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement block in content.EnumerateArray())
            {
                string? text = TryGetString(block, "text");
                if (text is not null)
                    builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static AgentTokenUsage ExtractUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return new AgentTokenUsage();
        }

        long input = TryGetInt64(usage, "input_tokens") ?? 0;
        long output = TryGetInt64(usage, "output_tokens") ?? 0;
        long total = TryGetInt64(usage, "total_tokens") ?? input + output;
        return new AgentTokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
        };
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error))
        {
            if (error.ValueKind == JsonValueKind.Object)
                return TryGetString(error, "message");
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        if (root.TryGetProperty("response", out JsonElement response)
            && response.TryGetProperty("error", out JsonElement responseError)
            && responseError.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(responseError, "message");
        }

        return TryGetString(root, "message");
    }

    private static string? ExtractIncompleteReason(JsonElement response)
        => response.TryGetProperty("incomplete_details", out JsonElement details)
            && details.ValueKind == JsonValueKind.Object
                ? TryGetString(details, "reason")
                : null;

    private static bool IsTerminalEvent(string eventType)
        => string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.incomplete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "response.cancelled", StringComparison.OrdinalIgnoreCase);

    private static string StatusFromEventType(string eventType)
        => eventType.ToLowerInvariant() switch
        {
            "response.completed" or "response.done" => "completed",
            "response.incomplete" => "incomplete",
            "response.failed" => "failed",
            "response.cancelled" => "cancelled",
            _ => "unknown",
        };

    private static string BoundEventType(string eventType)
        => eventType.Length <= 128 ? eventType : eventType[..128];

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static int? TryGetInt32(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.TryGetInt32(out int result)
                ? result
                : null;

    private static long? TryGetInt64(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.TryGetInt64(out long result)
                ? result
                : null;

    private sealed class PendingFunctionCall
    {
        public string CallId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();

        public AgentToolCall ToAgentToolCall()
            => new()
            {
                CallId = CallId,
                Name = Name,
                ArgumentsJson = Arguments.Length == 0 ? "{}" : Arguments.ToString(),
            };
    }
}
