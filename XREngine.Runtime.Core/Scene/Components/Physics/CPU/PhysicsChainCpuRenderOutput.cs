using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Allocation-free view over stable world-owned CPU render output. Multiple
/// renderers may retain the same memory slices until structural re-registration.
/// </summary>
public readonly record struct PhysicsChainCpuRenderOutput(
    ReadOnlyMemory<Matrix4x4> CurrentPalette,
    ReadOnlyMemory<Matrix4x4> PreviousPalette,
    PhysicsChainCpuBounds Bounds,
    PhysicsChainCpuConsumerFlags Consumers,
    long SimulationFrame,
    uint OutputGeneration,
    int TransformMirrorAgeFrames,
    long TransformMirrorCostTicks)
{
    public bool HasPalette => (Consumers & PhysicsChainCpuConsumerFlags.Palette) != 0 && !CurrentPalette.IsEmpty;
    public bool HasBounds => (Consumers & PhysicsChainCpuConsumerFlags.Bounds) != 0 && Bounds.IsValid;
    public bool HasValidHistory => HasPalette && PreviousPalette.Length == CurrentPalette.Length;
}
