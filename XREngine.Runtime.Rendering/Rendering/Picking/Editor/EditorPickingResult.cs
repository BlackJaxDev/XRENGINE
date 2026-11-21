using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>
/// Names the collected render owner for one editor pixel. A custom render owner
/// can have a render-info hit without a mesh component.
/// </summary>
public readonly record struct EditorPickingResult(
    bool IsHit,
    ulong RequestGeneration,
    BackendReadyFramePackageIdentity PackageIdentity,
    long PackageGeneration,
    uint ViewIndex,
    EditorPickingSourceKind SourceKind,
    uint SourceKey,
    RenderInfo? RenderInfo,
    RenderableMesh? RenderableMesh)
{
    public static EditorPickingResult Miss(
        ulong requestGeneration,
        BackendReadyFramePackageIdentity packageIdentity,
        long packageGeneration,
        uint viewIndex)
        => new(false, requestGeneration, packageIdentity, packageGeneration,
            viewIndex, EditorPickingSourceKind.None, 0u, null, null);
}
