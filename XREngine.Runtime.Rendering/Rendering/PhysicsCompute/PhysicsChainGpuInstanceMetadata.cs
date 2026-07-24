using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Compact resident scheduling state for one physics-chain instance.
/// Layout matches <c>PhysicsChainActiveWork.comp</c> under std430.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsChainGpuInstanceMetadata
{
    public uint Enabled;
    public uint Relevant;
    public uint SleepState;
    public uint QualityTier;
    public uint Cadence;
    public uint Phase;
    public uint LoopCount;
    public uint FeatureMask;
}
