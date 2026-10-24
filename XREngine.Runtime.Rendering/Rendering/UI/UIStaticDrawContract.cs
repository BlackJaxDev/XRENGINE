using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Describes one retained solid run independently of its current output receipt.</summary>
internal readonly record struct UIStaticDrawContract(
    ulong CanvasLifetime,
    UICompositionOwner Owner,
    ulong SegmentIdentity,
    ulong SegmentRevision,
    ulong RunIdentity,
    long BackendGeneration,
    UIIndexedDrawIntent Intent)
{
    internal XRMesh? CapturedMesh { get; init; }

    internal bool IsDefined => CanvasLifetime != 0 && Owner.IsExplicit &&
        Owner.Kind == EUICompositionLayerKind.StaticChrome && SegmentIdentity != 0 &&
        SegmentRevision != 0 && RunIdentity != 0 && CapturedMesh is not null && Intent.Renderer is not null &&
        Intent.Topology == EPrimitiveType.Triangles && Intent.IndexCount == 6 &&
        Intent.Instances != 0 && Intent.IndexOffset == 0;

    internal bool MatchesIntent(XRMeshRenderer renderer, uint instances)
        => IsDefined && ReferenceEquals(Intent.Renderer, renderer) &&
            renderer.Mesh is { } mesh && ReferenceEquals(mesh, CapturedMesh) &&
            mesh.GeometryRevision == Intent.GeometryRevision &&
            instances == Intent.Instances;
}
