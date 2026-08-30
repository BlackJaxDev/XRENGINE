namespace XREngine.Rendering.Commands;

/// <summary>Canonical reverse edges retained with a sealed publication.</summary>
public enum EAdvancedReverseDependencyKind : byte
{
    MaterialToDraw,
    GeometryToDraw,
    TextureToMaterial,
    KernelToMaterial,
    LayoutToMaterial,
}
