namespace XREngine.ControlPlane;

/// <summary>Private host configuration for encrypted ingress. The certificate private key stays in the Windows certificate store.</summary>
public sealed class ManagedWorkerTlsConfiguration
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; }
    public string CertificateThumbprint { get; set; } = string.Empty;
    public bool UseMachineCertificateStore { get; set; }
    public int MaximumConnections { get; set; } = 64;
    public int MaximumConnectionsPerAddress { get; set; } = 32;
    /// <summary>Selects binary WebSocket ingress instead of the native TLS framing.</summary>
    public bool UseWebSocket { get; set; }
    /// <summary>Exact HTTPS page origins, without paths or wildcards. No cookies or Authorization headers are accepted.</summary>
    public List<string> AllowedWebSocketOrigins { get; set; } = [];
}
