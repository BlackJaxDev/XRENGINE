using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Stable references to immutable source geometry and frame-slot deformation outputs.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDeformationRecord
{
    public AdvancedGpuHandle SourceGeometry;
    public AdvancedGpuHandle CurrentGeometry;
    public AdvancedGpuHandle PreviousGeometry;
    public AdvancedGpuHandle Animation;
    public uint CurrentFrameSlot;
    public uint PreviousFrameSlot;
    public uint VertexCount;
    public uint Flags;
}
