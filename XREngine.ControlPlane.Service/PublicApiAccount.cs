namespace XREngine.ControlPlane.Service;

/// <summary>Certificate-authenticated account mapped to one configured tenant and backend identity.</summary>
public sealed class PublicApiAccount
{
    public string UserId { get; set; } = string.Empty;
    /// <summary>Current and next SHA-256 SPKI pins; deleting a pin revokes that identity at the next gateway restart.</summary>
    public List<string> CertificatePublicKeyPins { get; set; } = [];
}
