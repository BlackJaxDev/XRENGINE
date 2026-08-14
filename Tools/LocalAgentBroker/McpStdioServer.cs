using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Newline-delimited JSON-RPC MCP stdio host. Only protocol responses are written to stdout.
/// </summary>
internal sealed class McpStdioServer(
    AgentRunRegistry registry,
    TextReader input,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            // Windows PowerShell 5 may emit a UTF-8 BOM through redirected stdin.
            if (line.StartsWith("\u00EF\u00BB\u00BF", StringComparison.Ordinal))
                line = line[3..];
            else if (line.StartsWith('\uFEFF'))
                line = line[1..];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? response = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                response = await HandleRequestAsync(document.RootElement, cancellationToken);
            }
            catch (JsonException exception)
            {
                response = ErrorResponse(id: null, -32700, "Parse error", exception.Message);
            }
            catch (Exception exception)
            {
                await error.WriteLineAsync($"Local agent broker request failure: {exception.Message}");
                response = ErrorResponse(id: null, -32603, "Internal error");
            }

            if (response is null)
                continue;
            await output.WriteLineAsync(response.ToJsonString(s_jsonOptions));
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task<JsonNode?> HandleRequestAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        JsonNode? id = request.TryGetProperty("id", out JsonElement idElement)
            ? JsonNode.Parse(idElement.GetRawText())
            : null;
        string? method = request.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;

        if (method is null)
            return ErrorResponse(id, -32600, "Invalid Request");
        if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal)
            || string.Equals(method, "notifications/cancelled", StringComparison.Ordinal))
        {
            return null;
        }

        JsonElement? parameters = request.TryGetProperty("params", out JsonElement parameterElement)
            ? parameterElement
            : null;
        return method switch
        {
            "initialize" => SuccessResponse(id, BuildInitializeResult(parameters)),
            "ping" => SuccessResponse(id, new JsonObject()),
            "tools/list" => SuccessResponse(id, BuildToolsList()),
            "tools/call" => SuccessResponse(
                id,
                await HandleToolCallAsync(parameters, cancellationToken)),
            _ => ErrorResponse(id, -32601, $"Method not found: {method}"),
        };
    }

    private async Task<JsonObject> HandleToolCallAsync(
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is null
            || parameters.Value.ValueKind != JsonValueKind.Object
            || !parameters.Value.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return ToolResult(
                new { error = "tools/call requires a string name" },
                isError: true);
        }

        string name = nameElement.GetString() ?? string.Empty;
        JsonElement arguments = parameters.Value.TryGetProperty("arguments", out JsonElement argumentElement)
            ? argumentElement
            : JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            object result = name switch
            {
                "recommend_agent_route" => Recommend(arguments),
                "start_agent_run" => StartRun(arguments),
                "get_agent_run" => GetRun(arguments),
                "cancel_agent_run" => CancelRun(arguments),
                "list_agent_runs" => ListRuns(arguments),
                _ => throw new KeyNotFoundException($"Unknown broker tool '{name}'."),
            };
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            return ToolResult(result, isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolResult(
                new
                {
                    error = exception.Message,
                    type = exception.GetType().Name,
                },
                isError: true);
        }
    }

    private static AgentRouteRecommendation Recommend(JsonElement arguments)
    {
        string objective = RequiredString(arguments, "objective");
        IReadOnlyList<string> constraints = ReadStringArray(arguments, "constraints");
        return AgentRouteAdvisor.Recommend(objective, constraints);
    }

    private object StartRun(JsonElement arguments)
    {
        AgentRunRequest request = arguments.Deserialize<AgentRunRequest>(s_jsonOptions)
            ?? throw new ArgumentException("start_agent_run arguments are invalid.");
        request = AgentRunBudgetPolicy.ApplyDefaults(request, arguments);
        string runId = registry.Start(request);
        return new
        {
            runId,
            status = AgentRunStatus.Queued,
            requestedModel = request.RequestedModel,
            actualModel = string.Empty,
            requestedReasoningEffort = request.ReasoningEffort,
            requestedTextVerbosity = request.TextVerbosity,
            maxOutputTokens = request.Budget.MaxOutputTokens,
            maxElapsedSeconds = request.Budget.MaxElapsedSeconds,
            useBackgroundMode = request.UseBackgroundMode,
            message = "Run queued. Poll get_agent_run until status is terminal.",
        };
    }

    private AgentRunSnapshot GetRun(JsonElement arguments)
        => registry.Get(RequiredString(arguments, "run_id"));

    private object CancelRun(JsonElement arguments)
    {
        string runId = RequiredString(arguments, "run_id");
        bool cancellationRequested = registry.Cancel(runId);
        return new { runId, cancellationRequested };
    }

    private IReadOnlyList<AgentRunListItem> ListRuns(JsonElement arguments)
    {
        int limit = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("limit", out JsonElement limitElement)
            && limitElement.TryGetInt32(out int parsed)
                ? parsed
                : 20;
        return registry.List(limit);
    }

    private static JsonObject BuildInitializeResult(JsonElement? parameters)
    {
        string requestedVersion = "2024-11-05";
        if (parameters is not null
            && parameters.Value.ValueKind == JsonValueKind.Object
            && parameters.Value.TryGetProperty("protocolVersion", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            requestedVersion = versionElement.GetString() ?? requestedVersion;
        }

        return new JsonObject
        {
            ["protocolVersion"] = requestedVersion,
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "XREngine.LocalAgentBroker",
                ["version"] = "0.7.0",
            },
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["instructions"] =
                "Use for automatic bounded API-worker delegation under the XRENGINE standing authorization. This broker does not switch the current Codex model. Omit editor_session for reasoning-only runs.",
        };
    }

    private static JsonObject BuildToolsList()
    {
        var tools = new JsonArray();
        foreach (McpToolSpec tool in BrokerMcpToolCatalog.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = tool.IsReadOnly,
                    ["destructiveHint"] = false,
                },
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject ToolResult(object value, bool isError)
    {
        JsonNode structured = JsonSerializer.SerializeToNode(value, s_jsonOptions) ?? new JsonObject();
        string text = structured.ToJsonString(s_jsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["structuredContent"] = structured,
            ["isError"] = isError,
        };
    }

    private static JsonObject SuccessResponse(JsonNode? id, JsonNode result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

    private static JsonObject ErrorResponse(
        JsonNode? id,
        int code,
        string message,
        string? data = null)
    {
        var errorObject = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (!string.IsNullOrWhiteSpace(data))
            errorObject["data"] = data;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = errorObject,
        };
    }

    private static string RequiredString(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"{propertyName} is required.");
        }

        return value.GetString()!;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToArray();
    }
}
