using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU-owned visibility history for one stable draw in one stable view.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct AdvancedVisibilityPersistentRecord
{
    public AdvancedGpuHandle Draw;
    public ulong LastVisibleFrame;
    public ulong LastTestedFrame;
    public uint DepthPyramidGeneration;
    public EAdvancedVisibilityPreparationFlags Flags;
}
