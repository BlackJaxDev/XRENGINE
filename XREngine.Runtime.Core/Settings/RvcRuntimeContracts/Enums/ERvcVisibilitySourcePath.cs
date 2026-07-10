namespace XREngine;

/// <summary>
/// Visibility source paths used by the engine.
/// </summary>
[Flags]
public enum ERvcVisibilitySourcePath
{
    /// <summary>
    /// No visibility source paths enabled.
    /// </summary>
    None = 0,
    /// <summary>
    /// Direct visibility from static meshes.
    /// </summary>
    StaticMeshDirect = 1 << 0,
    /// <summary>
    /// Visibility from skinned compute output.
    /// </summary>
    SkinnedComputeOutput = 1 << 1,
    /// <summary>
    /// Visibility from zero readback material table.
    /// </summary>
    ZeroReadbackMaterialTable = 1 << 2,
    /// <summary>
    /// Visibility from meshlet task expansion.
    /// </summary>
    MeshletTaskExpansion = 1 << 3,
    /// <summary>
    /// Visibility from forward plus oracle.
    /// </summary>
    ForwardPlusOracle = 1 << 4,
}
