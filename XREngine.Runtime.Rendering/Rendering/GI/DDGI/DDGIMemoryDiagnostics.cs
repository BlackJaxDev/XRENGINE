using System.Numerics;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Cold-path snapshot of DDGI logical GPU payload sizes. These values describe resource content only and do not
/// represent physical driver allocations, alignment, residency, or allocator overhead.
/// </summary>
public sealed record DDGIMemoryDiagnostics(
    int CascadeCount,
    long IrradianceAtlasPayloadBytes,
    long IrradiancePayloadBytesPerCascade,
    long VisibilityAtlasPayloadBytes,
    long VisibilityPayloadBytesPerCascade,
    long ProbeStatePayloadBytes,
    long ProbeStatePayloadBytesPerCascade,
    long RayBufferPayloadBytes,
    long HitBufferPayloadBytes,
    long RadianceBufferPayloadBytes,
    long ScreenOutputPayloadBytes,
    long AdvancedSurfaceExportPayloadBytes,
    long EnvironmentPayloadBytes,
    long DirectLightBufferPayloadBytes,
    long GeometryNodesPayloadBytes,
    long GeometryTrianglesPayloadBytes,
    long GeometryMaterialsPayloadBytes,
    long GeometryAttributesPayloadBytes,
    long MaterialTexturesPayloadBytes,
    long PipelineGeometryPayloadBytes,
    long DDGILogicalPayloadBytes,
    long TotalLogicalGpuPayloadBytes)
{
    internal static DDGIMemoryDiagnostics Capture(XRRenderPipelineInstance pipeline, int cascadeCount)
    {
        cascadeCount = Math.Max(0, cascadeCount);
        long irradiance = TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName), bytesPerTexel: 4);
        long visibility = TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName), bytesPerTexel: 4);
        long probeState = BufferPayloadBytes(pipeline.GetBuffer(DefaultRenderPipeline.DDGIProbeStateBufferName));
        long rays = BufferPayloadBytes(pipeline.GetBuffer(DefaultRenderPipeline.DDGIRayBufferName));
        long hits = BufferPayloadBytes(pipeline.GetBuffer(DefaultRenderPipeline.DDGIHitBufferName));
        long radiance = BufferPayloadBytes(pipeline.GetBuffer(DefaultRenderPipeline.DDGIRayRadianceBufferName));
        long screenOutput = TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DDGITextureName), bytesPerTexel: 8);
        // Advanced native shading exports four full-resolution RGBA16F surface
        // inputs for DDGI composition: normal, albedo/opacity, RMSE, and emission.
        long advancedSurfaceExports = pipeline.Pipeline is IAdvancedRenderStageFamilyHost { AdvancedStageFamilyDefinition.UsesDDGI: true }
            ? checked(
                TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.NormalTextureName), bytesPerTexel: 8) +
                TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.AlbedoOpacityTextureName), bytesPerTexel: 8) +
                TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.RMSETextureName), bytesPerTexel: 8) +
                TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.EmissionColorTextureName), bytesPerTexel: 8))
            : 0L;
        long environment = TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DDGIEnvironmentResources.TextureName), bytesPerTexel: 8);
        long directLights = BufferPayloadBytes(pipeline.GetBuffer(DDGIResourceImports.DirectLights));
        long geometryNodes = BufferPayloadBytes(pipeline.GetBuffer(DDGIResourceImports.Nodes));
        long geometryTriangles = BufferPayloadBytes(pipeline.GetBuffer(DDGIResourceImports.Triangles));
        long geometryMaterials = BufferPayloadBytes(pipeline.GetBuffer(DDGIResourceImports.Materials));
        long geometryAttributes = BufferPayloadBytes(pipeline.GetBuffer(DDGIResourceImports.Attributes));
        long materialTextures = TexturePayloadBytes(pipeline.GetTexture<XRTexture>(DDGIResourceImports.MaterialTextures), bytesPerTexel: 8);
        long pipelineGeometry = geometryNodes + geometryTriangles + geometryMaterials + geometryAttributes + materialTextures;
        long ddgiPayload = irradiance + visibility + probeState + rays + hits + radiance + screenOutput + advancedSurfaceExports + environment + directLights;

        return new(
            cascadeCount,
            irradiance,
            DivideAcrossCascades(irradiance, cascadeCount),
            visibility,
            DivideAcrossCascades(visibility, cascadeCount),
            probeState,
            DivideAcrossCascades(probeState, cascadeCount),
            rays,
            hits,
            radiance,
            screenOutput,
            advancedSurfaceExports,
            environment,
            directLights,
            geometryNodes,
            geometryTriangles,
            geometryMaterials,
            geometryAttributes,
            materialTextures,
            pipelineGeometry,
            ddgiPayload,
            ddgiPayload + pipelineGeometry);
    }

    private static long BufferPayloadBytes(XRDataBuffer? buffer)
        => buffer is null ? 0L : buffer.Length;

    private static long TexturePayloadBytes(XRTexture? texture, int bytesPerTexel)
    {
        if (texture is null)
            return 0L;

        Vector3 dimensions = texture.WidthHeightDepth;
        long width = Math.Max(0L, (long)dimensions.X);
        long height = Math.Max(0L, (long)dimensions.Y);
        long layers = Math.Max(0L, (long)dimensions.Z);
        return checked(width * height * layers * bytesPerTexel);
    }

    private static long DivideAcrossCascades(long payloadBytes, int cascadeCount)
        => cascadeCount == 0 ? 0L : payloadBytes / cascadeCount;
}
