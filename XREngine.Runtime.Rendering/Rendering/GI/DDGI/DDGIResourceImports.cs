using XREngine.Rendering.Resources;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>Declares separately owned DDGI inputs in each pipeline's immutable resource layout.</summary>
internal static class DDGIResourceImports
{
    public const string Nodes = "DDGIGeometryNodes";
    public const string Triangles = "DDGIGeometryTriangles";
    public const string Materials = "DDGIGeometryMaterials";
    public const string Attributes = "DDGIGeometryAttributes";
    public const string MaterialTextures = "DDGIMaterialTextures";
    private static readonly string[] BufferNames = [Nodes, Triangles, Materials, Attributes];

    public static void Declare(RenderPipelineResourceLayoutBuilder builder, RenderPipelineResourcePredicate predicate)
    {
        for (int i = 0; i < BufferNames.Length; i++)
            builder.External(BufferNames[i])
                .Contract(ExternalRenderResourceKind.Buffer, ExternalRenderResourceOwnership.Caller,
                    ExternalRenderResourceSynchronization.FrameBoundary)
                .When(predicate).Add();
        builder.External(MaterialTextures)
            .Contract(ExternalRenderResourceKind.Texture, ExternalRenderResourceOwnership.Caller,
                ExternalRenderResourceSynchronization.FrameBoundary)
            .When(predicate).Add();
    }

    public static bool BindAvailable(XRRenderPipelineInstance pipeline, GpuDdgiGeometryService geometry)
    {
        bool published = true;
        if (geometry.Nodes is { } nodes)
            published &= BindBuffer(pipeline, nodes, Nodes);
        else
            published = false;
        if (geometry.Triangles is { } triangles)
            published &= BindBuffer(pipeline, triangles, Triangles);
        else
            published = false;
        if (geometry.Materials is { } materials)
            published &= BindBuffer(pipeline, materials, Materials);
        else
            published = false;
        if (geometry.Attributes is { } attributes)
            published &= BindBuffer(pipeline, attributes, Attributes);
        else
            published = false;
        if (geometry.MaterialTextures is { } texture)
            published &= pipeline.BindImportedTexture(texture);
        else
            published = false;
        return published;
    }

    private static bool BindBuffer(XRRenderPipelineInstance pipeline, XRDataBuffer buffer, string name)
    {
        if (buffer.AttributeName != name)
            buffer.AttributeName = name;
        return pipeline.BindImportedBuffer(buffer);
    }
}
