namespace XREngine.Rendering.Commands;

/// <summary>
/// Logical source of vertex data. Visibility payloads retain the same geometry
/// handle and primitive meaning regardless of this encoding.
/// </summary>
public enum EAdvancedGeometrySource : uint
{
    Static = 0u,
    PreSkinnedCurrentAndPrevious = 1u,
    MeshletLocal = 2u,
}
