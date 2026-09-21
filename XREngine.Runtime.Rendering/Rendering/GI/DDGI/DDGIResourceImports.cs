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
    public const string DirectLights = "DDGILightBlock";
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
        builder.External(DirectLights)
            .Contract(ExternalRenderResourceKind.Buffer, ExternalRenderResourceOwnership.Caller,
                ExternalRenderResourceSynchronization.FrameBoundary)
            .When(predicate).Add();
    }

    public static void BindDirectLights(XRRenderPipelineInstance pipeline, XRDataBuffer buffer)
        => BindBuffer(pipeline, buffer, DirectLights);

    public static void BindAvailable(XRRenderPipelineInstance pipeline, GpuDdgiGeometryService geometry)
    {
        if (geometry.Nodes is { } nodes)
            BindBuffer(pipeline, nodes, Nodes);
        if (geometry.Triangles is { } triangles)
            BindBuffer(pipeline, triangles, Triangles);
        if (geometry.Materials is { } materials)
            BindBuffer(pipeline, materials, Materials);
        if (geometry.Attributes is { } attributes)
            BindBuffer(pipeline, attributes, Attributes);
        if (geometry.MaterialTextures is { } texture &&
            (!pipeline.Resources.TryGetTexture(MaterialTextures, out XRTexture? current) || !ReferenceEquals(current, texture)))
            pipeline.BindImportedTexture(texture);
    }

    private static void BindBuffer(XRRenderPipelineInstance pipeline, XRDataBuffer buffer, string name)
    {
        if (buffer.AttributeName != name)
            buffer.AttributeName = name;
        if (pipeline.Resources.BufferRecords.TryGetValue(name, out RenderBufferResource? record) &&
            ReferenceEquals(record.Instance, buffer) && record.Descriptor.SizeInBytes == buffer.Length)
            return;
        pipeline.BindImportedBuffer(buffer);
    }
}
