using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Info;

namespace XREngine.Rendering;

/// <summary>Retains the exact collected owner of one stable mesh command key.</summary>
internal readonly record struct EditorPickingSnapshotRow(
    uint StableQueryKey,
    RenderInfo? RenderInfo,
    RenderableMesh? RenderableMesh);
