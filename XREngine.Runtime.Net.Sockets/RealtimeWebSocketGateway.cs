using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace XREngine.Networking;

/// <summary>Encrypted browser ingress carrying the production managed datagram protocol to one immutable loopback worker.</summary>
public sealed class RealtimeWebSocketGateway : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly IPEndPoint _worker;
    private readonly string _authority;
    private readonly HashSet<string> _origins;
    private readonly Guid _session;
    private readonly Guid _generation;
    private readonly int _maximumConnections;
    private readonly int _maximumConnectionsPerAddress;
    private readonly object _addressLock = new();
    private readonly Dictionary<IPAddress, int> _addressCounts = [];
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptTask;
    private int _disposed;
    private long _rejected;
    private long _received;
    private long _sent;

    public RealtimeWebSocketGateway(IPEndPoint listenEndpoint, IPEndPoint workerEndpoint, X509Certificate2 certificate,
        Uri advertisedEndpoint, IEnumerable<string> allowedOrigins, Guid session, Guid generation,
        int maximumConnections = 64, int maximumConnectionsPerAddress = 16)
    {
        RealtimeWebSocketProtocol.ValidateEndpoint(advertisedEndpoint);
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        if (workerEndpoint.Address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(workerEndpoint.Address))
            throw new ArgumentException("Realtime WebSocket gateway target must be a literal IPv4 loopback worker.");
        if (!certificate.HasPrivateKey || session == Guid.Empty || generation == Guid.Empty
            || maximumConnections is < 1 or > 1024 || maximumConnectionsPerAddress is < 1 or > 1024)
            throw new ArgumentException("Realtime WebSocket gateway requires a certificate, worker identity, and bounded capacity.");
        _origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (string origin in allowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || origin != uri.GetLeftPart(UriPartial.Authority))
                throw new ArgumentException("Realtime WebSocket origins must be exact HTTPS origins without paths; HTTP is restricted to loopback development origins.");
            _origins.Add(origin);
        }
        if (_origins.Count is < 1 or > 32)
            throw new ArgumentException("Realtime WebSocket requires between one and 32 explicit allowed origins.");
        _listener = new TcpListener(listenEndpoint);
        _worker = new IPEndPoint(workerEndpoint.Address, workerEndpoint.Port);
        _certificate = certificate;
        _authority = advertisedEndpoint.Authority;
        _session = session;
        _generation = generation;
        _maximumConnections = maximumConnections;
        _maximumConnectionsPerAddress = maximumConnectionsPerAddress;
    }

    public bool IsListening => _acceptTask is { IsCompleted: false } && Volatile.Read(ref _disposed) == 0;
    public int ConnectionCount => _connections.Count;
    public long RejectedConnections => Interlocked.Read(ref _rejected);
    public long ReceivedBytes => Interlocked.Read(ref _received);
    public long SentBytes => Interlocked.Read(ref _sent);
    /// <summary>Only a bounded error category is retained, never URLs, peer messages, or credentials.</summary>
    public string? LastFailure { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_acceptTask is not null)
            throw new InvalidOperationException("Realtime WebSocket gateway has already started.");
        _listener.Start(_maximumConnections);
        _acceptTask = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient tcp = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                IPAddress address = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                bool admitted;
                lock (_addressLock)
                {
                    _addressCounts.TryGetValue(address, out int count);
                    admitted = _connections.Count < _maximumConnections && count < _maximumConnectionsPerAddress;
                    if (admitted)
                    {
                        _addressCounts[address] = count + 1;
                        _connections.TryAdd(tcp, 0);
                    }
                }
                if (!admitted)
                {
                    Interlocked.Increment(ref _rejected);
                    tcp.Dispose();
                    continue;
                }
                tcp.NoDelay = true;
                _ = ServeAsync(tcp, address);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
            {
                LastFailure = "Realtime WebSocket listener failed.";
                Dispose();
            }
        }
    }

    private async Task ServeAsync(TcpClient tcp, IPAddress address)
    {
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromHours(8));
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        AllowRenegotiation = false,
                    }, deadline.Token).ConfigureAwait(false);
                    await RealtimeWebSocketUpgrade.AcceptAsync(tls, _authority, _origins, deadline.Token).ConfigureAwait(false);
                }
                using WebSocket connection = WebSocket.CreateFromStream(tls, new WebSocketCreationOptions
                {
                    IsServer = true,
                    SubProtocol = RealtimeWebSocketProtocol.Subprotocol,
                    KeepAliveInterval = TimeSpan.FromSeconds(15),
                });
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                udp.Client.ReceiveBufferSize = RealtimeWebSocketProtocol.MaximumQueuedBytes;
                udp.Client.SendBufferSize = RealtimeWebSocketProtocol.MaximumQueuedBytes;
                udp.Connect(_worker);
                Task ingress = ForwardToWorkerAsync(connection, udp, lifetime.Token);
                Task egress = ForwardToClientAsync(udp, connection, lifetime.Token);
                await Task.WhenAny(ingress, egress).ConfigureAwait(false);
                lifetime.Cancel();
                connection.Abort();
                await Task.WhenAll(ingress, egress).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or AuthenticationException or WebSocketException
                or OperationCanceledException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    LastFailure = "Realtime WebSocket connection rejected or closed (required "
                        + RealtimeWebSocketProtocol.Subprotocol + "): " + exception.GetType().Name;
                    Interlocked.Increment(ref _rejected);
                }
            }
            finally
            {
                _connections.TryRemove(tcp, out _);
                lock (_addressLock)
                    if (_addressCounts[address] == 1)
                        _addressCounts.Remove(address);
                    else
                        _addressCounts[address]--;
            }
        }
    }

    private async Task ForwardToWorkerAsync(WebSocket connection, UdpClient udp, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[RealtimeWebSocketProtocol.MaximumDatagramBytes];
        long window = Stopwatch.GetTimestamp();
        int frames = 0;
        int bytes = 0;
        bool first = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            int count = 0;
            int fragments = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length || ++fragments > 256)
                    throw new InvalidDataException("Realtime WebSocket message bounds exceeded.");
                result = await connection.ReceiveAsync(buffer.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("Realtime WebSocket accepts only binary managed datagrams.");
                count += result.Count;
            } while (!result.EndOfMessage);
            if (RealtimeWireProtocol.IsIncompatible(buffer.AsSpan(0, count)))
                throw new InvalidDataException(RealtimeWireProtocol.UpdateRequiredMessage);
            if (!ManagedUdpEnvelope.TryRead(buffer.AsSpan(0, count), out var header, out _, out _)
                || header.Direction != ManagedUdpDirection.ClientToServer || header.SessionId != _session || header.Generation != _generation
                || first && header.Kind != ManagedUdpMessageKind.Hello)
                throw new InvalidDataException("Realtime WebSocket datagram does not match the managed worker.");
            first = false;
            if (Stopwatch.GetElapsedTime(window).TotalSeconds >= 1)
            {
                window = Stopwatch.GetTimestamp();
                frames = bytes = 0;
            }
            if (++frames > 240 || (bytes += count) > RealtimeWebSocketProtocol.MaximumQueuedBytes)
                throw new InvalidDataException("Realtime WebSocket ingress budget exceeded.");
            // Authorization and MAC/replay checks remain exclusively in the production worker.
            await udp.SendAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            Interlocked.Add(ref _received, count);
        }
    }

    private async Task ForwardToClientAsync(UdpClient udp, WebSocket connection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            UdpReceiveResult packet = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!packet.RemoteEndPoint.Equals(_worker)
                || !ManagedUdpEnvelope.TryRead(packet.Buffer, out var header, out _, out _)
                || header.Direction != ManagedUdpDirection.ServerToClient || header.SessionId != _session || header.Generation != _generation)
                throw new InvalidDataException("Realtime WebSocket received an unexpected worker datagram.");
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await connection.SendAsync(packet.Buffer.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
            Interlocked.Add(ref _sent, packet.Buffer.Length);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Stop();
        foreach (TcpClient connection in _connections.Keys)
            connection.Dispose();
    }
}
