using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Associates DDGI dispatches with their declared render-graph pass on every backend.</summary>
public abstract class VPRC_DDGIComputePass : ViewportRenderCommand
{
    private int _passIndex = int.MinValue;
    private string? _passName;
    private string? _allocationScopeName;
    protected string PassName => _passName ??= GetType().Name;
    private string AllocationScopeName => _allocationScopeName ??= $"DDGI.{PassName}";

    protected sealed override void Execute()
    {
        if (_passIndex == int.MinValue && ParentPipeline?.PassMetadata is { } metadata)
            foreach (RenderPassMetadata pass in metadata)
                if (pass.Name == PassName)
                {
                    _passIndex = pass.PassIndex;
                    break;
                }
        if (_passIndex == int.MinValue)
        {
            Debug.RenderingWarningEvery("DDGI.MissingPassMetadata", TimeSpan.FromSeconds(2),
                "DDGI cannot submit '{0}' because its render-graph pass is missing.", PassName);
            return;
        }
        // Track only the executed command. Setup, skipped commands, and MCP
        // diagnostics remain outside this rolling, backend-neutral sample.
#if !XRE_PUBLISHED
        using var allocationScope = RuntimeRenderingHostServices.Profiling.EnableThreadAllocationTracking
            ? DDGIManagedAllocationDiagnostics.Begin(AllocationScopeName)
            : default;
#endif
        using var scope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
        ExecuteDDGI();
    }

    protected abstract void ExecuteDDGI();

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        var builder = context.GetOrCreateSyntheticPass(PassName, ERenderGraphPassStage.Compute);
        switch (PassName)
        {
            case nameof(VPRC_DDGIPrepareGeometryPass):
                builder.ReadWriteBuffer("DDGIGeometryNodes");
                builder.ReadWriteBuffer("DDGIGeometryTriangles");
                builder.ReadWriteBuffer("DDGIGeometryMaterials");
                builder.ReadWriteBuffer("DDGIGeometryAttributes");
                builder.ReadWriteTexture(MakeTextureResource("DDGIMaterialTextures"));
                break;
            case nameof(VPRC_DDGIRaygenPass):
                builder.ReadWriteBuffer(DefaultRenderPipeline.DDGIProbeStateBufferName);
                builder.WriteBuffer(DefaultRenderPipeline.DDGIRayBufferName);
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName));
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName));
                break;
            case nameof(VPRC_DDGITracePass):
                builder.ReadBuffer("DDGIGeometryMaterials");
                builder.ReadBuffer("DDGIGeometryAttributes");
                builder.SampleTexture(MakeTextureResource("DDGIMaterialTextures"));
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayBufferName);
                builder.ReadBuffer("DDGIGeometryNodes");
                builder.ReadBuffer("DDGIGeometryTriangles");
                builder.WriteBuffer(DefaultRenderPipeline.DDGIHitBufferName);
                break;
            case nameof(VPRC_DDGIHitShadePass):
                builder.ReadBuffer(DDGIResourceImports.DirectLights, ERenderPassResourceType.UniformBuffer);
                builder.ReadBuffer("DDGIGeometryAttributes");
                builder.SampleTexture(MakeTextureResource("DDGIMaterialTextures"));
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayBufferName);
                builder.ReadBuffer(DefaultRenderPipeline.DDGIHitBufferName);
                builder.ReadBuffer(DefaultRenderPipeline.DDGIProbeStateBufferName);
                builder.ReadBuffer("DDGIGeometryNodes");
                builder.ReadBuffer("DDGIGeometryTriangles");
                builder.ReadBuffer("DDGIGeometryMaterials");
                builder.WriteBuffer(DefaultRenderPipeline.DDGIRayRadianceBufferName);
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName));
                builder.SampleTexture(MakeTextureResource(DDGIEnvironmentResources.TextureName));
                break;
            case nameof(VPRC_DDGIUpdateIrradiancePass):
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayBufferName);
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayRadianceBufferName);
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName));
                break;
            case nameof(VPRC_DDGIUpdateVisibilityPass):
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayBufferName);
                builder.ReadBuffer(DefaultRenderPipeline.DDGIRayRadianceBufferName);
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName));
                break;
            case nameof(VPRC_DDGIBorderCopyPass):
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName));
                builder.ReadWriteTexture(MakeTextureResource(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName));
                break;
        }
    }
}
