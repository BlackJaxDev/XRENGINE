using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// Hosts an <see cref="HttpListener"/> to receive webhook callbacks and surface them to XR systems.
    /// </summary>
    public class WebhookListenerComponent : XRComponent
    {
        private readonly ConcurrentQueue<WebhookEvent> _pendingEvents = new();
        private readonly ConcurrentDictionary<string, string> _responseHeaders = new(StringComparer.OrdinalIgnoreCase);
        private HttpListener? _listener;
        private CancellationTokenSource? _listenerCts;
        private Task? _listenTask;
        private string _prefix = "http://localhost:5055/webhooks/";
        private bool _autoStart = true;
        private int _defaultStatusCode = (int)HttpStatusCode.OK;
        private string _defaultResponseBody = "ok";
        private int _maxBodySizeBytes = 256 * 1024;

        /// <summary>
        /// Prefix consumed by <see cref="HttpListener"/> (must end with a slash and include the trailing path).
        /// </summary>
        public string Prefix
        {
            get => _prefix;
            set => SetField(ref _prefix, value);
        }

        /// <summary>
        /// Automatically starts listening when the component is activated.
        /// </summary>
        public bool AutoStart
        {
            get => _autoStart;
            set => SetField(ref _autoStart, value);
        }

        /// <summary>
        /// HTTP status code returned to callers when no custom handler overrides the response.
        /// </summary>
        public int DefaultStatusCode
        {
            get => _defaultStatusCode;
            set => SetField(ref _defaultStatusCode, value);
        }

        /// <summary>
        /// Optional textual payload returned to webhook callers.
        /// </summary>
        public string DefaultResponseBody
        {
            get => _defaultResponseBody;
            set => SetField(ref _defaultResponseBody, value ?? string.Empty);
        }

        /// <summary>
        /// Maximum accepted payload size in bytes.
        /// </summary>
        public int MaxBodySizeBytes
        {
            get => _maxBodySizeBytes;
            set => SetField(ref _maxBodySizeBytes, Math.Max(1024, value));
        }

        /// <summary>
        /// Optional custom response headers added to every acknowledgement.
        /// </summary>
        public IReadOnlyDictionary<string, string> DefaultResponseHeaders => _responseHeaders;

        /// <summary>
        /// Pending webhook count stored inside the component queue.
        /// </summary>
        public int PendingCount => _pendingEvents.Count;

        /// <summary>
        /// Raised whenever a new webhook payload is received.
        /// </summary>
        public event Action<WebhookListenerComponent, WebhookEvent>? WebhookReceived;

        /// <summary>
        /// Adds or replaces a default response header.
        /// </summary>
        public void SetResponseHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be blank.", nameof(name));

            _responseHeaders[name] = value;
        }

        /// <summary>
        /// Removes a response header if present.
        /// </summary>
        public bool RemoveResponseHeader(string name)
            => _responseHeaders.TryRemove(name, out _);

        /// <summary>
        /// Attempts to dequeue the next pending webhook event.
        /// </summary>
        public bool TryDequeue(out WebhookEvent webhook)
            => _pendingEvents.TryDequeue(out webhook);

        /// <summary>
        /// Starts the HTTP listener manually.
        /// </summary>
        public void StartListening()
        {
            if (_listener?.IsListening == true)
                return;

            _listener = new HttpListener();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(_prefix);
            _listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
            _listener.Start();

            _listenerCts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_listener, _listenerCts.Token));
        }

        /// <summary>
        /// Stops the HTTP listener and clears pending events.
        /// </summary>
        public void StopListening()
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;

            if (_listener is not null)
            {
                try { _listener.Stop(); }
                catch { }
                _listener.Close();
                _listener = null;
            }

            _listenTask = null;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (AutoStart)
                StartListening();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopListening();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            StopListening();
            _pendingEvents.Clear();
        }

        private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 64)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DispatchListenerError(ex);
                    continue;
                }

                _ = Task.Run(() => HandleContextAsync(context!, token));
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8, leaveOpen: false);
                string body = await ReadLimitedAsync(reader, _maxBodySizeBytes).ConfigureAwait(false);

                var webhookEvent = new WebhookEvent(
                    context.Request.HttpMethod,
                    context.Request.Url!,
                    ExtractHeaders(context.Request),
                    body,
                    context.Request.RemoteEndPoint?.ToString() ?? "unknown",
                    DateTimeOffset.UtcNow);

                _pendingEvents.Enqueue(webhookEvent);
                DispatchWebhook(webhookEvent);

                await WriteDefaultResponseAsync(context.Response).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DispatchListenerError(ex);
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    byte[] payload = Encoding.UTF8.GetBytes("error");
                    await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore secondary failures while best-effort responding.
                }
            }
            finally
            {
                try { context.Response.OutputStream.Close(); }
                catch { }
            }
        }

        private async Task WriteDefaultResponseAsync(HttpListenerResponse response)
        {
            response.StatusCode = _defaultStatusCode;
            foreach (var header in _responseHeaders)
                response.Headers[header.Key] = header.Value;

            byte[] payload = Encoding.UTF8.GetBytes(_defaultResponseBody);
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
        }

        private static Dictionary<string, string> ExtractHeaders(HttpListenerRequest request)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in request.Headers)
            {
                headers[key] = request.Headers[key]!;
            }
            return headers;
        }

        private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxBytes)
        {
            char[] buffer = new char[Math.Min(4096, maxBytes)];
            int totalRead = 0;
            using var writer = new StringWriter();
            while (true)
            {
                int toRead = Math.Min(buffer.Length, maxBytes - totalRead);
                if (toRead <= 0)
                    break;

                int read = await reader.ReadAsync(buffer, 0, toRead).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalRead += read;
                writer.Write(buffer, 0, read);
            }

            return writer.ToString();
        }

        private void DispatchWebhook(WebhookEvent webhook)
            => RunOnMainThread(() => WebhookReceived?.Invoke(this, webhook), "WebhookListenerComponent.WebhookReceived");

        private void DispatchListenerError(Exception ex)
            => RunOnMainThread(() => Debug.NetworkingWarning($"[Webhook] Listener error: {ex.Message}"), "WebhookListenerComponent.ListenerError");

        private static void RunOnMainThread(Action action, string reason)
        {
            if (!Engine.InvokeOnMainThread(action, reason))
                action();
        }
    }

    /// <summary>
    /// Represents a received webhook callback.
    /// </summary>
    public readonly record struct WebhookEvent(
        string Method,
        Uri Url,
        IReadOnlyDictionary<string, string> Headers,
        string Body,
        string RemoteEndpoint,
        DateTimeOffset ReceivedAt)
    {
        /// <summary>
        /// Attempts to deserialize the body as JSON using the supplied options or defaults.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Webhook payloads are user-defined and require runtime JSON deserialization.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Webhook payloads are user-defined and require runtime JSON deserialization.")]
        public T? DeserializeJson<T>(JsonSerializerOptions? options = null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(Body, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }
}
