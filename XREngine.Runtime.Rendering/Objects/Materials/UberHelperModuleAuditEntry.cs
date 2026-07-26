namespace XREngine.Rendering;

/// <summary>
/// Audited ownership and reachability state for one uber shader helper file.
/// </summary>
public sealed record UberHelperModuleAuditEntry(
    string FileName,
    EUberHelperModuleStatus Status,
    bool ReachableFromCanonicalPass,
    string Ownership);
