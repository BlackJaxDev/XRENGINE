using System.Net;
using System.Security.Cryptography.X509Certificates;
using XREngine.ControlPlane;

namespace XREngine.Networking;

internal sealed partial class ManagedServerWorker
{
    private RealtimeTlsGateway? _tlsGateway;
    private X509Certificate2? _tlsCertificate;

    private bool EncryptedIngressReady => _launch.AdvertisedEndpoint.Transport != RealtimeTransportKind.NativeTls
        || _tlsGateway is { IsListening: true };

    private void StartEncryptedIngress()
    {
        if (_launch.AdvertisedEndpoint.Transport != RealtimeTransportKind.NativeTls)
            return;
        ManagedWorkerTlsConfiguration config = _launch.Tls!;
        using var store = new X509Store(StoreName.My, config.UseMachineCertificateStore ? StoreLocation.LocalMachine : StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2Collection matches = store.Certificates.Find(X509FindType.FindByThumbprint, config.CertificateThumbprint, validOnly: false);
        _tlsCertificate = matches.Cast<X509Certificate2>().SingleOrDefault(certificate => certificate.HasPrivateKey
            && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            ?? throw new InvalidOperationException("The configured realtime certificate with a valid private key is unavailable.");
        _tlsGateway = new RealtimeTlsGateway(new IPEndPoint(IPAddress.Parse(config.ListenAddress), config.ListenPort),
            new IPEndPoint(BindAddress, BindPort), _tlsCertificate, config.MaximumConnections, config.MaximumConnectionsPerAddress);
        _tlsGateway.Start();
    }

    private static void ValidateEncryptedIngress(ManagedWorkerLaunch launch)
    {
        if (launch.AdvertisedEndpoint.Transport is not (RealtimeTransportKind.NativeUdp or RealtimeTransportKind.NativeTls))
            throw new NotSupportedException("Unknown managed realtime transport.");
        if (!IPAddress.TryParse(launch.BindAddress, out IPAddress? bind) || !IPAddress.IsLoopback(bind)
            || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("Managed raw UDP must remain on literal IPv4 loopback. Remote ingress requires NativeTls.");
        if (launch.AdvertisedEndpoint.Transport == RealtimeTransportKind.NativeUdp)
        {
            if (!IPAddress.TryParse(launch.AdvertisedEndpoint.Host, out IPAddress? advertised) || !IPAddress.IsLoopback(advertised))
                throw new InvalidOperationException("Unencrypted managed development endpoints must remain on loopback.");
            return;
        }
        if (launch.Tls is not { } tls || !IPAddress.TryParse(tls.ListenAddress, out _)
            || tls.ListenPort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(tls.CertificateThumbprint)
            || tls.MaximumConnections is < 1 or > 1024 || tls.MaximumConnectionsPerAddress is < 1 or > 1024)
            throw new InvalidOperationException("NativeTls requires a bounded gateway and an operator-provisioned certificate.");
        if (!IPAddress.IsLoopback(IPAddress.Parse(tls.ListenAddress)) && (launch.AdmissionSigningKeys.Count is < 1 or > 8
            || string.IsNullOrWhiteSpace(launch.AdmissionIssuer)))
            throw new InvalidOperationException("Remote encrypted ingress requires explicit admission signing trust.");
    }
}
