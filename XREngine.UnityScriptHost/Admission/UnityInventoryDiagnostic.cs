namespace XREngine.UnityScriptHost.Admission;

/// <summary>Describes an inspection failure or an unresolved execution requirement.</summary>
public sealed record UnityInventoryDiagnostic(string Code, string Path, string Message);
