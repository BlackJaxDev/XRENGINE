namespace XREngine.ControlPlane;

/// <summary>Detached ES256 signature over canonical admission claims, including the bearer proof hash and endpoint.</summary>
public sealed class ManagedAdmissionSignature
{
    public string KeyId { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; set; }
    public string Value { get; set; } = string.Empty;
}
