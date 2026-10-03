namespace XREngine.Data.Runtime.AotParity;

/// <summary>
/// One player-path reflective fallback observed in a development build. The fields name the
/// type, the fallback category, the owning call site, and the registration or generated
/// contract whose absence caused the fallback.
/// </summary>
public readonly record struct AotParityDiagnostic(
    string TypeFullName,
    EAotParityCategory Category,
    string CallSiteOwner,
    string Remediation)
{
    /// <summary>Formats the diagnostic as a single log line.</summary>
    public string Format()
        => $"[AotParity] {Category} for '{TypeFullName}' at {CallSiteOwner}: this resolution succeeded only through a reflective fallback that NativeAOT publication cannot provide. {Remediation}";
}
