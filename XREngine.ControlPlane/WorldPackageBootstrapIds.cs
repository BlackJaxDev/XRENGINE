namespace XREngine.ControlPlane;

/// <summary>
/// Compiled-in game bootstrap identities that a <see cref="WorldPackageManifest"/> may select.
/// A bootstrap id is a lookup key into the player binary and never refers to package content.
/// </summary>
public static class WorldPackageBootstrapIds
{
    /// <summary>
    /// Package-authored world bootstrap: a <c>CustomGameMode</c> with no default pawn where realtime
    /// admission creates remote pawns. This is the only bootstrap registered by current builds.
    /// </summary>
    public const string BuiltInWorldV1 = "world-v1";

    /// <summary>Returns true when the id names a bootstrap compiled into this build.</summary>
    public static bool IsCompiledIn(string? gameBootstrapId)
        => string.Equals(gameBootstrapId, BuiltInWorldV1, StringComparison.Ordinal);
}
