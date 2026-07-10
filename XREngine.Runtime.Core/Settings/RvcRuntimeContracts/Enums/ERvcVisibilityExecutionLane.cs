namespace XREngine;

/// <summary>
/// Visibility execution lanes used by the engine.
/// </summary>
[Flags]
public enum ERvcVisibilityExecutionLane
{
    /// <summary>
    /// No visibility execution lanes enabled.
    /// </summary>
    None = 0,
    /// <summary>
    /// Hardware rasterization lane.
    /// </summary>
    HardwareRaster = 1 << 0,
    /// <summary>
    /// Meshlet compute lane.
    /// </summary>
    MeshletCompute = 1 << 1,
    /// <summary>
    /// Mesh shader lane.
    /// </summary>
    MeshShader = 1 << 2,
    /// <summary>
    /// Tiny triangle software rasterization lane.
    /// </summary>
    TinyTriangleSoftwareRaster = 1 << 3,
    /// <summary>
    /// Forward plus fallback lane.
    /// </summary>
    ForwardPlusFallback = 1 << 4,
}
