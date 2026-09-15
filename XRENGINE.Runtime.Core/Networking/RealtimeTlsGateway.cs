using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace XREngine.Networking;

/// <summary>Bounded TLS 1.3 ingress to one immutable loopback worker target. Each connection owns its UDP association endpoint.</summary>
public sealed class RealtimeTlsGateway : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly IPEndPoint _worker;
    private readonly int _maximumConnections;
    private readonly int _maximumConnectionsPerAddress;
    private readonly object _addressLock = new();
    private readonly Dictionary<IPAddress, int> _addressCounts = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private Task? _acceptTask;
    private long _rejectedConnections;
    private long _receivedBytes;
    private long _sentBytes;
    private int _disposed;

    public RealtimeTlsGateway(IPEndPoint listenEndpoint, IPEndPoint workerEndpoint, X509Certificate2 certificate, int maximumConnections,
        int maximumConnectionsPerAddress = 32)
    {
        if (workerEndpoint.Address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(workerEndpoint.Address))
            throw new ArgumentException("The encrypted gateway target must be a literal IPv4 loopback worker.");
        if (!certificate.HasPrivateKey || maximumConnections is < 1 or > 1024 || maximumConnectionsPerAddress is < 1 or > 1024)
            throw new ArgumentException("The encrypted gateway requires a private certificate key and bounded capacity.");
        _listener = new TcpListener(listenEndpoint);
        _worker = workerEndpoint;
        _certificate = certificate;
        _maximumConnections = maximumConnections;
        _maximumConnectionsPerAddress = maximumConnectionsPerAddress;
    }

    public int ConnectionCount => _connections.Count;
    public long RejectedConnections => Interlocked.Read(ref _rejectedConnections);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public bool IsListening => _acceptTask is { IsCompleted: false } && Volatile.Read(ref _disposed) == 0;
    /// <summary>Last bounded transport failure category and platform error, without peer payloads or credentials.</summary>
    public string? LastFailure { get; private set; }

    public void Start()
    {
        if (_acceptTask is not null)
            throw new InvalidOperationException("The TLS gateway has already started.");
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
                IPAddress sourceAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                bool accepted;
                lock (_addressLock)
                {
                    _addressCounts.TryGetValue(sourceAddress, out int addressCount);
                    accepted = _connections.Count < _maximumConnections && addressCount < _maximumConnectionsPerAddress;
                    if (accepted)
                        _addressCounts[sourceAddress] = addressCount + 1;
                }
                if (!accepted)
                {
                    Interlocked.Increment(ref _rejectedConnections);
                    tcp.Dispose();
                    continue;
                }
                tcp.NoDelay = true;
                _connections.TryAdd(tcp, 0);
                _ = ServeAsync(tcp, sourceAddress);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
                Dispose();
        }
    }

    private async Task ServeAsync(TcpClient tcp, IPAddress sourceAddress)
    {
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromHours(8));
            try
            {
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        ApplicationProtocols = [RealtimeTlsFraming.Protocol],
                        AllowRenegotiation = false,
                    }, handshake.Token).ConfigureAwait(false);
                }
                if (tls.NegotiatedApplicationProtocol != RealtimeTlsFraming.Protocol)
                    throw new AuthenticationException("Realtime TLS protocol negotiation failed.");
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                udp.Connect(_worker);
                Task ingress = ForwardToWorkerAsync(tls, udp, lifetime.Token);
                Task egress = ForwardToClientAsync(udp, tls, lifetime.Token);
                await Task.WhenAny(ingress, egress).ConfigureAwait(false);
                lifetime.Cancel();
                await Task.WhenAll(ingress, egress).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                LastFailure = exception.GetType().Name + ": " + exception.Message + " (" + (exception.InnerException?.HResult ?? exception.HResult).ToString("X8") + ")";
                Interlocked.Increment(ref _rejectedConnections);
            }
            finally
            {
                _connections.TryRemove(tcp, out _);
                lock (_addressLock)
                    if (_addressCounts[sourceAddress] == 1)
                        _addressCounts.Remove(sourceAddress);
                    else
                        _addressCounts[sourceAddress]--;
            }
        }
    }

    private async Task ForwardToWorkerAsync(SslStream tls, UdpClient udp, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[RealtimeTlsFraming.MaximumDatagramBytes];
        long window = Stopwatch.GetTimestamp();
        int frames = 0;
        int bytes = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            int count = await RealtimeTlsFraming.ReadAsync(tls, buffer, deadline.Token).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(window).TotalSeconds >= 1)
            {
                window = Stopwatch.GetTimestamp();
                frames = bytes = 0;
            }
            if (++frames > 240 || (bytes += count) > 2 * 1024 * 1024)
                throw new InvalidDataException("Encrypted realtime ingress budget exceeded.");
            await udp.SendAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            Interlocked.Add(ref _receivedBytes, count);
        }
    }

    private async Task ForwardToClientAsync(UdpClient udp, SslStream tls, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[RealtimeTlsFraming.MaximumDatagramBytes + 4];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            UdpReceiveResult packet = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            if (!packet.RemoteEndPoint.Equals(_worker))
                throw new InvalidDataException("Unexpected worker response endpoint.");
            await RealtimeTlsFraming.WriteAsync(tls, packet.Buffer, frame, deadline.Token).ConfigureAwait(false);
            Interlocked.Add(ref _sentBytes, packet.Buffer.Length);
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
