using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Allocation-free diagnostic snapshot that resolves a draw without managed object identity.
/// Dense indices are suitable for capture dumps and GPU-side table inspection.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDrawDependencySnapshot
{
    public AdvancedGpuHandle Draw;
    public AdvancedGpuHandle Instance;
    public AdvancedGpuHandle Geometry;
    public AdvancedGpuHandle Material;
    public AdvancedGpuHandle Deformation;
    public AdvancedGpuHandle RenderState;
    public AdvancedGpuHandle EditorIdentity;
    public AdvancedGpuHandle CurrentTransform;
    public AdvancedGpuHandle PreviousTransform;
    public uint DrawDenseIndex;
    public uint InstanceDenseIndex;
    public uint GeometryDenseIndex;
    public uint DeformationDenseIndex;
    public uint RenderStateDenseIndex;
    public uint EditorIdentityDenseIndex;
    public uint CurrentTransformDenseIndex;
    public uint PreviousTransformDenseIndex;
    public EAdvancedGeometryResidency GeometryResidency;
    public uint Reserved0;
}
