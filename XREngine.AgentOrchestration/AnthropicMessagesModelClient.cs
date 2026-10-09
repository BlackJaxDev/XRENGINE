using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Sends stateless streaming turns to the public Anthropic Messages API.
/// </summary>
public sealed class AnthropicMessagesModelClient : IAgentModelClient
{
    private const int MaxEventCharacters = 8 * 1024 * 1024;
    public static readonly Uri PublicMessagesEndpoint = new("https://api.anthropic.com/v1/messages");

    private readonly HttpClient _httpClient;
    private readonly Func<string> _apiKeyProvider;
    private readonly Func<string?>? _workspaceIdProvider;
    private readonly Uri _endpoint;

    /// <summary>
    /// Creates a client. Supply a workspace ID for an Anthropic multi-workspace key.
    /// </summary>
    public AnthropicMessagesModelClient(
        HttpClient httpClient,
        Func<string> apiKeyProvider,
        Uri? endpoint = null,
        Func<string?>? workspaceIdProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _endpoint = endpoint ?? PublicMessagesEndpoint;
        _workspaceIdProvider = workspaceIdProvider;
    }

    public async Task<AgentModelTurnResult> CreateResponseAsync(
        AgentModelTurnRequest request, IAgentRunObserver observer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);
        ValidateRequest(request);
        string apiKey = _apiKeyProvider()?.Trim() ?? string.Empty;
        if (apiKey.Length == 0)
            throw new AgentModelException(AgentFailureCategory.Authentication,
                "The configured Anthropic API key environment variable is empty.");
        string? workspaceId = _workspaceIdProvider?.Invoke()?.Trim();
        if (!string.IsNullOrEmpty(workspaceId) && !IsValidWorkspaceId(workspaceId))
            throw new AgentModelException(AgentFailureCategory.Validation,
                "The configured Anthropic workspace ID is invalid.");

        JsonArray messages = BuildMessages(request);
        JsonObject payload = BuildPayload(request, messages);
        Stopwatch stopwatch = Stopwatch.StartNew();
        var parser = new AnthropicMessagesStreamParser();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            if (!string.IsNullOrEmpty(workspaceId))
                message.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);
            message.Headers.UserAgent.ParseAdd("XREngine-LocalAgentBroker/0.2");
            message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new AgentModelException(AgentFailureCategory.Transport,
                    "The Anthropic Messages API request could not be sent.", retryable: true,
                    diagnosticDetail: Redact(exception.Message, apiKey), innerException: exception);
            }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw await CreateHttpExceptionAsync(response, apiKey, cancellationToken);
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    throw new AgentModelException(AgentFailureCategory.ProviderError,
                        "The Anthropic Messages API did not return an event stream.");

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                await observer.OnEventAsync(new AgentRunEvent
                {
                    Kind = AgentRunEventKind.Status,
                    Message = "provider_stream_connected",
                }, cancellationToken);

                var eventData = new StringBuilder();
                while (!cancellationToken.IsCancellationRequested && !parser.IsCompleted)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                        break;
                    if (line.Length == 0)
                    {
                        if (eventData.Length == 0)
                            continue;
                        await ProcessEventAsync(eventData.ToString());
                        eventData.Clear();
                        continue;
                    }
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (eventData.Length + line.Length > MaxEventCharacters)
                            throw new AgentModelException(AgentFailureCategory.ProviderError,
                                "An Anthropic stream event exceeds the broker limit.");
                        if (eventData.Length > 0)
                            eventData.Append('\n');
                        eventData.Append(line.AsSpan(5).TrimStart());
                    }
                }
                if (eventData.Length > 0 && !parser.IsCompleted)
                    await ProcessEventAsync(eventData.ToString());
                if (!parser.IsCompleted)
                    throw new AgentModelException(AgentFailureCategory.Transport,
                        "The Anthropic stream ended before message_stop.", retryable: parser.Text.Length == 0);

                AgentModelTurnResult result = parser.BuildResult(messages);
                if (!string.Equals(result.ActualModel, request.Run.RequestedModel, StringComparison.Ordinal))
                    throw new AgentModelException(AgentFailureCategory.ModelSubstitution,
                        "Anthropic returned a different model than requested.");
                return result with { ProviderAttempt = CreateDiagnostic("completed") };
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new AgentModelOperationCanceledException(
                CreateDiagnostic("cancelled"), exception, cancellationToken);
        }
        catch (IOException exception)
        {
            var transport = new AgentModelException(AgentFailureCategory.Transport,
                "The Anthropic stream could not be read.", retryable: parser.Text.Length == 0,
                diagnosticDetail: Redact(exception.Message, apiKey), innerException: exception);
            throw transport.WithProviderAttempt(CreateDiagnostic("transport_error", transport));
        }
        catch (AgentModelException exception) when (exception.ProviderAttempt is null)
        {
            var sanitized = new AgentModelException(exception.Category,
                Redact(exception.Message, apiKey), exception.Retryable, exception.ProviderStatus,
                exception.RetryAfter, Redact(exception.DiagnosticDetail, apiKey));
            throw sanitized.WithProviderAttempt(CreateDiagnostic(
                exception.Category == AgentFailureCategory.Transport ? "transport_error" : "provider_error",
                sanitized));
        }

        async Task ProcessEventAsync(string data)
        {
            bool hasText = parser.ProcessData(data, out string delta);
            if (parser.ActualModel.Length > 0
                && !string.Equals(parser.ActualModel, request.Run.RequestedModel, StringComparison.Ordinal))
                throw new AgentModelException(AgentFailureCategory.ModelSubstitution,
                    "Anthropic returned a different model than requested.");
            if (hasText)
                await observer.OnEventAsync(new AgentRunEvent
                {
                    Kind = AgentRunEventKind.TextDelta,
                    Message = delta,
                }, cancellationToken);
        }

        AgentProviderAttemptDiagnostic CreateDiagnostic(string outcome, AgentModelException? error = null)
            => new()
            {
                TurnNumber = request.TurnIndex + 1,
                AttemptNumber = request.AttemptNumber,
                Outcome = outcome,
                ResponseId = parser.ResponseId,
                ActualModel = parser.ActualModel,
                ProviderEventCount = parser.ProviderEventCount,
                LastProviderEventType = parser.LastProviderEventType,
                TerminalStatus = parser.IsCompleted ? parser.StopReason : string.Empty,
                IncompleteReason = parser.StopReason == "max_tokens" ? "max_tokens" : string.Empty,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                FailureCategory = error?.Category,
                ProviderStatus = error?.ProviderStatus,
                Retryable = error?.Retryable == true,
            };
    }

    private static void ValidateRequest(AgentModelTurnRequest request)
    {
        if (request.Run.UseBackgroundMode)
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic Messages does not support broker background mode.");
        if (request.Run.HostedTools.Count > 0)
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic Messages does not support the selected hosted tools.");
        if (request.Run.RequireToolUse && request.Run.RequestedModel != "claude-haiku-5-5")
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Forced tool use is not supported for the selected Anthropic models.");
        if (request.Run.RequestedModel is not (
            "claude-fable-5-1" or "claude-opus-5-5" or "claude-sonnet-5-5" or "claude-haiku-5-5"))
            throw new AgentModelException(AgentFailureCategory.ModelUnavailable,
                "The requested Anthropic model is not supported by this broker.");
        if (request.Run.ReasoningEffort?.ToLowerInvariant() is not
            ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new AgentModelException(AgentFailureCategory.Validation,
                "The selected Anthropic reasoning effort is not supported.");
    }

    private static JsonArray BuildMessages(AgentModelTurnRequest request)
    {
        JsonArray messages;
        if (string.IsNullOrWhiteSpace(request.ContinuationJson))
        {
            JsonArray content = [new JsonObject { ["type"] = "text", ["text"] = request.Prompt }];
            foreach (AgentContextFileSnapshot snapshot in request.Run.ContextFileSnapshots)
            {
                JsonObject context = AgentContextFileInputBuilder.Build(snapshot);
                content.Add(new JsonObject { ["type"] = "text", ["text"] = context["text"]?.GetValue<string>() });
            }
            if (!string.IsNullOrWhiteSpace(request.Run.InitialImageDataUri))
                content.Add(BuildImage(request.Run.InitialImageDataUri));
            messages = [new JsonObject { ["role"] = "user", ["content"] = content }];
        }
        else
        {
            try
            {
                messages = JsonNode.Parse(request.ContinuationJson) as JsonArray
                    ?? throw new JsonException("The continuation is not an array.");
            }
            catch (JsonException exception)
            {
                throw new AgentModelException(AgentFailureCategory.Internal,
                    "Anthropic continuation state is invalid.", innerException: exception);
            }
        }

        if (request.ToolOutputs.Count > 0)
        {
            JsonArray results = [];
            foreach (AgentModelToolOutput output in request.ToolOutputs)
            {
                JsonArray resultContent = [new JsonObject { ["type"] = "text", ["text"] = output.Content }];
                if (!string.IsNullOrWhiteSpace(output.ImageDataUri))
                    resultContent.Add(BuildImage(output.ImageDataUri));
                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = output.CallId,
                    ["content"] = resultContent,
                });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
        return messages;
    }

    private static JsonObject BuildImage(string dataUri)
    {
        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic image input requires a base64 data URI.");
        int separator = dataUri.IndexOf(';');
        int comma = dataUri.IndexOf(',', separator + 1);
        if (separator < 0 || comma < 0
            || !string.Equals(dataUri[(separator + 1)..comma], "base64", StringComparison.OrdinalIgnoreCase))
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic image input requires base64 data.");
        string mediaType = dataUri[5..separator].ToLowerInvariant();
        if (mediaType is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp"))
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic does not support the selected image media type.");
        string base64 = dataUri[(comma + 1)..];
        try { Convert.FromBase64String(base64); }
        catch (FormatException exception)
        {
            throw new AgentModelException(AgentFailureCategory.Validation,
                "Anthropic image input is not valid base64.", innerException: exception);
        }
        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = mediaType,
                ["data"] = base64,
            },
        };
    }

    private static JsonObject BuildPayload(AgentModelTurnRequest request, JsonArray messages)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Run.RequestedModel,
            ["max_tokens"] = request.MaxOutputTokens > 0 ? Math.Min(request.MaxOutputTokens, 128_000) : 128_000,
            ["messages"] = messages.DeepClone(),
            ["stream"] = true,
            ["thinking"] = new JsonObject { ["type"] = "adaptive" },
            ["output_config"] = new JsonObject { ["effort"] = request.Run.ReasoningEffort.ToLowerInvariant() },
        };
        string verbosity = request.Run.TextVerbosity.ToLowerInvariant() switch
        {
            "low" => "Keep the visible final answer brief.",
            "high" => "Give a detailed visible final answer.",
            _ => "Give a clear visible final answer.",
        };
        payload["system"] = string.IsNullOrWhiteSpace(request.Run.SystemInstructions)
            ? verbosity : request.Run.SystemInstructions + "\n\n" + verbosity;
        if (request.Tools.Count > 0)
        {
            JsonArray tools = [];
            foreach (AgentToolDefinition tool in request.Tools)
            {
                JsonNode schema;
                try { schema = JsonNode.Parse(tool.InputSchemaJson) ?? throw new JsonException(); }
                catch (JsonException exception)
                {
                    throw new AgentModelException(AgentFailureCategory.Validation,
                        $"Tool '{tool.Name}' has invalid input schema JSON.", innerException: exception);
                }
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = schema,
                });
            }
            payload["tools"] = tools;
            if (request.ForceTextResponse)
                payload["tool_choice"] = new JsonObject { ["type"] = "none" };
            else if (request.TurnIndex == 0 && request.Run.RequireToolUse)
                payload["tool_choice"] = new JsonObject { ["type"] = "any" };
        }
        return payload;
    }

    private static async Task<AgentModelException> CreateHttpExceptionAsync(
        HttpResponseMessage response, string apiKey, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        int status = (int)response.StatusCode;
        string message = $"The Anthropic Messages API returned HTTP {status}.";
        string errorType = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.TryGetProperty("message", out JsonElement detail))
                    message = detail.GetString() ?? message;
                if (error.TryGetProperty("type", out JsonElement type))
                    errorType = type.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        AgentFailureCategory category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AgentFailureCategory.Authentication,
            HttpStatusCode.TooManyRequests => AgentFailureCategory.ProviderRateLimit,
            HttpStatusCode.NotFound => AgentFailureCategory.ModelUnavailable,
            _ => AgentFailureCategory.ProviderError,
        };
        bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout || status == 529 || errorType == "overloaded_error";
        return new AgentModelException(category, Redact(message, apiKey), retryable,
            status, response.Headers.RetryAfter?.Delta,
            diagnosticDetail: $"Anthropic error type: {errorType}; HTTP {status}.");
    }

    private static string Redact(string value, string secret)
        => secret.Length == 0 ? value : value.Replace(secret, "[redacted]", StringComparison.Ordinal);

    private static bool IsValidWorkspaceId(string workspaceId)
    {
        if (!workspaceId.StartsWith("wrkspc_", StringComparison.Ordinal) || workspaceId.Length <= 7)
            return false;
        foreach (char character in workspaceId.AsSpan(7))
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
                return false;
        }
        return true;
    }
}
