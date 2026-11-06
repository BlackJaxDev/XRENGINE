namespace XREngine.Cutscenes;

/// <summary>
/// The level of detail of an asset that is resident in memory.
/// The value of each tier is its level index: 0 is T0, 1 is T1, and 2 is T2.
/// </summary>
public enum EResidencyLevel
{
    /// <summary>No data of the asset is resident.</summary>
    None = -1,

    /// <summary>T0: the proxy. It stays resident while the cutscene is open.</summary>
    Proxy = 0,

    /// <summary>T1: the coarse level, for example a mid LOD or mips up to about 512 px.</summary>
    Coarse = 1,

    /// <summary>T2: the target level that the manifest records.</summary>
    Target = 2,
}
