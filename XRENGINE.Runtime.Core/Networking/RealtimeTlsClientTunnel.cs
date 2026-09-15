using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XREngine.Networking;

/// <summary>Encrypts the managed datagram protocol over a server-authenticated TLS connection, without a plaintext fallback.</summary>
public sealed class RealtimeTlsClientTunnel : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _tls;
    private readonly UdpClient _local;
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _disposed;

    private RealtimeTlsClientTunnel(TcpClient tcp, SslStream tls)
    {
        _tcp = tcp;
        _tls = tls;
        _local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_local.Client.LocalEndPoint!;
    /// <summary>Credential-free terminal failure, also observed by the managed connection's silence deadline.</summary>
    public string? Failure { get; private set; }

    public static async Task<RealtimeTlsClientTunnel> ConnectAsync(IPAddress address, int port, string serverName,
        string? developmentCertificateSha256 = null, CancellationToken cancellationToken = default)
    {
        // Pinning a self-signed certificate is deliberately restricted to a literal local endpoint.
        // Remote production servers must pass platform chain, name, validity and revocation checks.
        if (!string.IsNullOrEmpty(developmentCertificateSha256) && !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Development certificate pins require a loopback endpoint.");
        byte[]? pin = string.IsNullOrEmpty(developmentCertificateSha256) ? null : Convert.FromHexString(developmentCertificateSha256);
        if (pin is not null && pin.Length != 32)
            throw new ArgumentException("Development certificate fingerprint must be SHA-256.");
        TcpClient tcp = new(address.AddressFamily) { NoDelay = true };
        SslStream? tls = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            RemoteCertificateValidationCallback? validator = pin is null ? null : (_, certificate, _, errors) =>
                certificate is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                && certificate is X509Certificate2 certificate2
                && CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(certificate2.PublicKey.ExportSubjectPublicKeyInfo()))
                && DateTime.UtcNow >= certificate2.NotBefore.ToUniversalTime()
                && DateTime.UtcNow < certificate2.NotAfter.ToUniversalTime();
            tls = new SslStream(tcp.GetStream(), false, validator);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = pin is null ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                ApplicationProtocols = [RealtimeTlsFraming.Protocol],
                AllowRenegotiation = false,
            }, deadline.Token).ConfigureAwait(false);
            if (tls.NegotiatedApplicationProtocol != RealtimeTlsFraming.Protocol)
                throw new AuthenticationException("The server did not negotiate the realtime protocol.");
            return new RealtimeTlsClientTunnel(tcp, tls);
        }
        catch
        {
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Binds the bridge to the engine socket; another local sender cannot retarget this connection.</summary>
    public void Start(IPEndPoint engineEndpoint)
    {
        if (!IPAddress.IsLoopback(engineEndpoint.Address) || _pump is not null)
            throw new InvalidOperationException("A TLS bridge requires exactly one local engine socket.");
        _local.Connect(engineEndpoint);
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            Task send = SendAsync();
            Task receive = ReceiveAsync();
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            _stop.Cancel();
            await Task.WhenAll(send, receive).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposed) == 0)
                Failure = "Encrypted realtime connection closed.";
        }
        finally { Dispose(); }
    }

    private async Task SendAsync()
    {
        byte[] frame = new byte[RealtimeTlsFraming.MaximumDatagramBytes + 4];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        while (!_stop.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            UdpReceiveResult packet = await _local.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            await RealtimeTlsFraming.WriteAsync(_tls, packet.Buffer, frame, deadline.Token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[RealtimeTlsFraming.MaximumDatagramBytes];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        while (!_stop.IsCancellationRequested)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            int count = await RealtimeTlsFraming.ReadAsync(_tls, buffer, deadline.Token).ConfigureAwait(false);
            await _local.SendAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _local.Dispose();
        _tls.Dispose();
        _tcp.Dispose();
        // Pump continuations own no world objects and observe cancellation; do not block a simulation fence.
    }
}
