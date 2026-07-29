namespace XREngine;

/// <summary>
/// Selects which engine-owned common assets are included in a cooked game.
/// </summary>
public enum ECommonAssetsPackageMode
{
    /// <summary>
    /// Packages the complete common-assets library.
    /// </summary>
    Full,

    /// <summary>
    /// Packages the complete runtime shader tree and a package manifest.
    /// Use for projects that create all other runtime content themselves.
    /// </summary>
    RuntimeShaders,
}
