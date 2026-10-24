using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Describes one retained intrinsic quad run independently of its current output receipt.</summary>
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
    internal bool IsBorder { get; init; }
    internal XRDataBuffer? BorderColorBuffer { get; init; }
    internal ulong BorderColorRevision { get; init; }
    internal XRDataBuffer? BorderWidthBuffer { get; init; }
    internal ulong BorderWidthRevision { get; init; }

    internal bool IsDefined => CanvasLifetime != 0 && Owner.IsExplicit &&
        Owner.Kind == EUICompositionLayerKind.StaticChrome && SegmentIdentity != 0 &&
        SegmentRevision != 0 && RunIdentity != 0 && CapturedMesh is not null && Intent.Renderer is not null &&
        Intent.Topology == EPrimitiveType.Triangles && Intent.IndexCount == 6 &&
        Intent.Instances != 0 && Intent.IndexOffset == 0 &&
        (IsBorder ? HasCurrentBorderBuffers(Intent.Renderer) :
            BorderColorBuffer is null && BorderWidthBuffer is null &&
            BorderColorRevision == 0 && BorderWidthRevision == 0);

    internal bool MatchesIntent(XRMeshRenderer renderer, uint instances)
        => IsDefined && ReferenceEquals(Intent.Renderer, renderer) &&
            renderer.Mesh is { } mesh && ReferenceEquals(mesh, CapturedMesh) &&
            mesh.GeometryRevision == Intent.GeometryRevision &&
            instances == Intent.Instances &&
            (!IsBorder || HasCurrentBorderBuffers(renderer));

    private bool HasCurrentBorderBuffers(XRMeshRenderer renderer)
        => BorderColorBuffer is { } color && BorderWidthBuffer is { } width &&
            !ReferenceEquals(color, width) &&
            BorderColorRevision != 0 && BorderWidthRevision != 0 &&
            color.Revision == BorderColorRevision && width.Revision == BorderWidthRevision &&
            color.Target == EBufferTarget.ShaderStorageBuffer && color.BindingIndexOverride == 3 &&
            width.Target == EBufferTarget.ShaderStorageBuffer && width.BindingIndexOverride == 4 &&
            renderer.Buffers.TryGetValue("QuadUvBuffer", out XRDataBuffer? currentColor) &&
            ReferenceEquals(currentColor, color) &&
            renderer.Buffers.TryGetValue("QuadBorderWidthBuffer", out XRDataBuffer? currentWidth) &&
            ReferenceEquals(currentWidth, width);
}
