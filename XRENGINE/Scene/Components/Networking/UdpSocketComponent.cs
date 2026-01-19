using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Components
{
    /// <summary>
    /// UDP helper component that can bind to a local port, listen for datagrams, and broadcast packets.
    /// </summary>
    public class UdpSocketComponent : XRComponent
    {
        private readonly SemaphoreSlim _socketLock = new(1, 1);
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private UdpClient? _client;
        private bool _manualShutdown;

        private string _localAddress = "0.0.0.0";
        private int _localPort;
        private string _remoteHost = "127.0.0.1";
        private int _remotePort;
        private bool _autoBindOnActivate = true;
        private bool _allowBroadcast = true;
        private bool _reuseAddress = true;
        private bool _dispatchTextMessages = true;
        private Encoding _textEncoding = Encoding.UTF8;
        private string? _multicastAddress;
        private bool _restartOnFailure = true;

        public event Action<UdpSocketComponent, IPEndPoint>? Bound;
        public event Action<UdpSocketComponent>? Unbound;
        public event Action<UdpSocketComponent, UdpDatagram>? DatagramReceived;
        public event Action<UdpSocketComponent, string, IPEndPoint>? TextReceived;
        public event Action<UdpSocketComponent, Exception>? SocketError;

        /// <summary>
        /// Address the socket binds to (defaults to any interface).
        /// </summary>
        public string LocalAddress
        {
            get => _localAddress;
            set => SetField(ref _localAddress, value);
        }

        /// <summary>
        /// Local port the socket listens on (0 selects an ephemeral port).
        /// </summary>
        public int LocalPort
        {
            get => _localPort;
            set => SetField(ref _localPort, Math.Max(0, value));
        }

        /// <summary>
        /// Default remote host for outbound packets.
        /// </summary>
        public string RemoteHost
        {
            get => _remoteHost;
            set => SetField(ref _remoteHost, value);
        }

        /// <summary>
        /// Default remote port for outbound packets.
        /// </summary>
        public int RemotePort
        {
            get => _remotePort;
            set => SetField(ref _remotePort, Math.Max(0, value));
        }

        /// <summary>
        /// Automatically binds when the component activates.
        /// </summary>
        public bool AutoBindOnActivate
        {
            get => _autoBindOnActivate;
            set => SetField(ref _autoBindOnActivate, value);
        }

        /// <summary>
        /// Enables broadcast traffic on the socket.
        /// </summary>
        public bool AllowBroadcast
        {
            get => _allowBroadcast;
            set => SetField(ref _allowBroadcast, value);
        }

        /// <summary>
        /// Allows multiple sockets to share the same address/port.
        /// </summary>
        public bool ReuseAddress
        {
            get => _reuseAddress;
            set => SetField(ref _reuseAddress, value);
        }

        /// <summary>
        /// If provided, joins the given multicast group after binding.
        /// </summary>
        public string? MulticastAddress
        {
            get => _multicastAddress;
            set => SetField(ref _multicastAddress, value);
        }

        /// <summary>
        /// Emits decoded text messages in addition to raw datagrams.
        /// </summary>
        public bool DispatchTextMessages
        {
            get => _dispatchTextMessages;
            set => SetField(ref _dispatchTextMessages, value);
        }

        /// <summary>
        /// Encoding used to convert payloads to text.
        /// </summary>
        public Encoding TextEncoding
        {
            get => _textEncoding;
            set => SetField(ref _textEncoding, value ?? Encoding.UTF8);
        }

        /// <summary>
        /// Attempts to rebind automatically after socket errors.
        /// </summary>
        public bool RestartOnFailure
        {
            get => _restartOnFailure;
            set => SetField(ref _restartOnFailure, value);
        }

        /// <summary>
        /// True when the socket is currently bound.
        /// </summary>
        public bool IsBound => _client is not null;

        public Task BindAsync(CancellationToken cancellationToken = default)
            => EnsureBoundAsync(cancellationToken);

        public Task CloseAsync()
        {
            _manualShutdown = true;
            return StopBindingAsync();
        }

        /// <summary>
        /// Sends a datagram to the configured remote endpoint.
        /// </summary>
        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => SendAsync(payload, _remoteHost, _remotePort, cancellationToken);

        /// <summary>
        /// Sends a datagram to an explicit host and port.
        /// </summary>
        public async Task SendAsync(ReadOnlyMemory<byte> payload, string? host, int port, CancellationToken cancellationToken = default)
        {
            UdpClient? client = _client;
            if (client is null)
                throw new InvalidOperationException("Socket is not bound.");

            if (string.IsNullOrWhiteSpace(host) || port <= 0)
                throw new InvalidOperationException("A valid host and port are required.");

            IPAddress? address = await ResolveHostAsync(host).ConfigureAwait(false);
            if (address is null)
                throw new InvalidOperationException($"Unable to resolve host '{host}'.");

            var endpoint = new IPEndPoint(address, port);
            byte[] buffer = payload.ToArray();
            await client.SendAsync(buffer, buffer.Length, endpoint).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends UTF-8 encoded text using the UDP socket.
        /// </summary>
        public Task SendStringAsync(string text, CancellationToken cancellationToken = default)
        {
            byte[] payload = _textEncoding.GetBytes(text);
            return SendAsync(payload, cancellationToken);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _manualShutdown = false;
            if (_autoBindOnActivate)
                _ = EnsureBoundAsync(CancellationToken.None);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            _manualShutdown = true;
            _ = StopBindingAsync();
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _manualShutdown = true;
            _ = StopBindingAsync();
        }

        private async Task EnsureBoundAsync(CancellationToken cancellationToken)
        {
            await _socketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client is not null)
                    return;

                var address = ResolveLocalAddress();
                var localEndPoint = new IPEndPoint(address, _localPort);
                var client = new UdpClient(localEndPoint.AddressFamily);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, _reuseAddress);
                client.Client.Bind(localEndPoint);
                client.EnableBroadcast = _allowBroadcast;

                if (!string.IsNullOrWhiteSpace(_multicastAddress) && IPAddress.TryParse(_multicastAddress, out IPAddress? multicast) && multicast.AddressFamily == AddressFamily.InterNetwork)
                {
                    client.JoinMulticastGroup(multicast);
                }

                _client = client;
                _receiveCts = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(client, _receiveCts.Token));
                DispatchBound((IPEndPoint)client.Client.LocalEndPoint!);
            }
            finally
            {
                _socketLock.Release();
            }
        }

        private async Task StopBindingAsync()
        {
            await _socketLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CancelReceiveLoop();
                _client?.Dispose();
                _client = null;
            }
            finally
            {
                _socketLock.Release();
            }
        }

        private void CancelReceiveLoop()
        {
            try
            {
                _receiveCts?.Cancel();
            }
            catch
            {
                // Ignore CTS disposal races.
            }

            _receiveCts?.Dispose();
            _receiveCts = null;
            _receiveTask = null;
        }

        private async Task ReceiveLoopAsync(UdpClient client, CancellationToken token)
        {
            bool shouldRestart = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result = await client.ReceiveAsync(token).ConfigureAwait(false);
                    var datagram = new UdpDatagram(result.RemoteEndPoint, result.Buffer);
                    DispatchDatagram(datagram);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down.
            }
            catch (Exception ex)
            {
                DispatchSocketError(ex);
                shouldRestart = _restartOnFailure && !_manualShutdown && IsActiveInHierarchy;
            }
            finally
            {
                DispatchUnbound();
                _client?.Dispose();
                _client = null;
                _receiveTask = null;

                if (shouldRestart)
                    _ = EnsureBoundAsync(CancellationToken.None);
            }
        }

        private IPAddress ResolveLocalAddress()
        {
            if (IPAddress.TryParse(_localAddress, out IPAddress? parsed))
                return parsed;

            return _localAddress.Contains(':') ? IPAddress.IPv6Any : IPAddress.Any;
        }

        private static async Task<IPAddress?> ResolveHostAsync(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress? address))
                return address;

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            return addresses.Length > 0 ? addresses[0] : null;
        }

        private void DispatchBound(IPEndPoint endPoint)
            => RunOnMainThread(() => Bound?.Invoke(this, endPoint), "UdpSocketComponent.Bound");

        private void DispatchUnbound()
            => RunOnMainThread(() => Unbound?.Invoke(this), "UdpSocketComponent.Unbound");

        private void DispatchDatagram(UdpDatagram datagram)
        {
            RunOnMainThread(() => DatagramReceived?.Invoke(this, datagram), "UdpSocketComponent.DatagramReceived");
            if (_dispatchTextMessages)
            {
                string text = _textEncoding.GetString(datagram.Payload.Span);
                RunOnMainThread(() => TextReceived?.Invoke(this, text, datagram.RemoteEndPoint), "UdpSocketComponent.TextReceived");
            }
        }

        private void DispatchSocketError(Exception ex)
            => RunOnMainThread(() => SocketError?.Invoke(this, ex), "UdpSocketComponent.SocketError");

        private static void RunOnMainThread(Action action, string reason)
        {
            if (!Engine.InvokeOnMainThread(action, reason))
                action();
        }
    }

    /// <summary>
    /// Represents a UDP datagram with helpers to decode the payload.
    /// </summary>
    public readonly struct UdpDatagram
    {
        public UdpDatagram(IPEndPoint remoteEndPoint, byte[] payload)
        {
            RemoteEndPoint = remoteEndPoint;
            Payload = payload;
        }

        public IPEndPoint RemoteEndPoint { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public string GetText(Encoding? encoding = null)
            => (encoding ?? Encoding.UTF8).GetString(Payload.Span);
    }
}
