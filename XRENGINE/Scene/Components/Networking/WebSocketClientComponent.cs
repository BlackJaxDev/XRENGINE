using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// XR component that manages a persistent WebSocket client connection with optional auto-reconnect and thread-safe send helpers.
    /// </summary>
    public class WebSocketClientComponent : XRComponent
    {
        private readonly ConcurrentDictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _connectionCts;
        private Task? _receiveTask;
        private bool _manualDisconnect;

        private string _endpoint = string.Empty;
        private int _receiveBufferSize = 8 * 1024;
        private TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
        private TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);
        private bool _autoReconnect = true;
        private bool _connectOnActivate = true;

        /// <summary>
        /// WebSocket endpoint (absolute URI).
        /// </summary>
        public string Endpoint
        {
            get => _endpoint;
            set => SetField(ref _endpoint, value);
        }

        /// <summary>
        /// Initial connection attempt occurs when the component is activated if true.
        /// </summary>
        public bool ConnectOnActivate
        {
            get => _connectOnActivate;
            set => SetField(ref _connectOnActivate, value);
        }

        /// <summary>
        /// Attempts to reconnect automatically after unexpected disconnects when true.
        /// </summary>
        public bool AutoReconnect
        {
            get => _autoReconnect;
            set => SetField(ref _autoReconnect, value);
        }

        /// <summary>
        /// Delay between reconnection attempts.
        /// </summary>
        public TimeSpan ReconnectDelay
        {
            get => _reconnectDelay;
            set => SetField(ref _reconnectDelay, value);
        }

        /// <summary>
        /// Maximum duration allowed for a single connect attempt.
        /// </summary>
        public TimeSpan ConnectionTimeout
        {
            get => _connectTimeout;
            set => SetField(ref _connectTimeout, value);
        }

        /// <summary>
        /// Size of the receive buffer used for streaming WebSocket frames.
        /// </summary>
        public int ReceiveBufferSize
        {
            get => _receiveBufferSize;
            set => SetField(ref _receiveBufferSize, Math.Max(1024, value));
        }

        /// <summary>
        /// Default headers appended to the handshake request.
        /// </summary>
        public IReadOnlyDictionary<string, string> Headers => _headers;

        /// <summary>
        /// Indicates the current socket state when a client is available.
        /// </summary>
        public WebSocketState? SocketState => _socket?.State;

        public event Action<WebSocketClientComponent>? Connected;
        public event Action<WebSocketClientComponent, WebSocketCloseStatus?, string?>? Disconnected;
        public event Action<WebSocketClientComponent, WebSocketMessage>? MessageReceived;
        public event Action<WebSocketClientComponent, Exception>? ConnectionError;

        /// <summary>
        /// Adds or replaces a WebSocket handshake header.
        /// </summary>
        public void SetHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Header name cannot be blank.", nameof(name));

            _headers[name] = value;
        }

        /// <summary>
        /// Removes a handshake header if it exists.
        /// </summary>
        public bool RemoveHeader(string name)
            => _headers.TryRemove(name, out _);

        /// <summary>
        /// Begins (or restarts) the connection loop.
        /// </summary>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
            => RunConnectionLoopAsync(forceReconnect: true, cancellationToken);

        /// <summary>
        /// Gracefully closes the socket and stops auto-reconnect.
        /// </summary>
        public async Task DisconnectAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure, string? description = null)
        {
            _manualDisconnect = true;
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CancelReceiveLoop();
                if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived } socket)
                {
                    try
                    {
                        await socket.CloseAsync(closeStatus, description ?? "closed", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Swallow close exceptions to avoid noisy shutdowns.
                    }
                }

                _socket?.Dispose();
                _socket = null;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Sends a UTF-8 encoded message to the server.
        /// </summary>
        public Task SendTextAsync(string message, CancellationToken cancellationToken = default)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            return SendBinaryAsync(payload, WebSocketMessageType.Text, cancellationToken);
        }

        /// <summary>
        /// Sends arbitrary binary data using the active WebSocket connection.
        /// </summary>
        public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType = WebSocketMessageType.Binary, CancellationToken cancellationToken = default)
        {
            ClientWebSocket? socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            await socket.SendAsync(payload, messageType, true, cancellationToken).ConfigureAwait(false);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _manualDisconnect = false;
            if (ConnectOnActivate)
                _ = RunConnectionLoopAsync(forceReconnect: false, CancellationToken.None);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            _manualDisconnect = true;
            CancelReceiveLoop();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _manualDisconnect = true;
            CancelReceiveLoop();
            _socket?.Dispose();
        }

        private async Task RunConnectionLoopAsync(bool forceReconnect, CancellationToken cancellationToken)
        {
            if (!IsActiveInHierarchy && !forceReconnect)
                return;

            await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CancelReceiveLoop();
                _connectionCts = new CancellationTokenSource();
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_connectionCts.Token, cancellationToken);

                bool firstAttempt = true;
                while (!linked.Token.IsCancellationRequested)
                {
                    if (!firstAttempt && !_autoReconnect)
                        break;

                    if (await TryConnectAsync(linked.Token).ConfigureAwait(false))
                    {
                        await WaitForReceiveLoopAsync(linked.Token).ConfigureAwait(false);
                        if (_manualDisconnect || !_autoReconnect)
                            break;
                    }

                    if (linked.Token.IsCancellationRequested)
                        break;

                    try
                    {
                        await Task.Delay(_reconnectDelay, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    firstAttempt = false;
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task<bool> TryConnectAsync(CancellationToken token)
        {
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? uri))
            {
                DispatchError(new InvalidOperationException($"WebSocket endpoint '{_endpoint}' is invalid."));
                return false;
            }

            _socket?.Dispose();
            var socket = new ClientWebSocket();
            foreach (var header in _headers)
                socket.Options.SetRequestHeader(header.Key, header.Value);

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_connectTimeout);

            try
            {
                await socket.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
                _socket = socket;
                DispatchConnected();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, timeoutCts.Token));
                return true;
            }
            catch (OperationCanceledException ex)
            {
                    DispatchError(ex);
                    return false;
            }
            catch (Exception ex)
            {
                DispatchError(ex);
                socket.Dispose();
                return false;
            }
        }

        private async Task WaitForReceiveLoopAsync(CancellationToken token)
        {
            if (_receiveTask is null)
                return;

            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Ignore cancellation triggered by lifecycle.
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
            WebSocketCloseStatus? closeStatus = null;
            string? closeReason = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var messageStream = new MemoryStream();
                    WebSocketReceiveResult? result;
                    do
                    {
                        ArraySegment<byte> segment = new(buffer);
                        result = await socket.ReceiveAsync(segment, token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            closeStatus = result.CloseStatus ?? socket.CloseStatus;
                            closeReason = result.CloseStatusDescription ?? socket.CloseStatusDescription;
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "remote closed", CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        messageStream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    byte[] payload = messageStream.ToArray();
                    var message = new WebSocketMessage(result.MessageType == WebSocketMessageType.Text, payload);
                    DispatchMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
            catch (Exception ex)
            {
                DispatchError(ex);
                closeStatus ??= socket.CloseStatus;
                closeReason ??= socket.CloseStatusDescription;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                DispatchDisconnected(closeStatus ?? socket.CloseStatus, closeReason ?? socket.CloseStatusDescription);
            }
        }

        private void CancelReceiveLoop()
        {
            try
            {
                _connectionCts?.Cancel();
            }
            catch
            {
                // Ignore CTS disposal races.
            }

            _connectionCts?.Dispose();
            _connectionCts = null;
        }

        private void DispatchConnected()
            => RunOnMainThread(() => Connected?.Invoke(this), "WebSocketClientComponent.Connected");

        private void DispatchDisconnected(WebSocketCloseStatus? status, string? reason)
            => RunOnMainThread(() => Disconnected?.Invoke(this, status, reason), "WebSocketClientComponent.Disconnected");

        private void DispatchMessage(WebSocketMessage message)
            => RunOnMainThread(() => MessageReceived?.Invoke(this, message), "WebSocketClientComponent.MessageReceived");

        private void DispatchError(Exception ex)
            => RunOnMainThread(() => ConnectionError?.Invoke(this, ex), "WebSocketClientComponent.ConnectionError");

        private static void RunOnMainThread(Action action, string reason)
        {
            if (!Engine.InvokeOnMainThread(action, reason))
                action();
        }
    }

    /// <summary>
    /// Represents an incoming WebSocket payload.
    /// </summary>
    public struct WebSocketMessage
    {
        private readonly ReadOnlyMemory<byte> _payload;
        private string? _cachedText;

        public WebSocketMessage(bool isText, ReadOnlyMemory<byte> payload)
        {
            IsText = isText;
            _payload = payload;
            _cachedText = null;
        }

        /// <summary>
        /// True if the message originated from a text frame.
        /// </summary>
        public bool IsText { get; }

        /// <summary>
        /// Raw payload bytes.
        /// </summary>
        public ReadOnlyMemory<byte> Payload => _payload;

        /// <summary>
        /// Lazily converts the payload to a UTF-8 string (only valid when <see cref="IsText"/> is true).
        /// </summary>
        public string? Text
        {
            get
            {
                if (!IsText)
                    return null;
                _cachedText ??= Encoding.UTF8.GetString(_payload.Span);
                return _cachedText;
            }
        }
    }
}
