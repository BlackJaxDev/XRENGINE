using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// GPU-uploadable dense-index relocation for one stable logical handle.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuHandleRemap(
    AdvancedGpuHandle Handle,
    uint PreviousDenseIndex,
    uint CurrentDenseIndex)
{
    public const uint InvalidDenseIndex = uint.MaxValue;

    public bool RemovesRecord => CurrentDenseIndex == InvalidDenseIndex;
}
