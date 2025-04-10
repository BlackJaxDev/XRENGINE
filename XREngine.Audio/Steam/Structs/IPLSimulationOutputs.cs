using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Simulation results for a source. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLSimulationOutputs
{
    /** Direct path simulation results. */
    public IPLDirectEffectParams direct;

    /** Reflection simulation results. */
    public IPLReflectionEffectParams reflections;

    /** Pathing simulation results. */
    public IPLPathEffectParams pathing;
}
