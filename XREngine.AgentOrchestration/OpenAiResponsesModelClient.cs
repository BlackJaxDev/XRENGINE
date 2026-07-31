using System.Net;
using System.Net.Http.Headers;
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
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Headers.UserAgent.ParseAdd("XREngine-LocalAgentBroker/0.1");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
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

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync(response, apiKey, cancellationToken);

            string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                return OpenAiResponsesStreamParser.ParseNonStreamingResponse(body, input.ToJsonString());
            }

            var parser = new OpenAiResponsesStreamParser();
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

            return result;
        }
    }

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
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = request.MaxOutputTokens > 0
                ? request.MaxOutputTokens
                : request.Run.Budget.MaxOutputTokens,
            ["parallel_tool_calls"] = request.Run.Budget.MaxConcurrency > 1
                && !request.Run.ToolPolicy.AllowMutation,
        };
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
