using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Reads Anthropic message events and keeps assistant blocks for tool continuation.
/// </summary>
public sealed class AnthropicMessagesStreamParser
{
    private const int MaxOutputCharacters = 16 * 1024 * 1024;
    private const int MaxToolInputCharacters = 8 * 1024 * 1024;
    private readonly SortedDictionary<int, JsonObject> _blocks = [];
    private readonly Dictionary<int, StringBuilder> _toolInputs = [];
    private readonly Dictionary<int, StringBuilder> _textBlocks = [];
    private readonly Dictionary<int, StringBuilder> _thinkingBlocks = [];
    private readonly Dictionary<int, StringBuilder> _signatures = [];
    private readonly HashSet<int> _openBlocks = [];
    private readonly StringBuilder _text = new();
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheCreationInputTokens;
    private long _cacheReadInputTokens;
    private bool _started;

    public string ResponseId { get; private set; } = string.Empty;
    public string ActualModel { get; private set; } = string.Empty;
    public int ProviderEventCount { get; private set; }
    public string LastProviderEventType { get; private set; } = string.Empty;
    public bool IsCompleted { get; private set; }
    public string StopReason { get; private set; } = string.Empty;
    public string Text => _text.ToString();

    public bool ProcessData(string data, out string textDelta)
    {
        textDelta = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException exception)
        {
            throw new AgentModelException(AgentFailureCategory.ProviderError,
                "The Anthropic stream returned invalid JSON.", diagnosticDetail: exception.Message,
                innerException: exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string type = GetString(root, "type") ?? string.Empty;
            ProviderEventCount++;
            LastProviderEventType = type.Length <= 128 ? type : type[..128];
            if (type.Length == 0)
                throw InvalidEvent("An Anthropic stream event has no type.");
            switch (type)
            {
                case "message_start":
                    if (_started)
                        throw InvalidEvent("The Anthropic stream started more than one message.");
                    if (!root.TryGetProperty("message", out JsonElement message))
                        throw InvalidEvent("message_start has no message.");
                    _started = true;
                    ResponseId = GetString(message, "id") ?? string.Empty;
                    ActualModel = GetString(message, "model") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(ResponseId) || string.IsNullOrWhiteSpace(ActualModel))
                        throw InvalidEvent("The Anthropic message has no response ID or model.");
                    if (message.TryGetProperty("usage", out JsonElement startUsage))
                        CaptureUsage(startUsage);
                    break;
                case "content_block_start":
                    if (!_started || IsCompleted)
                        throw InvalidEvent("A content block started outside a message.");
                    if (!TryGetIndex(root, out int startIndex)
                        || !root.TryGetProperty("content_block", out JsonElement startedBlock)
                        || JsonNode.Parse(startedBlock.GetRawText()) is not JsonObject block)
                        throw InvalidEvent("content_block_start has no valid block.");
                    string blockType = GetString(startedBlock, "type") ?? string.Empty;
                    if (blockType is not ("text" or "tool_use" or "thinking" or "redacted_thinking"))
                        throw InvalidEvent($"Anthropic returned unsupported content block '{blockType}'.");
                    if (_blocks.ContainsKey(startIndex))
                        throw InvalidEvent("Anthropic reused one content block index.");
                    _blocks.Add(startIndex, block);
                    if (!_openBlocks.Add(startIndex))
                        throw InvalidEvent("Anthropic started one content block twice.");
                    if (blockType == "tool_use")
                        _toolInputs[startIndex] = new StringBuilder();
                    else if (blockType == "text")
                    {
                        textDelta = GetString(startedBlock, "text") ?? string.Empty;
                        if (_text.Length + textDelta.Length > MaxOutputCharacters)
                            throw InvalidEvent("The Anthropic response exceeds the broker text limit.");
                        _textBlocks[startIndex] = new StringBuilder(textDelta);
                        _text.Append(textDelta);
                    }
                    else if (blockType == "thinking")
                    {
                        _thinkingBlocks[startIndex] = new StringBuilder(GetString(startedBlock, "thinking") ?? string.Empty);
                        _signatures[startIndex] = new StringBuilder(GetString(startedBlock, "signature") ?? string.Empty);
                    }
                    break;
                case "content_block_delta":
                    if (!TryGetIndex(root, out int deltaIndex)
                        || !_openBlocks.Contains(deltaIndex)
                        || !_blocks.TryGetValue(deltaIndex, out JsonObject? current)
                        || !root.TryGetProperty("delta", out JsonElement delta))
                        throw InvalidEvent("content_block_delta has no open block.");
                    string deltaType = GetString(delta, "type") ?? string.Empty;
                    switch (deltaType)
                    {
                        case "text_delta":
                            if (current["type"]?.GetValue<string>() != "text")
                                throw InvalidEvent("Anthropic sent text for a different block type.");
                            textDelta = GetString(delta, "text") ?? string.Empty;
                            if (_text.Length + textDelta.Length > MaxOutputCharacters)
                                throw InvalidEvent("The Anthropic response exceeds the broker text limit.");
                            _textBlocks[deltaIndex].Append(textDelta);
                            _text.Append(textDelta);
                            break;
                        case "input_json_delta":
                            if (current["type"]?.GetValue<string>() != "tool_use")
                                throw InvalidEvent("Anthropic sent tool input for a different block type.");
                            string inputDelta = GetString(delta, "partial_json") ?? string.Empty;
                            if (_toolInputs[deltaIndex].Length + inputDelta.Length > MaxToolInputCharacters)
                                throw InvalidEvent("The Anthropic tool input exceeds the broker limit.");
                            _toolInputs[deltaIndex].Append(inputDelta);
                            break;
                        case "thinking_delta":
                            if (current["type"]?.GetValue<string>() != "thinking")
                                throw InvalidEvent("Anthropic sent thinking for a different block type.");
                            if (_thinkingBlocks[deltaIndex].Length
                                + (GetString(delta, "thinking")?.Length ?? 0) > MaxOutputCharacters)
                                throw InvalidEvent("The Anthropic thinking block exceeds the broker limit.");
                            _thinkingBlocks[deltaIndex].Append(GetString(delta, "thinking") ?? string.Empty);
                            break;
                        case "signature_delta":
                            if (current["type"]?.GetValue<string>() != "thinking")
                                throw InvalidEvent("Anthropic sent a signature for a different block type.");
                            if (_signatures[deltaIndex].Length
                                + (GetString(delta, "signature")?.Length ?? 0) > MaxOutputCharacters)
                                throw InvalidEvent("The Anthropic signature exceeds the broker limit.");
                            _signatures[deltaIndex].Append(GetString(delta, "signature") ?? string.Empty);
                            break;
                        default:
                            throw InvalidEvent($"Anthropic returned unsupported block delta '{deltaType}'.");
                    }
                    break;
                case "content_block_stop":
                    if (!TryGetIndex(root, out int stopIndex)
                        || !_openBlocks.Remove(stopIndex)
                        || !_blocks.TryGetValue(stopIndex, out JsonObject? stopped))
                        throw InvalidEvent("content_block_stop has no open block.");
                    if (_toolInputs.TryGetValue(stopIndex, out StringBuilder? input))
                    {
                        try
                        {
                            if (input.Length > 0)
                                stopped["input"] = JsonNode.Parse(input.ToString())
                                    as JsonObject ?? throw InvalidEvent("A tool call requires object input.");
                            else if (stopped["input"] is not JsonObject)
                                throw InvalidEvent("A tool call requires object input.");
                        }
                        catch (JsonException exception)
                        {
                            throw new AgentModelException(AgentFailureCategory.ProviderError,
                                "An Anthropic tool call has invalid JSON input.",
                                diagnosticDetail: exception.Message, innerException: exception);
                        }
                    }
                    if (_textBlocks.TryGetValue(stopIndex, out StringBuilder? text))
                        stopped["text"] = text.ToString();
                    if (_thinkingBlocks.TryGetValue(stopIndex, out StringBuilder? thinking))
                    {
                        stopped["thinking"] = thinking.ToString();
                        stopped["signature"] = _signatures[stopIndex].ToString();
                    }
                    break;
                case "message_delta":
                    if (!_started || IsCompleted)
                        throw InvalidEvent("Anthropic sent message_delta outside a message.");
                    if (root.TryGetProperty("delta", out JsonElement messageDelta))
                        StopReason = GetString(messageDelta, "stop_reason") ?? StopReason;
                    if (root.TryGetProperty("usage", out JsonElement deltaUsage))
                        CaptureUsage(deltaUsage);
                    break;
                case "message_stop":
                    if (!_started || _openBlocks.Count > 0 || IsCompleted)
                        throw InvalidEvent("The Anthropic message stopped with unfinished content.");
                    IsCompleted = true;
                    break;
                case "error":
                    string errorType = root.TryGetProperty("error", out JsonElement error)
                        ? GetString(error, "type") ?? string.Empty : string.Empty;
                    string errorMessage = error.ValueKind == JsonValueKind.Object
                        ? GetString(error, "message") ?? "The Anthropic stream failed."
                        : "The Anthropic stream failed.";
                    throw new AgentModelException(AgentFailureCategory.ProviderError,
                        errorMessage, retryable: errorType is "overloaded_error" or "api_error");
                default:
                    if (type.StartsWith("content_block", StringComparison.Ordinal))
                        throw InvalidEvent($"Anthropic returned unsupported content event '{type}'.");
                    break;
            }
        }
        return textDelta.Length > 0;
    }

    public AgentModelTurnResult BuildResult(JsonArray messages)
    {
        if (!IsCompleted || string.IsNullOrWhiteSpace(ResponseId)
            || string.IsNullOrWhiteSpace(ActualModel) || string.IsNullOrWhiteSpace(StopReason))
            throw InvalidEvent("The Anthropic stream ended without a complete message.");
        if (StopReason == "max_tokens")
            throw new AgentModelException(AgentFailureCategory.BudgetExceeded,
                "The Anthropic message reached its max_tokens limit.");
        if (StopReason is not ("end_turn" or "tool_use" or "stop_sequence"))
            throw InvalidEvent($"The Anthropic message stopped with '{StopReason}'.");

        JsonArray content = [];
        List<AgentToolCall> calls = [];
        foreach (JsonObject block in _blocks.Values)
        {
            string type = block["type"]?.GetValue<string>() ?? string.Empty;
            if (type == "tool_use")
            {
                if (block["input"] is not JsonObject input)
                    throw InvalidEvent("An Anthropic tool call has no object input.");
                string id = block["id"]?.GetValue<string>() ?? string.Empty;
                string name = block["name"]?.GetValue<string>() ?? string.Empty;
                if (id.Length == 0 || name.Length == 0)
                    throw InvalidEvent("An Anthropic tool call has no ID or name.");
                calls.Add(new AgentToolCall
                {
                    CallId = id,
                    Name = name,
                    ArgumentsJson = input.ToJsonString(),
                });
            }
            else if (type is not ("text" or "thinking" or "redacted_thinking"))
                throw InvalidEvent($"Anthropic returned unsupported content block '{type}'.");
            if (type == "thinking" && string.IsNullOrWhiteSpace(block["signature"]?.GetValue<string>()))
                throw InvalidEvent("An Anthropic thinking block has no signature.");
            content.Add(block.DeepClone());
        }
        if (StopReason == "tool_use" && calls.Count == 0)
            throw InvalidEvent("Anthropic stopped for tool use without a tool call.");
        if (StopReason != "tool_use" && calls.Count > 0)
            throw InvalidEvent("Anthropic returned tool calls without a tool_use stop reason.");

        JsonArray continuation = (JsonArray)messages.DeepClone();
        continuation.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
        string outputText = _text.ToString();
        return new AgentModelTurnResult
        {
            ResponseId = ResponseId,
            ActualModel = ActualModel,
            OutputText = outputText,
            ToolCalls = calls,
            OutputItems = outputText.Length == 0 ? [] :
                [new AgentOutputItem { Kind = AgentOutputItemKind.Text, Text = outputText }],
            Usage = new AgentTokenUsage
            {
                InputTokens = _inputTokens + _cacheCreationInputTokens + _cacheReadInputTokens,
                OutputTokens = _outputTokens,
                TotalTokens = _inputTokens + _cacheCreationInputTokens + _cacheReadInputTokens + _outputTokens,
            },
            ContinuationJson = continuation.ToJsonString(),
        };
    }

    private void CaptureUsage(JsonElement usage)
    {
        if (TryGetLong(usage, "input_tokens", out long input))
            _inputTokens = input;
        if (TryGetLong(usage, "output_tokens", out long output))
            _outputTokens = output;
        if (TryGetLong(usage, "cache_creation_input_tokens", out long cacheCreation))
            _cacheCreationInputTokens = cacheCreation;
        if (TryGetLong(usage, "cache_read_input_tokens", out long cacheRead))
            _cacheReadInputTokens = cacheRead;
    }

    private static bool TryGetIndex(JsonElement root, out int index)
    {
        index = -1;
        return root.TryGetProperty("index", out JsonElement value) && value.TryGetInt32(out index)
            && index >= 0;
    }

    private static bool TryGetLong(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement item)
            && item.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static AgentModelException InvalidEvent(string detail)
        => new(AgentFailureCategory.ProviderError, detail);
}
