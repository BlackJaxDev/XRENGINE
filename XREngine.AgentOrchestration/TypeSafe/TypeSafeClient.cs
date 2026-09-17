using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.AgentOrchestration.TypeSafe;

/// <summary>
/// Production client for TypeSafe's System One (Jev) API.
/// Designed for optional use: if no key is configured or access is restricted, calls return null
/// safely so calling code can proceed with deterministic fallbacks without interruption.
/// </summary>
public sealed class TypeSafeClient : ITypeSafeClient
{
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";
    public const string DefaultApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly Action<string>? _logWarning;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TypeSafeClient(
        HttpClient? httpClient = null,
        Func<string?>? apiKeyProvider = null,
        string endpoint = DefaultEndpoint,
        string model = DefaultModel,
        Action<string>? logWarning = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _apiKeyProvider = apiKeyProvider ?? GetDefaultApiKey;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _logWarning = logWarning;
    }

    private static string? GetDefaultApiKey()
        => Environment.GetEnvironmentVariable(DefaultApiKeyEnvironmentVariable);

    public bool IsAvailable => !string.IsNullOrWhiteSpace(GetApiKey());

    public string TargetModel => _model;

    private string? GetApiKey() => _apiKeyProvider();

    public async Task<TypeSafeResponse?> EvaluateAsync(
        object state,
        IReadOnlyDictionary<string, TypeSafeQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        string? apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        if (questions == null || questions.Count == 0)
            return null;

        var requestPayload = new TypeSafeRequest
        {
            State = state,
            Model = _model,
            Questions = questions,
        };

        string jsonBody = JsonSerializer.Serialize(requestPayload, s_jsonOptions);

        const int maxRetries = 2;
        int delayMs = 150;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<TypeSafeResponse>(responseBody, s_jsonOptions);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logWarning?.Invoke("[TypeSafe] 401 Unauthorized: Invalid API key or account is on the waitlist. Operating in fallback mode.");
                    return null;
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)529)
                {
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        delayMs *= 2;
                        continue;
                    }
                }

                _logWarning?.Invoke($"[TypeSafe] Evaluation request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logWarning?.Invoke($"[TypeSafe] Communication error during evaluation: {ex.Message}");
                return null;
            }
        }

        return null;
    }
}
