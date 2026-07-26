namespace XREngine.Rendering;

/// <summary>
/// Reachability classification for a source file in the canonical uber shader tree.
/// </summary>
public enum EUberHelperModuleStatus
{
    Active,
    Partial,
    Dormant,
    Obsolete,
    Reusable,
}
