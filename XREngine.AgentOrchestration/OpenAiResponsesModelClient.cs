using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentOrchestration;

/// <summary>
/// Public OpenAI Responses API transport with streaming, stateless continuation, and safe errors.
/// </summary>
public sealed class OpenAiResponsesModelClient : IAgentModelClient
{
    public static readonly Uri PublicResponsesEndpoint = new("https://api.openai.com/v1/responses");

    private readonly HttpClient _httpClient;
    private readonly Func<string> _apiKeyProvider;
    private readonly Uri _endpoint;

    public OpenAiResponsesModelClient(
        HttpClient httpClient,
        Func<string> apiKeyProvider,
        Uri? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _endpoint = endpoint ?? PublicResponsesEndpoint;
    }

    public async Task<AgentModelTurnResult> CreateResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        string apiKey = _apiKeyProvider().Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AgentModelException(
                AgentFailureCategory.Authentication,
                "The configured OpenAI API key environment variable is empty.");
        }

        JsonArray input = BuildInput(request);
        JsonObject payload = BuildPayload(request, input);
        return request.Run.UseBackgroundMode
            ? await CreateBackgroundResponseAsync(
                request,
                observer,
                apiKey,
                input,
                payload,
                cancellationToken)
            : await CreateStreamingResponseAsync(
                request,
                observer,
                apiKey,
                input,
                payload,
                cancellationToken);
    }

    private async Task<AgentModelTurnResult> CreateStreamingResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        string apiKey,
        JsonArray input,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var parser = new OpenAiResponsesStreamParser();
        try
        {
            using HttpRequestMessage message = CreateAuthorizedRequest(
                HttpMethod.Post,
                _endpoint,
                apiKey,
                payload);
            using HttpResponseMessage response = await SendProviderRequestAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync(response, apiKey, cancellationToken);

            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                AgentModelTurnResult nonStreamingResult =
                    OpenAiResponsesStreamParser.ParseNonStreamingResponse(body, input.ToJsonString());
                return nonStreamingResult with
                {
                    ProviderAttempt = CreateAttemptDiagnostic(
                        request,
                        stopwatch,
                        outcome: "completed",
                        responseId: nonStreamingResult.ResponseId,
                        actualModel: nonStreamingResult.ActualModel,
                        providerEventCount: 1,
                        lastProviderEventType: "response.completed",
                        terminalStatus: "completed"),
                };
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line[5..].TrimStart();
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    break;
                if (!parser.ProcessData(data, out string delta))
                    continue;

                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.TextDelta,
                        Message = delta,
                    },
                    cancellationToken);
            }

            if (parser.IsTerminal && !parser.IsCompleted)
                throw parser.CreateTerminalException();
            if (!parser.IsCompleted)
            {
                throw new AgentModelException(
                    AgentFailureCategory.Transport,
                    "The Responses API stream ended before a completed response event.",
                    retryable: string.IsNullOrEmpty(parser.Text));
            }

            AgentModelTurnResult result = parser.BuildResult(input.ToJsonString());
            if (string.IsNullOrWhiteSpace(result.ActualModel))
            {
                throw new AgentModelException(
                    AgentFailureCategory.ProviderError,
                    "The Responses API stream did not report the actual model.");
            }

            return result with
            {
                ProviderAttempt = CreateAttemptDiagnostic(
                    request,
                    stopwatch,
                    outcome: "completed",
                    parser: parser),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentModelException exception) when (exception.ProviderAttempt is null)
        {
            throw exception.WithProviderAttempt(CreateAttemptDiagnostic(
                request,
                stopwatch,
                outcome: OutcomeFor(exception),
                parser: parser,
                failureCategory: exception.Category,
                providerStatus: exception.ProviderStatus,
                retryable: exception.Retryable));
        }
    }

    private async Task<AgentModelTurnResult> CreateBackgroundResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        string apiKey,
        JsonArray input,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string responseId = string.Empty;
        string actualModel = string.Empty;
        string status = string.Empty;
        string incompleteReason = string.Empty;
        string lastEventType = string.Empty;
        int providerResponseCount = 0;

        try
        {
            using (HttpRequestMessage createMessage = CreateAuthorizedRequest(
                HttpMethod.Post,
                _endpoint,
                apiKey,
                payload))
            using (HttpResponseMessage createResponse = await SendProviderRequestAsync(
                createMessage,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken))
            {
                if (!createResponse.IsSuccessStatusCode)
                    throw await CreateHttpExceptionAsync(createResponse, apiKey, cancellationToken);

                string body = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                (responseId, actualModel, status, incompleteReason) = ReadResponseState(body);
                providerResponseCount++;
                lastEventType = EventTypeForStatus(status);
                if (string.IsNullOrWhiteSpace(responseId))
                {
                    throw new AgentModelException(
                        AgentFailureCategory.ProviderError,
                        "The background Responses API request did not report a response ID.");
                }

                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Diagnostic,
                        Message = $"Background response entered {NormalizeStatus(status)} state.",
                        ProviderAttempt = CreateAttemptDiagnostic(
                            request,
                            stopwatch,
                            outcome: IsTerminalStatus(status) ? NormalizeStatus(status) : "in_progress",
                            responseId: responseId,
                            actualModel: actualModel,
                            providerEventCount: providerResponseCount,
                            lastProviderEventType: lastEventType,
                            terminalStatus: status,
                            incompleteReason: incompleteReason),
                    },
                    cancellationToken);

                if (IsTerminalStatus(status))
                    return BuildBackgroundTerminalResult(body);
            }

            int consecutivePollFailures = 0;
            while (true)
            {
                await observer.OnEventAsync(
                    new AgentRunEvent
                    {
                        Kind = AgentRunEventKind.Status,
                        Message = $"provider_background_{NormalizeStatus(status)}",
                    },
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

                string body;
                try
                {
                    using HttpRequestMessage pollMessage = CreateAuthorizedRequest(
                        HttpMethod.Get,
                        BuildResponseUri(responseId),
                        apiKey);
                    using HttpResponseMessage pollResponse = await SendProviderRequestAsync(
                        pollMessage,
                        HttpCompletionOption.ResponseContentRead,
                        cancellationToken);
                    if (!pollResponse.IsSuccessStatusCode)
                        throw await CreateHttpExceptionAsync(pollResponse, apiKey, cancellationToken);
                    body = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                    consecutivePollFailures = 0;
                }
                catch (AgentModelException exception) when (exception.Retryable)
                {
                    consecutivePollFailures++;
                    TimeSpan delay = exception.RetryAfter
                        ?? TimeSpan.FromMilliseconds(Math.Min(
                            8_000,
                            400 * Math.Pow(2, Math.Min(consecutivePollFailures - 1, 4))));
                    await observer.OnEventAsync(
                        new AgentRunEvent
                        {
                            Kind = AgentRunEventKind.Diagnostic,
                            Message = $"Background response polling failed transiently; retrying after {delay.TotalMilliseconds:0} ms.",
                        },
                        cancellationToken);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                (string polledId, string polledModel, status, incompleteReason) = ReadResponseState(body);
                providerResponseCount++;
                lastEventType = EventTypeForStatus(status);
                if (!string.IsNullOrWhiteSpace(polledId)
                    && !string.Equals(responseId, polledId, StringComparison.Ordinal))
                {
                    throw new AgentModelException(
                        AgentFailureCategory.ProviderError,
                        "The background Responses API poll returned a different response ID.");
                }
                if (!string.IsNullOrWhiteSpace(polledModel))
                    actualModel = polledModel;
                if (IsTerminalStatus(status))
                    return BuildBackgroundTerminalResult(body);
            }
        }
        catch (OperationCanceledException)
        {
            bool providerCancellationAccepted = false;
            if (!string.IsNullOrWhiteSpace(responseId))
                providerCancellationAccepted = await TryCancelBackgroundResponseAsync(responseId, apiKey);

            AgentProviderAttemptDiagnostic diagnostic = CreateAttemptDiagnostic(
                request,
                stopwatch,
                outcome: "cancelled",
                responseId: responseId,
                actualModel: actualModel,
                providerEventCount: providerResponseCount,
                lastProviderEventType: lastEventType,
                terminalStatus: "cancelled",
                incompleteReason: incompleteReason,
                failureCategory: AgentFailureCategory.Cancelled,
                providerCancellationAccepted: providerCancellationAccepted);
            await TryPublishCancellationDiagnosticAsync(
                observer,
                diagnostic);
            throw new AgentModelOperationCanceledException(
                diagnostic,
                new OperationCanceledException(cancellationToken),
                cancellationToken);
        }
        catch (AgentModelException exception) when (exception.ProviderAttempt is null)
        {
            throw exception.WithProviderAttempt(CreateAttemptDiagnostic(
                request,
                stopwatch,
                outcome: OutcomeFor(exception),
                responseId: responseId,
                actualModel: actualModel,
                providerEventCount: providerResponseCount,
                lastProviderEventType: lastEventType,
                terminalStatus: status,
                incompleteReason: incompleteReason,
                failureCategory: exception.Category,
                providerStatus: exception.ProviderStatus,
                retryable: exception.Retryable));
        }

        AgentModelTurnResult BuildBackgroundTerminalResult(string body)
        {
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var parser = new OpenAiResponsesStreamParser();
                string terminalEvent = EventTypeForStatus(status);
                parser.ProcessData(
                    new JsonObject
                    {
                        ["type"] = terminalEvent,
                        ["response"] = JsonNode.Parse(body),
                    }.ToJsonString(),
                    out _);
                throw parser.CreateTerminalException();
            }

            AgentModelTurnResult result =
                OpenAiResponsesStreamParser.ParseNonStreamingResponse(body, input.ToJsonString());
            if (string.IsNullOrWhiteSpace(result.ActualModel))
            {
                throw new AgentModelException(
                    AgentFailureCategory.ProviderError,
                    "The background Responses API result did not report the actual model.");
            }

            actualModel = result.ActualModel;
            return result with
            {
                ProviderAttempt = CreateAttemptDiagnostic(
                    request,
                    stopwatch,
                    outcome: "completed",
                    responseId: result.ResponseId,
                    actualModel: result.ActualModel,
                    providerEventCount: providerResponseCount,
                    lastProviderEventType: lastEventType,
                    terminalStatus: status,
                    incompleteReason: incompleteReason),
            };
        }
    }

    private static AgentProviderAttemptDiagnostic CreateAttemptDiagnostic(
        AgentModelTurnRequest request,
        Stopwatch stopwatch,
        string outcome,
        OpenAiResponsesStreamParser? parser = null,
        string responseId = "",
        string actualModel = "",
        int providerEventCount = 0,
        int malformedEventCount = 0,
        string lastProviderEventType = "",
        long? lastSequenceNumber = null,
        string terminalStatus = "",
        string incompleteReason = "",
        AgentFailureCategory? failureCategory = null,
        int? providerStatus = null,
        bool retryable = false,
        bool providerCancellationAccepted = false)
        => new()
        {
            TurnNumber = request.TurnIndex + 1,
            AttemptNumber = request.AttemptNumber,
            UsedBackgroundMode = request.Run.UseBackgroundMode,
            Outcome = outcome,
            ResponseId = parser?.ResponseId ?? responseId,
            ActualModel = parser?.ActualModel ?? actualModel,
            ProviderEventCount = parser?.ProviderEventCount ?? providerEventCount,
            MalformedEventCount = parser?.MalformedEventCount ?? malformedEventCount,
            LastProviderEventType = parser?.LastProviderEventType ?? lastProviderEventType,
            LastSequenceNumber = parser?.LastSequenceNumber ?? lastSequenceNumber,
            TerminalStatus = parser?.TerminalStatus ?? terminalStatus,
            IncompleteReason = parser?.IncompleteReason ?? incompleteReason,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            FailureCategory = failureCategory,
            ProviderStatus = providerStatus,
            Retryable = retryable,
            ProviderCancellationAccepted = providerCancellationAccepted,
        };

    private static string OutcomeFor(AgentModelException exception)
        => exception.Category switch
        {
            AgentFailureCategory.Transport => "transport_error",
            AgentFailureCategory.ProviderRateLimit => "rate_limited",
            AgentFailureCategory.Authentication => "authentication_error",
            AgentFailureCategory.BudgetExceeded => "incomplete",
            AgentFailureCategory.Cancelled => "cancelled",
            _ => "provider_error",
        };

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        Uri uri,
        string apiKey,
        JsonObject? payload = null)
    {
        var message = new HttpRequestMessage(method, uri);
        if (payload is not null)
            message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("XREngine-LocalAgentBroker/0.2");
        return message;
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(
        HttpRequestMessage message,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(message, completionOption, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new AgentModelException(
                AgentFailureCategory.Transport,
                "The Responses API request could not be sent.",
                retryable: true,
                diagnosticDetail: exception.Message,
                innerException: exception);
        }
    }

    private Uri BuildResponseUri(string responseId, string suffix = "")
    {
        string escapedResponseId = Uri.EscapeDataString(responseId);
        string baseUri = _endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri($"{baseUri}/{escapedResponseId}{suffix}", UriKind.Absolute);
    }

    private async Task<bool> TryCancelBackgroundResponseAsync(string responseId, string apiKey)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using HttpRequestMessage message = CreateAuthorizedRequest(
                HttpMethod.Post,
                BuildResponseUri(responseId, "/cancel"),
                apiKey,
                new JsonObject());
            using HttpResponseMessage response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Best-effort provider cancellation must not replace the original cancellation result.
            return false;
        }
    }

    private static async Task TryPublishCancellationDiagnosticAsync(
        IAgentRunObserver observer,
        AgentProviderAttemptDiagnostic diagnostic)
    {
        try
        {
            await observer.OnEventAsync(
                new AgentRunEvent
                {
                    Kind = AgentRunEventKind.Diagnostic,
                    Message = "Background response cancellation was requested.",
                    ProviderAttempt = diagnostic,
                },
                CancellationToken.None);
        }
        catch
        {
            // Cancellation diagnostics are secondary to completing cancellation promptly.
        }
    }

    private static (string Id, string Model, string Status, string IncompleteReason)
        ReadResponseState(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string id = TryGetString(root, "id") ?? string.Empty;
            string model = TryGetString(root, "model") ?? string.Empty;
            string status = NormalizeStatus(TryGetString(root, "status"));
            string incompleteReason = root.TryGetProperty("incomplete_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object
                    ? TryGetString(details, "reason") ?? string.Empty
                    : string.Empty;
            if (!IsPendingStatus(status) && !IsTerminalStatus(status))
            {
                throw new AgentModelException(
                    AgentFailureCategory.ProviderError,
                    $"The background Responses API returned unknown status '{status}'.");
            }
            return (id, model, status, incompleteReason);
        }
        catch (JsonException exception)
        {
            throw new AgentModelException(
                AgentFailureCategory.ProviderError,
                "The background Responses API returned invalid JSON.",
                diagnosticDetail: exception.Message,
                innerException: exception);
        }
    }

    private static bool IsPendingStatus(string status)
        => string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string EventTypeForStatus(string status)
        => "response." + NormalizeStatus(status);

    private static string NormalizeStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            ? "unknown"
            : status.Trim().ToLowerInvariant();

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static JsonArray BuildInput(AgentModelTurnRequest request)
    {
        JsonArray input;
        if (string.IsNullOrWhiteSpace(request.ContinuationJson))
        {
            JsonNode content = string.IsNullOrWhiteSpace(request.Run.InitialImageDataUri)
                ? JsonValue.Create(request.Prompt)!
                : new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = request.Prompt },
                    new JsonObject { ["type"] = "input_image", ["image_url"] = request.Run.InitialImageDataUri },
                };
            input =
            [
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            ];
        }
        else
        {
            input = JsonNode.Parse(request.ContinuationJson) as JsonArray
                ?? throw new AgentModelException(
                    AgentFailureCategory.Internal,
                    "Provider continuation state was not a JSON array.");
        }

        foreach (AgentModelToolOutput output in request.ToolOutputs)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = output.CallId,
                ["output"] = output.Content,
            });

            if (!string.IsNullOrWhiteSpace(output.ImageDataUri))
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = output.ImageDataUri,
                        },
                    },
                });
            }
        }

        return input;
    }

    private static JsonObject BuildPayload(AgentModelTurnRequest request, JsonArray input)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Run.RequestedModel,
            ["input"] = input.DeepClone(),
            ["stream"] = !request.Run.UseBackgroundMode,
            ["store"] = false,
            ["max_output_tokens"] = request.MaxOutputTokens > 0
                ? request.MaxOutputTokens
                : request.Run.Budget.MaxOutputTokens,
            ["parallel_tool_calls"] = request.Run.Budget.MaxConcurrency > 1
                && !request.Run.ToolPolicy.AllowMutation,
        };
        if (request.Run.UseBackgroundMode)
            payload["background"] = true;
        if (SupportsReasoning(request.Run.RequestedModel))
        {
            payload["reasoning"] = new JsonObject
            {
                ["effort"] = request.Run.ReasoningEffort.ToLowerInvariant(),
            };
        }
        if (!string.IsNullOrWhiteSpace(request.Run.SystemInstructions))
            payload["instructions"] = request.Run.SystemInstructions;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (AgentToolDefinition tool in request.Tools)
            {
                JsonNode parameters;
                try
                {
                    parameters = JsonNode.Parse(tool.InputSchemaJson)
                        ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }
                catch (JsonException)
                {
                    parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                });
            }

            payload["tools"] = tools;
            if (request.ForceTextResponse)
                payload["tool_choice"] = "none";
            else if (request.TurnIndex == 0 && request.Run.RequireToolUse)
                payload["tool_choice"] = "required";
        }

        if (request.Run.HostedTools.Count > 0 && !request.ForceTextResponse)
        {
            JsonArray tools = payload["tools"] as JsonArray ?? [];
            foreach (AgentHostedTool hostedTool in request.Run.HostedTools.Distinct())
            {
                tools.Add(new JsonObject
                {
                    ["type"] = hostedTool switch
                    {
                        AgentHostedTool.WebSearch => "web_search",
                        AgentHostedTool.ImageGeneration => "image_generation",
                        _ => throw new ArgumentOutOfRangeException(nameof(hostedTool)),
                    },
                });
            }
            payload["tools"] = tools;
        }

        return payload;
    }

    private static bool SupportsReasoning(string model)
        => model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    private static async Task<AgentModelException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        string apiKey,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = RedactDiagnostic(
            ExtractSafeErrorMessage(body)
                ?? $"The Responses API returned HTTP {(int)response.StatusCode}.",
            apiKey);
        int status = (int)response.StatusCode;
        bool retryable = response.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
        AgentFailureCategory category = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AgentFailureCategory.Authentication,
            HttpStatusCode.TooManyRequests => AgentFailureCategory.ProviderRateLimit,
            HttpStatusCode.NotFound => AgentFailureCategory.ModelUnavailable,
            _ => AgentFailureCategory.ProviderError,
        };

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        return new AgentModelException(
            category,
            message,
            retryable,
            status,
            retryAfter,
            RedactDiagnostic(body, apiKey));
    }

    private static string? ExtractSafeErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string RedactDiagnostic(string value, string? exactSecret = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string bounded = value.Length <= 4_096 ? value : value[..4_096] + "…";
        if (!string.IsNullOrEmpty(exactSecret))
            bounded = bounded.Replace(exactSecret, "[REDACTED]", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(
            bounded,
            "(?i)(authorization|api[_-]?key|token|secret)(\\s*[=:]\\s*)[^\\s,\\\"}]+",
            "$1$2[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
