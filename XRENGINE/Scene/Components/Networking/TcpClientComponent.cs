using System;
using System.Buffers;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// XR component that manages a TCP client connection with optional TLS, auto-reconnect, and main-thread event dispatching.
    /// </summary>
    public class TcpClientComponent : XRComponent
    {
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private CancellationTokenSource? _connectionCts;
        private Task? _receiveTask;
        private TcpClient? _client;
        private Stream? _activeStream;
        private bool _manualDisconnect;

        private string _host = "localhost";
        private int _port = 0;
        private bool _connectOnActivate = true;
        private bool _autoReconnect = true;
        private TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
        private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(10);
        private int _receiveBufferSize = 8 * 1024;
        private bool _useTls;
        private string? _tlsHostName;
        private SslProtocols _tlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        private bool _ignoreCertificateErrors;
        private bool _enableNagle;
        private bool _dispatchTextMessages = true;
        private Encoding _textEncoding = Encoding.UTF8;

        public event Action<TcpClientComponent>? Connected;
        public event Action<TcpClientComponent, Exception?>? Disconnected;
        public event Action<TcpClientComponent, ReadOnlyMemory<byte>>? DataReceived;
        public event Action<TcpClientComponent, string>? TextReceived;
        public event Action<TcpClientComponent, Exception>? ConnectionError;

        /// <summary>
        /// Remote host or IP address.
        /// </summary>
        public string Host
        {
            get => _host;
            set => SetField(ref _host, value);
        }

        /// <summary>
        /// Remote TCP port.
        /// </summary>
        public int Port
        {
            get => _port;
            set => SetField(ref _port, value);
        }

        /// <summary>
        /// Attempts connection when the component becomes active.
        /// </summary>
        public bool ConnectOnActivate
        {
            get => _connectOnActivate;
            set => SetField(ref _connectOnActivate, value);
        }

        /// <summary>
        /// Enables automatic reconnection once an established link is lost.
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
        /// Maximum time to wait for an individual connect attempt.
        /// </summary>
        public TimeSpan ConnectionTimeout
        {
            get => _connectionTimeout;
            set => SetField(ref _connectionTimeout, value);
        }

        /// <summary>
        /// Buffer size used while streaming incoming data.
        /// </summary>
        public int ReceiveBufferSize
        {
            get => _receiveBufferSize;
            set => SetField(ref _receiveBufferSize, Math.Max(1024, value));
        }

        /// <summary>
        /// Enables TLS (SSL) negotiation on top of the socket.
        /// </summary>
        public bool UseTls
        {
            get => _useTls;
            set => SetField(ref _useTls, value);
        }

        /// <summary>
        /// Overrides the TLS host name used for certificate validation (defaults to Host).
        /// </summary>
        public string? TlsHostName
        {
            get => _tlsHostName;
            set => SetField(ref _tlsHostName, value);
        }

        /// <summary>
        /// TLS protocol versions permitted during negotiation.
        /// </summary>
        public SslProtocols TlsProtocols
        {
            get => _tlsProtocols;
            set => SetField(ref _tlsProtocols, value);
        }

        /// <summary>
        /// When true any certificate validation errors are ignored.
        /// </summary>
        public bool IgnoreCertificateErrors
        {
            get => _ignoreCertificateErrors;
            set => SetField(ref _ignoreCertificateErrors, value);
        }

        /// <summary>
        /// Enables Nagle's algorithm when true (latency vs throughput).
        /// </summary>
        public bool EnableNagle
        {
            get => _enableNagle;
            set => SetField(ref _enableNagle, value);
        }

        /// <summary>
        /// Emits decoded UTF-8 messages whenever bytes are received.
        /// </summary>
        public bool DispatchTextMessages
        {
            get => _dispatchTextMessages;
            set => SetField(ref _dispatchTextMessages, value);
        }

        /// <summary>
        /// Encoding used to convert text payloads.
        /// </summary>
        public Encoding TextEncoding
        {
            get => _textEncoding;
            set => SetField(ref _textEncoding, value ?? Encoding.UTF8);
        }

        /// <summary>
        /// True when the client stream is ready.
        /// </summary>
        public bool IsConnected => _activeStream is not null;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
            => RunConnectionLoopAsync(forceReconnect: true, cancellationToken);

        public Task DisconnectAsync()
        {
            _manualDisconnect = true;
            return StopConnectionAsync();
        }

        /// <summary>
        /// Sends raw bytes across the active stream.
        /// </summary>
        public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Stream? stream = _activeStream;
            if (stream is null)
                throw new InvalidOperationException("TCP connection is not established.");

            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends UTF-8 encoded text to the remote endpoint.
        /// </summary>
        public Task SendStringAsync(string text, CancellationToken cancellationToken = default)
        {
            byte[] payload = _textEncoding.GetBytes(text);
            return SendAsync(payload, cancellationToken);
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
            _ = StopConnectionAsync();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _manualDisconnect = true;
            _ = StopConnectionAsync();
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
                        CleanupConnection();
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
            if (string.IsNullOrWhiteSpace(_host) || _port <= 0)
            {
                DispatchError(new InvalidOperationException("Host and Port must be configured before connecting."));
                return false;
            }

            CleanupConnection();
            var client = new TcpClient
            {
                NoDelay = !_enableNagle
            };

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_connectionTimeout);

            try
            {
                await client.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
                Stream stream = client.GetStream();

                if (_useTls)
                {
                    var sslStream = new SslStream(stream, leaveInnerStreamOpen: false, ValidateCertificate);
                    var authOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = string.IsNullOrWhiteSpace(_tlsHostName) ? _host : _tlsHostName,
                        EnabledSslProtocols = _tlsProtocols,
                        RemoteCertificateValidationCallback = ValidateCertificate
                    };

                    await sslStream.AuthenticateAsClientAsync(authOptions, timeoutCts.Token).ConfigureAwait(false);
                    stream = sslStream;
                }

                _client = client;
                _activeStream = stream;
                DispatchConnected();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(stream, token));
                return true;
            }
            catch (OperationCanceledException ex)
            {
                client.Dispose();
                DispatchError(ex);
                return false;
            }
            catch (Exception ex)
            {
                client.Dispose();
                DispatchError(ex);
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
                // Swallow lifecycle cancellations.
            }
        }

        private async Task ReceiveLoopAsync(Stream stream, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
            Exception? disconnectReason = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    byte[] payload = new byte[read];
                    Buffer.BlockCopy(buffer, 0, payload, 0, read);
                    DispatchData(payload);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down.
            }
            catch (Exception ex)
            {
                disconnectReason = ex;
                DispatchError(ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                DispatchDisconnected(disconnectReason);
            }
        }

        private async Task StopConnectionAsync()
        {
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CancelReceiveLoop();
                CleanupConnection();
            }
            finally
            {
                _connectionLock.Release();
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

        private void CleanupConnection()
        {
            try
            {
                _activeStream?.Dispose();
            }
            catch
            {
                // Ignore shutdown races.
            }

            _activeStream = null;
            _client?.Dispose();
            _client = null;
        }

        private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (_ignoreCertificateErrors)
                return true;

            return errors == SslPolicyErrors.None;
        }

        private void DispatchConnected()
            => RunOnMainThread(() => Connected?.Invoke(this), "TcpClientComponent.Connected");

        private void DispatchDisconnected(Exception? ex)
            => RunOnMainThread(() => Disconnected?.Invoke(this, ex), "TcpClientComponent.Disconnected");

        private void DispatchData(ReadOnlyMemory<byte> payload)
        {
            RunOnMainThread(() => DataReceived?.Invoke(this, payload), "TcpClientComponent.DataReceived");
            if (_dispatchTextMessages)
            {
                string text = _textEncoding.GetString(payload.Span);
                RunOnMainThread(() => TextReceived?.Invoke(this, text), "TcpClientComponent.TextReceived");
            }
        }

        private void DispatchError(Exception ex)
            => RunOnMainThread(() => ConnectionError?.Invoke(this, ex), "TcpClientComponent.ConnectionError");

        private static void RunOnMainThread(Action action, string reason)
        {
            if (!Engine.InvokeOnMainThread(action, reason))
                action();
        }
    }
}
