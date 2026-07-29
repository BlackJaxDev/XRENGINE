using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Stable logical table identity. Slot zero and generation zero are reserved so
/// a zero-initialized record always contains invalid references.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuHandle(uint Index, uint Generation)
{
    public static AdvancedGpuHandle Invalid => default;

    public bool IsValid => Index != 0u && Generation != 0u;
}
