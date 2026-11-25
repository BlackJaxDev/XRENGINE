using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

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
    internal bool IsImage { get; init; }
    internal UITextureSnapshot? ImageSnapshot { get; init; }
    internal XRTexture2D? ImageTexture { get; init; }
    internal bool ImageSrgb { get; init; }
    internal XRDataBuffer? ImageUvBuffer { get; init; }
    internal ulong ImageUvRevision { get; init; }
    internal bool IsText { get; init; }
    internal UITextureSnapshot? TextAtlasSnapshot { get; init; }
    internal XRTexture2D? TextAtlas { get; init; }
    internal int TextAtlasType { get; init; }
    internal float TextDistanceRange { get; init; }
    internal float TextDistanceRangeMiddle { get; init; }
    internal float TextMsdfFillBias { get; init; }
    internal int TextDebugMode { get; init; }
    internal XRDataBuffer? TextGlyphTransforms { get; init; }
    internal ulong TextGlyphTransformsRevision { get; init; }
    internal XRDataBuffer? TextGlyphTexCoords { get; init; }
    internal ulong TextGlyphTexCoordsRevision { get; init; }
    internal XRDataBuffer? TextInstances { get; init; }
    internal ulong TextInstancesRevision { get; init; }
    internal XRDataBuffer? TextGlyphIndices { get; init; }
    internal ulong TextGlyphIndicesRevision { get; init; }
    internal XRDataBuffer? TextDrawIndices { get; init; }
    internal ulong TextDrawIndicesRevision { get; init; }

    internal bool IsDefined => CanvasLifetime != 0 && Owner.IsExplicit &&
        Owner.Kind == EUICompositionLayerKind.StaticChrome && SegmentIdentity != 0 &&
        SegmentRevision != 0 && RunIdentity != 0 && CapturedMesh is not null && Intent.Renderer is not null &&
        Intent.Topology == EPrimitiveType.Triangles && Intent.IndexCount == 6 &&
        Intent.Instances != 0 && Intent.IndexOffset == 0 &&
        (IsBorder ? !IsImage && !IsText && HasCurrentBorderBuffers(Intent.Renderer) &&
                HasNoImageState && HasNoTextState :
            BorderColorBuffer is null && BorderWidthBuffer is null &&
            BorderColorRevision == 0 && BorderWidthRevision == 0 &&
            (IsImage ? !IsText && HasCurrentImageBuffer(Intent.Renderer) && HasNoTextState :
             IsText ? HasNoImageState && HasCurrentTextBuffers(Intent.Renderer) :
                HasNoImageState && HasNoTextState));

    internal bool MatchesIntent(XRMeshRenderer renderer, uint instances)
        => IsDefined && ReferenceEquals(Intent.Renderer, renderer) &&
            renderer.Mesh is { } mesh && ReferenceEquals(mesh, CapturedMesh) &&
            mesh.GeometryRevision == Intent.GeometryRevision &&
            instances == Intent.Instances;

    private bool HasNoImageState => ImageSnapshot is null && ImageTexture is null &&
        !ImageSrgb && ImageUvBuffer is null && ImageUvRevision == 0;

    private bool HasNoTextState => TextAtlasSnapshot is null && TextAtlas is null &&
        TextAtlasType == 0 && TextDistanceRange == 0 && TextDistanceRangeMiddle == 0 &&
        TextMsdfFillBias == 0 && TextDebugMode == 0 &&
        TextGlyphTransforms is null && TextGlyphTransformsRevision == 0 &&
        TextGlyphTexCoords is null && TextGlyphTexCoordsRevision == 0 &&
        TextInstances is null && TextInstancesRevision == 0 &&
        TextGlyphIndices is null && TextGlyphIndicesRevision == 0 &&
        TextDrawIndices is null && TextDrawIndicesRevision == 0;

    private bool HasCurrentTextBuffers(XRMeshRenderer renderer)
        => TextAtlasSnapshot is { } snapshot && TextAtlas is { } atlas &&
            ReferenceEquals(snapshot.Texture, atlas) &&
            renderer.Material is { } material && material.Textures.Count == 1 &&
            ReferenceEquals(material.Textures[0], atlas) &&
            HasCurrentTextUniforms(material) &&
            HasTextBuffer(renderer, "GlyphTransformsBuffer", TextGlyphTransforms, TextGlyphTransformsRevision, 0) &&
            HasTextBuffer(renderer, "GlyphTexCoordsBuffer", TextGlyphTexCoords, TextGlyphTexCoordsRevision, 1) &&
            HasTextBuffer(renderer, "TextInstanceBuffer", TextInstances, TextInstancesRevision, 2) &&
            HasTextBuffer(renderer, "GlyphTextIndexBuffer", TextGlyphIndices, TextGlyphIndicesRevision, 3) &&
            HasTextBuffer(renderer, "GlyphDrawIndexBuffer", TextDrawIndices, TextDrawIndicesRevision, 4);

    private static bool HasTextBuffer(XRMeshRenderer renderer, string name,
        XRDataBuffer? buffer, ulong revision, uint binding)
        => buffer is not null && revision != 0 && buffer.Revision == revision &&
            buffer.Target == EBufferTarget.ShaderStorageBuffer &&
            buffer.BindingIndexOverride == binding &&
            renderer.Buffers.TryGetValue(name, out XRDataBuffer? current) &&
            ReferenceEquals(current, buffer);

    private bool HasCurrentTextUniforms(XRMaterial material)
        => material.Parameter<ShaderInt>("TextAtlasType")?.Value == TextAtlasType &&
            material.Parameter<ShaderFloat>("MsdfDistanceRange")?.Value == TextDistanceRange &&
            material.Parameter<ShaderFloat>("MsdfDistanceRangeMiddle")?.Value ==
                TextDistanceRangeMiddle &&
            material.Parameter<ShaderFloat>("MsdfFillBias")?.Value == TextMsdfFillBias &&
            material.Parameter<ShaderInt>("TextDebugMode")?.Value == TextDebugMode &&
            material.Parameter<ShaderInt>("TextRenderLayer")?.Value == 0 &&
            material.Parameter<ShaderInt>("TextRenderLayer_VTX")?.Value == 0;

    private bool HasCurrentImageBuffer(XRMeshRenderer renderer)
        => ImageSnapshot is { } snapshot && ImageTexture is { } texture &&
            ReferenceEquals(snapshot.Texture, texture) &&
            renderer.Material is { } material && material.Textures.Count == 1 &&
            ReferenceEquals(material.Textures[0], texture) &&
            ImageUvBuffer is { } uv && ImageUvRevision != 0 &&
            uv.Revision == ImageUvRevision &&
            uv.Target == EBufferTarget.ShaderStorageBuffer && uv.BindingIndexOverride == 3 &&
            renderer.Buffers.TryGetValue("QuadUvBuffer", out XRDataBuffer? currentUv) &&
            ReferenceEquals(currentUv, uv);

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
