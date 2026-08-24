namespace XREngine.Rendering.Commands;

/// <summary>Canonical record owner for independently uploadable dirty ranges.</summary>
public enum EAdvancedGpuRecordOwner : byte
{
    Draw,
    Instance,
    Transform,
    Deformation,
    RenderState,
    Material,
    Geometry,
    EditorIdentity,
}
