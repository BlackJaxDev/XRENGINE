using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>Privileged client-launch handoff. Public directory responses never contain local package paths.</summary>
public sealed class ManagedClientLaunch
{
    public int ContractVersion { get; set; } = 1;
    public RealtimeJoinHandoffPayload Handoff { get; set; } = new();
    public WorldPackageManifest WorldPackage { get; set; } = new();
    public string PackageRootPath { get; set; } = string.Empty;
    public string CacheRootPath { get; set; } = string.Empty;
    public string WorldEntryPoint { get; set; } = string.Empty;
    public string GameBootstrapId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public ManagedAdmissionGrant? AdmissionCredential { get; set; }
}
