using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Allocation-free resolved dependency set for one draw.
/// Material remains a stable external table handle owned by the material database.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedResolvedDrawRecords
{
    public AdvancedDrawRecord Draw;
    public AdvancedInstanceRecord Instance;
    public AdvancedGeometryRecord Geometry;
    public AdvancedTransformRecord CurrentTransform;
    public AdvancedTransformRecord PreviousTransform;
    public AdvancedDeformationRecord Deformation;
    public AdvancedRenderStateRecord RenderState;
    public AdvancedEditorIdentityRecord EditorIdentity;
    public AdvancedGpuHandle Material;
    public uint HasDeformation;
    public uint Reserved0;
}
