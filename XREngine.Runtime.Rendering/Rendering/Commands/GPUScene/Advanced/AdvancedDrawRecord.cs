using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical draw row. Every dependency is a stable generation-checked table
/// handle; no managed renderer identity is required to shade the draw.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDrawRecord
{
    public AdvancedGpuHandle Instance;
    public AdvancedGpuHandle Geometry;
    public AdvancedGpuHandle Material;
    public AdvancedGpuHandle Deformation;
    public AdvancedGpuHandle RenderState;
    public AdvancedGpuHandle EditorIdentity;
    public AdvancedGpuHandle CurrentTransform;
    public AdvancedGpuHandle PreviousTransform;
    public uint PrimitiveSection;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
}
