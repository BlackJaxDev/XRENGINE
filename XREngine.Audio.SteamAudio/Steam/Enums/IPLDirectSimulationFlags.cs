namespace XREngine.Audio.Steam;

/// <summary>
/// Flags indicating which types of direct simulation should be enabled for a given source.
/// </summary>
[Flags]
public enum IPLDirectSimulationFlags
{
    /// <summary>
    /// Enable distance attenuation calculations.
    /// </summary>
    DistanceAttenuation = 1 << 0,

    /// <summary>
    /// Enable air absorption calculations.
    /// </summary>
    AirAbsorption = 1 << 1,

    /// <summary>
    /// Enable directivity calculations.
    /// </summary>
    Directivity = 1 << 2,

    /// <summary>
    /// Enable occlusion simulation.
    /// </summary>
    Occlusion = 1 << 3,

    /// <summary>
    /// Enable transmission simulation. Requires occlusion to also be enabled.
    /// </summary>
    Transmission = 1 << 4
}