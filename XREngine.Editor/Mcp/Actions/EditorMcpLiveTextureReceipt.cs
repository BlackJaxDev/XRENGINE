using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Editor.Mcp;

/// <summary>Retains exact live objects for a guarded texture restore.</summary>
internal sealed record EditorMcpLiveTextureReceipt(
    SceneNode Node,
    ModelComponent Component,
    SubMesh Submesh,
    SubMeshLOD Lod,
    int SubmeshIndex,
    int LodIndex,
    XRMaterial Material,
    bool Legacy,
    int BindingIndex,
    XRTexture? Previous,
    XRTexture? Replacement,
    MaterialSurfaceTextureBinding? PreviousSurface,
    MaterialSurfaceTextureBinding? ReplacementSurface,
    StandardLitTextureBindingEdit? PairedEdit);
