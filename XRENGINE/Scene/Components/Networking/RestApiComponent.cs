using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// Component that exposes a reusable <see cref="HttpClient"/> for REST style integrations and keeps
    /// the resulting data accessible to other XR systems through events and a lightweight data store.
    /// </summary>
    public class RestApiComponent : XRComponent
    {
        private readonly ConcurrentDictionary<string, string> _defaultHeaders = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, JsonNode?> _dataStore = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _httpClientLock = new();
        private HttpClient? _client;
        private SocketsHttpHandler? _httpHandler;
        private CancellationTokenSource _requestScopeCts = new();
        private string _baseUrl = string.Empty;
        private TimeSpan _timeout = TimeSpan.FromSeconds(30);
        private bool _logRequests;

        /// <summary>
        /// Default serializer configuration used for outgoing JSON payloads and incoming responses unless overridden per request.
        /// </summary>
        public JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Returns the mutable set of default headers applied to every request when <see cref="RestApiRequest.UseDefaultHeaders"/> is true.
        /// </summary>
        public IReadOnlyDictionary<string, string> DefaultHeaders => _defaultHeaders;

        /// <summary>
        /// Enumerates the keys currently cached inside the component's JSON data store.
        /// </summary>
        public IEnumerable<string> DataKeys => _dataStore.Keys;

        /// <summary>
        /// Gets the most recent response produced by <see cref="SendAsync(RestApiRequest, CancellationToken)"/>.
        /// </summary>
        public RestApiResponse? LastResponse { get; private set; }

        /// <summary>
        /// Enables verbose request/response logging through <see cref="Debug.Out(EOutputVerbosity, string, object[])"/>.
        /// </summary>
        public bool LogRequests
        {
            get => _logRequests;
            set => SetField(ref _logRequests, value);
        }

        /// <summary>
        /// Base URI applied to relative resource paths when building requests.
        /// </summary>
        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetField(ref _baseUrl, value))
                    ApplyBaseUrl();
            }
        }

        /// <summary>
        /// Global timeout applied to the underlying <see cref="HttpClient"/>.
        /// </summary>
        public TimeSpan RequestTimeout
        {
            get => _timeout;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be greater than zero.");

                if (SetField(ref _timeout, value))
                    ApplyTimeout();
            }
        }

        /// <summary>
        /// Raised just before a request is dispatched.
        /// </summary>
        public event Action<RestApiComponent, RestApiRequest>? RequestStarted;

        /// <summary>
        /// Raised after the HTTP response has been received and cached.
        /// </summary>
        public event Action<RestApiComponent, RestApiResponse>? ResponseReceived;

        /// <summary>
        /// Raised when sending or deserializing a request results in an exception.
        /// </summary>
        public event Action<RestApiComponent, RestApiRequest, Exception>? RequestFailed;

        /// <summary>
        /// Raised whenever <see cref="RestApiRequest.ResponseDataKey"/> is populated and the response JSON is successfully parsed.
        /// </summary>
        public event Action<RestApiComponent, string, JsonNode?>? DataUpdated;

        /// <summary>
        /// Adds or replaces a header that will be applied to subsequent requests when default headers are enabled.
        /// </summary>
        public void SetDefaultHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be empty.", nameof(name));

            _defaultHeaders[name] = value;
        }

        /// <summary>
        /// Removes a default header if present.
        /// </summary>
        public bool RemoveDefaultHeader(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return _defaultHeaders.TryRemove(name, out _);
        }

        /// <summary>
        /// Removes every default header currently stored by the component.
        /// </summary>
        public void ClearDefaultHeaders()
            => _defaultHeaders.Clear();

        /// <summary>
        /// Retrieves a parsed response payload previously stored for <paramref name="key"/>.
        /// </summary>
        public bool TryGetData(string key, [NotNullWhen(true)] out JsonNode? value)
            => _dataStore.TryGetValue(key, out value);

        /// <summary>
        /// Convenience wrapper that returns cached JSON data or null if the key is missing.
        /// </summary>
        public JsonNode? GetDataOrDefault(string key)
        {
            _dataStore.TryGetValue(key, out JsonNode? value);
            return value;
        }

        /// <summary>
        /// Deletes a cached response associated with <paramref name="key"/>.
        /// </summary>
        public bool RemoveData(string key)
            => _dataStore.TryRemove(key, out _);

        /// <summary>
        /// Clears every entry from the component's JSON cache.
        /// </summary>
        public void ClearData()
            => _dataStore.Clear();

        /// <summary>
        /// Performs a GET request against <paramref name="resource"/> using optional query parameters.
        /// </summary>
        public Task<RestApiResponse> GetAsync(string resource, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
            => SendAsync(new RestApiRequest
            {
                Method = HttpMethod.Get,
                Resource = resource,
                Query = query
            }, cancellationToken);

        /// <summary>
        /// Performs a GET request and deserializes the body into <typeparamref name="TResponse"/>.
        /// </summary>
        public Task<RestApiResponse<TResponse>> GetJsonAsync<TResponse>(string resource, IReadOnlyDictionary<string, string>? query = null, CancellationToken cancellationToken = default)
            => SendAsync<TResponse>(new RestApiRequest
            {
                Method = HttpMethod.Get,
                Resource = resource,
                Query = query
            }, cancellationToken);

        /// <summary>
        /// Performs a POST request whose body is the serialized <paramref name="payload"/>.
        /// </summary>
        public Task<RestApiResponse> PostJsonAsync<TRequest>(string resource, TRequest payload, CancellationToken cancellationToken = default)
            => SendAsync(new RestApiRequest
            {
                Method = HttpMethod.Post,
                Resource = resource,
                JsonPayload = payload
            }, cancellationToken);

        /// <summary>
        /// Performs a POST request with a serialized payload and parses the response into <typeparamref name="TResponse"/>.
        /// </summary>
        public Task<RestApiResponse<TResponse>> PostJsonAsync<TRequest, TResponse>(string resource, TRequest payload, CancellationToken cancellationToken = default)
            => SendAsync<TResponse>(new RestApiRequest
            {
                Method = HttpMethod.Post,
                Resource = resource,
                JsonPayload = payload
            }, cancellationToken);

        /// <summary>
        /// Executes <paramref name="request"/> and returns the raw REST response, optionally throwing for non-success status codes.
        /// </summary>
        public async Task<RestApiResponse> SendAsync(RestApiRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            EnsureClient();
            if (_client is null)
                throw new InvalidOperationException("REST client has not been initialized.");

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_requestScopeCts.Token, cancellationToken);
            if (request.TimeoutOverride is { } overrideTimeout && overrideTimeout > TimeSpan.Zero)
                linkedCts.CancelAfter(overrideTimeout);

            using HttpRequestMessage message = BuildRequestMessage(request, out Uri targetUri);
            DispatchRequestStarted(request);
            LogRequest(request, targetUri);

            try
            {
                using HttpResponseMessage response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                byte[] body = response.Content is null
                    ? Array.Empty<byte>()
                    : await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);

                var restResponse = new RestApiResponse(
                    request,
                    response.StatusCode,
                    response.ReasonPhrase,
                    ConvertHeaders(response),
                    body,
                    response.IsSuccessStatusCode,
                    DateTimeOffset.UtcNow);

                LastResponse = restResponse;
                TryStoreResponseData(request, restResponse);
                DispatchResponseArrived(restResponse);
                LogResponse(restResponse);

                if (!response.IsSuccessStatusCode && request.ThrowOnError)
                    throw new RestApiRequestException(request, restResponse);

                return restResponse;
            }
            catch (RestApiRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DispatchRequestFailed(request, ex);
                throw;
            }
        }

        /// <summary>
        /// Executes <paramref name="request"/> and deserializes the JSON body into <typeparamref name="TResponse"/>.
        /// </summary>
        public async Task<RestApiResponse<TResponse>> SendAsync<TResponse>(RestApiRequest request, CancellationToken cancellationToken = default)
        {
            RestApiResponse response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Body.IsEmpty)
                return new RestApiResponse<TResponse>(response, default);

            try
            {
                JsonSerializerOptions options = request.SerializerOptionsOverride ?? SerializerOptions;
                string json = response.BodyText;
                TResponse? payload = DeserializePayload<TResponse>(json, options);
                return new RestApiResponse<TResponse>(response, payload);
            }
            catch (JsonException ex)
            {
                var deserializationException = new RestApiDeserializationException(typeof(TResponse), request, response, ex);
                DispatchRequestFailed(request, deserializationException);
                throw deserializationException;
            }
        }

        /// <summary>
        /// Cancels every in-flight request issued by this component.
        /// </summary>
        public void CancelAllRequests()
        {
            var previous = Interlocked.Exchange(ref _requestScopeCts, new CancellationTokenSource());
            previous.Cancel();
            previous.Dispose();
        }

        /// <inheritdoc />
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureClient();
        }

        /// <inheritdoc />
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            CancelAllRequests();
        }

        /// <inheritdoc />
        protected override void OnDestroying()
        {
            base.OnDestroying();
            CancelAllRequests();
            _client?.Dispose();
            _httpHandler?.Dispose();
        }

        /// <summary>
        /// Lazily creates the shared <see cref="HttpClient"/> and handler.
        /// </summary>
        private void EnsureClient()
        {
            if (_client is not null)
                return;

            lock (_httpClientLock)
            {
                if (_client is not null)
                    return;

                _httpHandler ??= new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 32
                };

                _client = new HttpClient(_httpHandler, false)
                {
                    Timeout = _timeout
                };

                ApplyBaseUrl();
            }
        }

        /// <summary>
        /// Applies the configured <see cref="BaseUrl"/> to the underlying client, logging errors for malformed URIs.
        /// </summary>
        private void ApplyBaseUrl()
        {
            if (_client is null)
                return;

            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                _client.BaseAddress = null;
                return;
            }

            if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out Uri? uri))
            {
                _client.BaseAddress = uri;
            }
            else
            {
                Debug.LogWarning($"[RestApi] Invalid base URL '{_baseUrl}'.");
            }
        }

        /// <summary>
        /// Applies the configured <see cref="RequestTimeout"/> to the underlying client.
        /// </summary>
        private void ApplyTimeout()
        {
            if (_client is null)
                return;

            _client.Timeout = _timeout;
        }

        /// <summary>
        /// Builds an <see cref="HttpRequestMessage"/> from the supplied request data and returns the resolved URI.
        /// </summary>
        private HttpRequestMessage BuildRequestMessage(RestApiRequest request, out Uri targetUri)
        {
            targetUri = BuildRequestUri(request);
            var message = new HttpRequestMessage(request.Method, targetUri);
            HttpContent? content = BuildContent(request);
            if (content is not null)
                message.Content = content;

            if (request.UseDefaultHeaders)
                ApplyHeaders(message, _defaultHeaders);

            if (request.Headers is not null)
                ApplyHeaders(message, request.Headers);

            return message;
        }

        /// <summary>
        /// Generates the appropriate <see cref="HttpContent"/> for the request payload.
        /// </summary>
        private HttpContent? BuildContent(RestApiRequest request)
        {
            if (request.FormFields is { Count: > 0 })
                return new FormUrlEncodedContent(request.FormFields);

            if (request.BinaryPayload is { } binary)
            {
                var content = new ByteArrayContent(binary.ToArray());
                if (!string.IsNullOrWhiteSpace(request.ContentType))
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                return content;
            }

            if (!string.IsNullOrEmpty(request.RawPayload))
                return new StringContent(request.RawPayload, Encoding.UTF8, request.ContentType);

            if (request.JsonPayload is not null)
            {
                JsonSerializerOptions options = request.SerializerOptionsOverride ?? SerializerOptions;
                string json = request.JsonPayload is string str
                    ? str
                    : SerializePayload(request.JsonPayload, options);
                return new StringContent(json, Encoding.UTF8, request.ContentType);
            }

            return null;
        }

        /// <summary>
        /// Resolves the absolute URI for a request, combining the base address when necessary.
        /// </summary>
        private Uri BuildRequestUri(RestApiRequest request)
        {
            string resource = request.Resource ?? string.Empty;
            string target = AppendQueryString(resource, request.Query);

            if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute))
                return absolute;

            if (_client?.BaseAddress is null)
                throw new InvalidOperationException("BaseUrl must be set to send relative REST requests.");

            return new Uri(_client.BaseAddress, target);
        }

        /// <summary>
        /// Appends query string parameters to a resource path, respecting existing query values.
        /// </summary>
        private static string AppendQueryString(string resource, IReadOnlyDictionary<string, string>? query)
        {
            if (query is null || query.Count == 0)
                return resource ?? string.Empty;

            string normalized = resource ?? string.Empty;
            var builder = new StringBuilder(normalized);
            bool hasExistingQuery = normalized.IndexOf('?') >= 0;
            builder.Append(hasExistingQuery ? '&' : '?');

            bool first = true;
            foreach (KeyValuePair<string, string> pair in query)
            {
                if (!first)
                    builder.Append('&');

                builder
                    .Append(Uri.EscapeDataString(pair.Key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(pair.Value ?? string.Empty));

                first = false;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Applies header dictionaries to a request, falling back to content headers when necessary.
        /// </summary>
        private static void ApplyHeaders(HttpRequestMessage message, IReadOnlyDictionary<string, string> headers)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value) && message.Content is not null)
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        /// <summary>
        /// Captures response headers (including content ones) into a case-insensitive dictionary.
        /// </summary>
        private static IReadOnlyDictionary<string, string[]> ConvertHeaders(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in response.Headers)
                headers[header.Key] = header.Value.ToArray();

            if (response.Content is not null)
            {
                foreach (var header in response.Content.Headers)
                    headers[header.Key] = header.Value.ToArray();
            }

            return headers;
        }

        /// <summary>
        /// Parses JSON bodies and stores them in the component-level cache when instructed by the request.
        /// </summary>
        private void TryStoreResponseData(RestApiRequest request, RestApiResponse response)
        {
            if (string.IsNullOrWhiteSpace(request.ResponseDataKey) || response.Body.IsEmpty)
                return;

            try
            {
                JsonNode? node = JsonNode.Parse(response.Body.Span);
                _dataStore[request.ResponseDataKey] = node;
                DispatchDataUpdated(request.ResponseDataKey, node);
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"[RestApi] Failed to parse JSON for '{request.ResponseDataKey}': {ex.Message}");
            }
        }

        /// <summary>
        /// Notifies listeners that a request is about to be dispatched.
        /// </summary>
        private void DispatchRequestStarted(RestApiRequest request)
        {
            var handler = RequestStarted;
            if (handler is null)
                return;

            RunOnMainThread(() => handler(this, request), "RestApiComponent.RequestStarted");
        }

        /// <summary>
        /// Notifies listeners that a response has arrived.
        /// </summary>
        private void DispatchResponseArrived(RestApiResponse response)
        {
            var handler = ResponseReceived;
            if (handler is null)
                return;

            RunOnMainThread(() => handler(this, response), "RestApiComponent.RequestCompleted");
        }

        /// <summary>
        /// Notifies listeners that a request has failed.
        /// </summary>
        private void DispatchRequestFailed(RestApiRequest request, Exception ex)
        {
            var handler = RequestFailed;
            if (handler is null)
                return;

            RunOnMainThread(() => handler(this, request, ex), "RestApiComponent.RequestFailed");
        }

        /// <summary>
        /// Notifies listeners that cached data for <paramref name="key"/> has changed.
        /// </summary>
        private void DispatchDataUpdated(string key, JsonNode? payload)
        {
            var handler = DataUpdated;
            if (handler is null)
                return;

            RunOnMainThread(() => handler(this, key, payload), "RestApiComponent.PayloadReceived");
        }

        /// <summary>
        /// Writes the outgoing request details to the debug output if logging is enabled.
        /// </summary>
        private void LogRequest(RestApiRequest request, Uri target)
        {
            if (!LogRequests)
                return;

            Debug.Out(EOutputVerbosity.Verbose, "[RestApi] {0} {1}", request.Method.Method, target);
        }

        /// <summary>
        /// Writes the incoming response summary to the debug output if logging is enabled.
        /// </summary>
        private void LogResponse(RestApiResponse response)
        {
            if (!LogRequests)
                return;

            Debug.Out(EOutputVerbosity.Verbose, "[RestApi] {0} ({1}) for {2}", (int)response.StatusCode, response.StatusCode, response.Request.Resource);
        }

        /// <summary>
        /// Deserializes JSON text into <typeparamref name="TPayload"/> while suppressing trim/AOT warnings.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The component intentionally defers generic response materialization to user-defined types.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JsonSerializer is used deliberately to deserialize caller-provided payload models.")]
        private static TPayload? DeserializePayload<TPayload>(string json, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<TPayload>(json, options);

        /// <summary>
        /// Serializes an arbitrary payload into JSON text while suppressing trim/AOT warnings.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The component intentionally serializes arbitrary payloads supplied by user code.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JsonSerializer is used deliberately to serialize caller-provided payload models.")]
        private static string SerializePayload(object payload, JsonSerializerOptions options)
            => JsonSerializer.Serialize(payload, payload.GetType(), options);

        /// <summary>
        /// Ensures callbacks fire on the engine's main thread immediately if already there.
        /// </summary>
        private static void RunOnMainThread(Action action, string reason)
        {
            Engine.InvokeOnMainThread(action, reason, true);
        }
    }

    /// <summary>
    /// Immutable description of a REST call issued by <see cref="RestApiComponent"/>.
    /// </summary>
    public sealed record class RestApiRequest
    {
        /// <summary>
        /// HTTP verb to execute. Defaults to <see cref="HttpMethod.Get"/>.
        /// </summary>
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        /// <summary>
        /// Relative or absolute resource path.
        /// </summary>
        public string Resource { get; init; } = string.Empty;
        /// <summary>
        /// Key/value pairs appended to the query string.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Query { get; init; }
            = null;
        /// <summary>
        /// Additional request-specific headers.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Headers { get; init; }
            = null;
        /// <summary>
        /// Optional form fields encoded as <c>application/x-www-form-urlencoded</c>.
        /// </summary>
        public IReadOnlyDictionary<string, string>? FormFields { get; init; }
            = null;
        /// <summary>
        /// Arbitrary CLR object serialized as JSON.
        /// </summary>
        public object? JsonPayload { get; init; }
            = null;
        /// <summary>
        /// Literal string payload for text-based requests.
        /// </summary>
        public string? RawPayload { get; init; }
            = null;
        /// <summary>
        /// Raw binary payload sent as-is.
        /// </summary>
        public ReadOnlyMemory<byte>? BinaryPayload { get; init; }
            = null;
        /// <summary>
        /// MIME type applied to the outgoing content.
        /// </summary>
        public string ContentType { get; init; } = "application/json";
        /// <summary>
        /// Cache key used when storing parsed JSON responses.
        /// </summary>
        public string? ResponseDataKey { get; init; }
            = null;
        /// <summary>
        /// When true, non-success HTTP codes trigger a <see cref="RestApiRequestException"/>.
        /// </summary>
        public bool ThrowOnError { get; init; }
            = false;
        /// <summary>
        /// Optional per-request timeout that overrides <see cref="RestApiComponent.RequestTimeout"/>.
        /// </summary>
        public TimeSpan? TimeoutOverride { get; init; }
            = null;
        /// <summary>
        /// Enables automatic inclusion of the component-level default headers.
        /// </summary>
        public bool UseDefaultHeaders { get; init; } = true;
        /// <summary>
        /// Overrides the serializer options used for this request.
        /// </summary>
        public JsonSerializerOptions? SerializerOptionsOverride { get; init; }
            = null;
    }

    /// <summary>
    /// Represents the raw results of an HTTP request issued by <see cref="RestApiComponent"/>.
    /// </summary>
    public class RestApiResponse
    {
        private string? _cachedBodyText;

        internal RestApiResponse(
            RestApiRequest request,
            HttpStatusCode statusCode,
            string? reasonPhrase,
            IReadOnlyDictionary<string, string[]> headers,
            byte[] body,
            bool isSuccess,
            DateTimeOffset receivedAt)
        {
            Request = request;
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            Headers = headers;
            Body = new ReadOnlyMemory<byte>(body);
            IsSuccessStatusCode = isSuccess;
            ReceivedAt = receivedAt;
        }

        protected RestApiResponse(RestApiResponse other)
        {
            Request = other.Request;
            StatusCode = other.StatusCode;
            ReasonPhrase = other.ReasonPhrase;
            Headers = other.Headers;
            Body = other.Body;
            IsSuccessStatusCode = other.IsSuccessStatusCode;
            ReceivedAt = other.ReceivedAt;
        }

        /// <summary>
        /// Original request definition.
        /// </summary>
        public RestApiRequest Request { get; }
        /// <summary>
        /// HTTP status code returned by the server.
        /// </summary>
        public HttpStatusCode StatusCode { get; }
        /// <summary>
        /// Optional human-readable reason phrase supplied by the server.
        /// </summary>
        public string? ReasonPhrase { get; }
        /// <summary>
        /// Indicates whether the status code is in the success range.
        /// </summary>
        public bool IsSuccessStatusCode { get; }
        /// <summary>
        /// Aggregated response and content headers.
        /// </summary>
        public IReadOnlyDictionary<string, string[]> Headers { get; }
        /// <summary>
        /// Raw response body.
        /// </summary>
        public ReadOnlyMemory<byte> Body { get; }
        /// <summary>
        /// True when the body contains zero bytes.
        /// </summary>
        public bool BodyIsEmpty => Body.IsEmpty;
        /// <summary>
        /// Timestamp (UTC) when the response was received.
        /// </summary>
        public DateTimeOffset ReceivedAt { get; }

        /// <summary>
        /// Lazy UTF-8 view of <see cref="Body"/>.
        /// </summary>
        public string BodyText
        {
            get
            {
                _cachedBodyText ??= Encoding.UTF8.GetString(Body.Span);
                return _cachedBodyText;
            }
        }

        /// <summary>
        /// Provides a read-only stream backed by the response bytes.
        /// </summary>
        public Stream GetBodyStream()
            => new MemoryStream(Body.ToArray(), false);

        /// <summary>
        /// Creates a shallow copy of the response.
        /// </summary>
        public virtual RestApiResponse Clone()
            => new(this);
    }

    /// <summary>
    /// Response wrapper that includes a strongly typed payload.
    /// </summary>
    public sealed class RestApiResponse<TPayload> : RestApiResponse
    {
        internal RestApiResponse(RestApiResponse source, TPayload? payload)
            : base(source)
        {
            Payload = payload;
        }

        /// <summary>
        /// Deserialized response payload.
        /// </summary>
        public TPayload? Payload { get; }
    }

    /// <summary>
    /// Exception thrown when <see cref="RestApiRequest.ThrowOnError"/> is enabled and the server returns a non-success status code.
    /// </summary>
    public sealed class RestApiRequestException(RestApiRequest request, RestApiResponse response) : Exception($"Request to '{request.Resource}' returned HTTP {(int)response.StatusCode} ({response.StatusCode}).")
    {
        /// <summary>
        /// Request that triggered the exception.
        /// </summary>
        public RestApiRequest Request { get; } = request;
        /// <summary>
        /// Response returned by the server.
        /// </summary>
        public RestApiResponse Response { get; } = response;
    }

    /// <summary>
    /// Exception thrown when JSON payloads cannot be parsed into the requested model type.
    /// </summary>
    public sealed class RestApiDeserializationException : Exception
    {
        internal RestApiDeserializationException(Type targetType, RestApiRequest request, RestApiResponse response, Exception inner)
            : base($"Failed to deserialize response from '{request.Resource}' to type '{targetType.Name}'.", inner)
        {
            TargetType = targetType;
            Request = request;
            Response = response;
        }

        /// <summary>
        /// CLR type that failed to deserialize.
        /// </summary>
        public Type TargetType { get; }
        /// <summary>
        /// Request that produced the response.
        /// </summary>
        public RestApiRequest Request { get; }
        /// <summary>
        /// Response containing the problematic payload.
        /// </summary>
        public RestApiResponse Response { get; }
    }
}
