namespace XREngine.ControlPlane.Service;

/// <summary>Operator-selected certificate-store signing key; ordinary configuration contains no private key material.</summary>
public sealed class AdmissionSigningOptions
{
    public string Issuer { get; set; } = string.Empty;
    public string ActiveKeyId { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public bool UseMachineStore { get; set; }
    public Dictionary<string, string> PreviousVerificationKeys { get; set; } = new(StringComparer.Ordinal);
}
