using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

public enum EMeshRenderingPathIntent
{
    Traditional = 0,
    Meshlet = 1,
}

/// <summary>
/// Shared router entry point for mesh rendering command paths.
/// Callers configure one command and do not encode traditional/meshlet details.
/// </summary>
public class VPRC_RenderMeshesPassShared : ViewportPopStateRenderCommand
{
    public VPRC_RenderMeshesPassShared()
    {
    }

    public VPRC_RenderMeshesPassShared(int renderPass, bool gpuDispatch)
    {
        RenderPass = renderPass;
        GPUDispatch = gpuDispatch;
    }

    private bool _gpuDispatch;
    public bool GPUDispatch
    {
        get => _gpuDispatch;
        set => SetField(ref _gpuDispatch, value);
    }

    private int _renderPass;
    public int RenderPass
    {
        get => _renderPass;
        set => SetField(ref _renderPass, value);
    }

    private EMeshRenderingPathIntent _pathIntent = EMeshRenderingPathIntent.Traditional;
    public EMeshRenderingPathIntent PathIntent
    {
        get => _pathIntent;
        set => SetField(ref _pathIntent, value);
    }

    public void SetOptions(int renderPass, bool gpuDispatch)
    {
        RenderPass = renderPass;
        GPUDispatch = gpuDispatch;
    }

    public void SetOptions(int renderPass, bool gpuDispatch, EMeshRenderingPathIntent pathIntent)
    {
        RenderPass = renderPass;
        GPUDispatch = gpuDispatch;
        PathIntent = pathIntent;
    }

    protected override void Execute()
    {
        switch (PathIntent)
        {
            case EMeshRenderingPathIntent.Meshlet:
                VPRC_RenderMeshesPassMeshlet.Execute(this);
                break;
            case EMeshRenderingPathIntent.Traditional:
            default:
                VPRC_RenderMeshesPassTraditional.Execute(this);
                break;
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        if (RenderPass < 0)
            return;

        string passName = PathIntent switch
        {
            EMeshRenderingPathIntent.Meshlet => $"RenderMeshesMeshlet_{RenderPass}",
            _ => $"RenderMeshesTraditional_{RenderPass}",
        };

        var builder = context.Metadata.ForPass(RenderPass, passName, RenderGraphPassStage.Graphics);
        builder
            .UseEngineDescriptors()
            .UseMaterialDescriptors();

        if (context.CurrentRenderTarget is { } target)
        {
            builder.WithName($"{passName}_{target.Name}");
            var colorLoad = target.ConsumeColorLoadOp();
            var depthLoad = target.ConsumeDepthLoadOp();

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                colorLoad,
                target.GetColorStoreOp());

            builder.UseDepthAttachment(
                MakeFboDepthResource(target.Name),
                target.DepthAccess,
                depthLoad,
                target.GetDepthStoreOp());
        }
    }
}
