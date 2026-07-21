namespace XREngine.Components;

/// <summary>GPU solver families required by the component's authored trees.</summary>
[Flags]
public enum PhysicsChainGpuKernelMask : byte
{
    None = 0,
    ShortLinear = 1 << 0,
    BranchedOrLong = 1 << 1,
}
