using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>Privileged, versioned configuration sent only to a newly assigned worker.</summary>
public sealed class ManagedWorkerLaunch
{
    public int ContractVersion { get; set; } = 1;
    public string OperationId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid Generation { get; set; }
    public string ManagementUrl { get; set; } = string.Empty;
    public string ManagementToken { get; set; } = string.Empty;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int BindPort { get; set; }
    public RealtimeEndpointDescriptor AdvertisedEndpoint { get; set; } = new();
    public int MaxPlayers { get; set; }
    public WorldPackageManifest WorldPackage { get; set; } = new();
    /// <summary>Private host-local source/staging root; never serialized for public clients.</summary>
    public string PackageRootPath { get; set; } = string.Empty;
    public string WorldEntryPoint { get; set; } = string.Empty;
    public string GameBootstrapId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public ManagedWorkerStartupConfiguration Startup { get; set; } = new();
    public ManagedWorkerResourceLimits ResourceLimits { get; set; } = new();
    public ManagedWorkerTlsConfiguration? Tls { get; set; }
    public string AdmissionIssuer { get; set; } = string.Empty;
    /// <summary>Trusted key IDs and base64 DER SubjectPublicKeyInfo. Omit only for explicit local development.</summary>
    public Dictionary<string, string> AdmissionSigningKeys { get; set; } = new(StringComparer.Ordinal);
}
