namespace XREngine.Data.Runtime.AotParity;

/// <summary>
/// How development builds respond when a player-path resolution succeeds only through a
/// reflective fallback that NativeAOT publication cannot provide.
/// </summary>
public enum EAotParityMode
{
    /// <summary>Fallbacks run silently. This is the default for interactive editor sessions.</summary>
    Off = 0,
    /// <summary>Each distinct type and category pair is logged once.</summary>
    Warn = 1,
    /// <summary>The first violation throws <see cref="AotParityViolationException"/>. This is the default for the unit-test lane and the parity smoke.</summary>
    Error = 2,
}
