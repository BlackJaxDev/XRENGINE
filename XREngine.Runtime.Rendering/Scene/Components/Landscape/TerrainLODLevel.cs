using System.ComponentModel;

namespace XREngine.Scene.Components.Landscape;

/// <summary>
/// Configuration for a single LOD level.
/// </summary>
public class TerrainLODLevel
{
    [Description("Distance at which this LOD starts.")]
    public float StartDistance { get; set; }

    [Description("Grid resolution divisor relative to full resolution.")]
    public int GridDivisor { get; set; } = 1;

    [Description("Whether to use vertex morphing at LOD transitions.")]
    public bool UseMorphing { get; set; } = true;

    [Description("Range over which morphing occurs (in meters).")]
    public float MorphRange { get; set; } = 10.0f;
}
