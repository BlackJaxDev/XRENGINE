using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentOrchestration;

/// <summary>
/// BCL-only MCP-over-HTTP client with broker-side tool filtering and error preservation.
/// </summary>
public sealed class HttpMcpToolProvider : IAgentToolProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _authToken;
    private readonly AgentToolPolicy _policy;
    private readonly TimeSpan _toolTimeout;
    private IReadOnlyDictionary<string, AgentToolDefinition>? _visibleTools;

    public HttpMcpToolProvider(
        HttpClient httpClient,
        Uri endpoint,
        AgentToolPolicy policy,
        string? authToken = null,
        TimeSpan? toolTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
        _toolTimeout = toolTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<string> PreflightAsync(string expectedEditorSession, CancellationToken cancellationToken)
    {
        JsonElement result = await SendRequestAsync("ping", parameters: null, cancellationToken);
        string? reportedSession = null;
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("status", out JsonElement status)
            && status.TryGetProperty("editorSession", out JsonElement editorSession)
            && editorSession.TryGetProperty("name", out JsonElement name)
            && name.ValueKind == JsonValueKind.String)
        {
            reportedSession = name.GetString();
        }

        if (!string.Equals(reportedSession, expectedEditorSession, StringComparison.Ordinal))
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.ToolDiscovery,
                $"Editor MCP preflight reported session '{reportedSession ?? "<none>"}' instead of '{expectedEditorSession}'.");
        }

        return reportedSession!;
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await SendRequestAsync("tools/list", parameters: null, cancellationToken);
        if (!result.TryGetProperty("tools", out JsonElement toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.ToolDiscovery,
                "Editor MCP tools/list did not return a tools array.");
        }

        var visible = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        foreach (JsonElement tool in toolsElement.EnumerateArray())
        {
            string name = TryGetString(tool, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            (bool readOnly, bool destructive) = ReadToolAnnotations(tool, name);
            if (!IsAllowed(name, readOnly, destructive))
                continue;

            string schema = tool.TryGetProperty("inputSchema", out JsonElement inputSchema)
                ? inputSchema.GetRawText()
                : """{"type":"object","properties":{}}""";
            visible[name] = new AgentToolDefinition
            {
                Name = name,
                Description = TryGetString(tool, "description") ?? string.Empty,
                InputSchemaJson = schema,
                IsReadOnly = readOnly,
                IsDestructive = destructive,
            };
        }

        _visibleTools = visible;
        return visible.Values.ToArray();
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, AgentToolDefinition> visibleTools = _visibleTools
            ?? throw new AgentToolProviderException(
                AgentFailureCategory.ToolDiscovery,
                "Tools must be listed before a tool can be called.");

        if (!visibleTools.ContainsKey(call.Name))
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.ToolDenied,
                $"Tool '{call.Name}' is not visible under the broker-side policy.");
        }

        JsonNode arguments;
        try
        {
            arguments = JsonNode.Parse(call.ArgumentsJson) ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            return new AgentToolResult
            {
                Content = $"MCP tool arguments are not valid JSON: {exception.Message}",
                IsError = true,
            };
        }

        var parameters = new JsonObject
        {
            ["name"] = call.Name,
            ["arguments"] = arguments,
        };

        try
        {
            JsonElement result = await SendRequestAsync("tools/call", parameters, cancellationToken, _toolTimeout);
            return ParseToolResult(result);
        }
        catch (AgentToolProviderException exception)
        {
            return new AgentToolResult
            {
                Content = exception.Message,
                IsError = true,
            };
        }
    }

    private bool IsAllowed(string name, bool readOnly, bool destructive)
    {
        if (_policy.DeniedTools.Contains(name, StringComparer.Ordinal))
            return false;
        if (_policy.AllowedTools.Count > 0 && !_policy.AllowedTools.Contains(name, StringComparer.Ordinal))
            return false;
        if (destructive)
            return _policy.AllowMutation && _policy.AllowDestructive;
        if (!readOnly)
            return _policy.AllowMutation;
        return true;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        CancellationToken requestToken = timeoutSource.Token;

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
        };
        if (parameters is not null)
            payload["params"] = parameters;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_authToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken);
            string body = await response.Content.ReadAsStringAsync(requestToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AgentToolProviderException(
                    AgentFailureCategory.Transport,
                    $"Editor MCP returned HTTP {(int)response.StatusCode}.",
                    RedactDiagnostic(body));
            }

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                string message = RedactDiagnostic(
                    TryGetString(error, "message") ?? "Editor MCP returned a JSON-RPC error.");
                string code = error.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.GetRawText()
                    : "unknown";
                throw new AgentToolProviderException(
                    AgentFailureCategory.ToolError,
                    $"Editor MCP error {code}: {message}",
                    RedactDiagnostic(error.GetRawText()));
            }
            if (!root.TryGetProperty("result", out JsonElement result))
            {
                throw new AgentToolProviderException(
                    AgentFailureCategory.Transport,
                    "Editor MCP response did not contain result or error.");
            }

            return result.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.Transport,
                $"Editor MCP method '{method}' timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.Transport,
                $"Editor MCP method '{method}' could not be sent.",
                exception.Message,
                exception);
        }
        catch (JsonException exception)
        {
            throw new AgentToolProviderException(
                AgentFailureCategory.Transport,
                $"Editor MCP method '{method}' returned malformed JSON.",
                exception.Message,
                exception);
        }
    }

    private static AgentToolResult ParseToolResult(JsonElement result)
    {
        var content = new StringBuilder();
        string? imageDataUri = null;
        string? imagePath = null;
        if (result.TryGetProperty("content", out JsonElement contentArray)
            && contentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in contentArray.EnumerateArray())
            {
                string? type = TryGetString(item, "type");
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    string? text = TryGetString(item, "text");
                    if (text is null)
                        continue;
                    if (content.Length > 0)
                        content.AppendLine();
                    content.Append(text);
                }
                else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                {
                    string? data = TryGetString(item, "data");
                    string mimeType = TryGetString(item, "mimeType") ?? "image/png";
                    if (!string.IsNullOrWhiteSpace(data))
                        imageDataUri = $"data:{mimeType};base64,{data}";
                }
            }
        }

        if (result.TryGetProperty("structuredContent", out JsonElement structured))
            imagePath = TryFindImagePath(structured);
        if (imagePath is null && result.TryGetProperty("data", out JsonElement legacyData))
            imagePath = TryFindImagePath(legacyData);

        if (content.Length == 0)
            content.Append(result.GetRawText());

        bool isError = result.TryGetProperty("isError", out JsonElement isErrorElement)
            && isErrorElement.ValueKind is JsonValueKind.True;
        return new AgentToolResult
        {
            Content = content.ToString(),
            IsError = isError,
            ImageDataUri = imageDataUri,
            ImagePath = imagePath,
        };
    }

    private static (bool ReadOnly, bool Destructive) ReadToolAnnotations(JsonElement tool, string name)
    {
        if (tool.TryGetProperty("annotations", out JsonElement annotations)
            && annotations.ValueKind == JsonValueKind.Object)
        {
            bool readOnly = annotations.TryGetProperty("readOnlyHint", out JsonElement readOnlyElement)
                && readOnlyElement.ValueKind == JsonValueKind.True;
            bool destructive = annotations.TryGetProperty("destructiveHint", out JsonElement destructiveElement)
                && destructiveElement.ValueKind == JsonValueKind.True;
            return (readOnly, destructive);
        }

        bool heuristicReadOnly = name.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("find_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("search_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("capture_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("probe_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("dump_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("validate_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("query_", StringComparison.OrdinalIgnoreCase);
        bool heuristicDestructive = name.StartsWith("delete_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("remove_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("write_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("save_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("import_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("export_", StringComparison.OrdinalIgnoreCase);
        return (heuristicReadOnly, heuristicDestructive);
    }

    private static string? TryFindImagePath(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (string propertyName in new[] { "path", "imagePath", "outputPath" })
        {
            string? candidate = TryGetString(element, propertyName);
            if (candidate is not null && IsImagePath(candidate))
                return candidate;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string? nested = TryFindImagePath(property.Value);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static bool IsImagePath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private string RedactDiagnostic(string value)
    {
        string bounded = value.Length <= 4_096 ? value : value[..4_096] + "…";
        if (!string.IsNullOrEmpty(_authToken))
            bounded = bounded.Replace(_authToken, "[REDACTED]", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(
            bounded,
            "(?i)(authorization|api[_-]?key|token|secret)(\\s*[=:]\\s*)[^\\s,\\\"}]+",
            "$1$2[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
