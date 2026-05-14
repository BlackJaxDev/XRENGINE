using System;
using XREngine.Rendering.RenderGraph;
using XREngine.Data.Rendering;

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
[RenderPipelineScriptCommand]
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

    public VPRC_RenderMeshesPassShared(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy)
    {
        RenderPass = renderPass;
        MeshSubmissionStrategy = meshSubmissionStrategy;
    }

    private EMeshSubmissionStrategy _meshSubmissionStrategy = EMeshSubmissionStrategy.CpuDirect;
    public EMeshSubmissionStrategy MeshSubmissionStrategy
    {
        get => _meshSubmissionStrategy;
        set => SetField(ref _meshSubmissionStrategy, value);
    }

    public bool GPUDispatch
    {
        get => MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect;
        set => MeshSubmissionStrategy = value
            ? EMeshSubmissionStrategy.GpuIndirectInstrumented
            : EMeshSubmissionStrategy.CpuDirect;
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

    public void SetOptions(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy)
    {
        RenderPass = renderPass;
        MeshSubmissionStrategy = meshSubmissionStrategy;
    }

    public void SetOptions(int renderPass, bool gpuDispatch, EMeshRenderingPathIntent pathIntent)
    {
        RenderPass = renderPass;
        GPUDispatch = gpuDispatch;
        PathIntent = pathIntent;
    }

    public void SetOptions(int renderPass, EMeshSubmissionStrategy meshSubmissionStrategy, EMeshRenderingPathIntent pathIntent)
    {
        RenderPass = renderPass;
        MeshSubmissionStrategy = meshSubmissionStrategy;
        PathIntent = pathIntent;
    }

    public override string GpuProfilingName
        => $"VPRC_RenderMeshesPass[{FormatRenderPassName(RenderPass)};{MeshSubmissionStrategy};{PathIntent}]";

    protected override bool ShouldExecuteThisFrame()
    {
        XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        return activeInstance?.MeshRenderCommands.HasRenderingCommands(RenderPass) == true;
    }

    protected override void Execute()
    {
        if (MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuMeshlet ||
            PathIntent == EMeshRenderingPathIntent.Meshlet)
        {
            if (AbstractRenderer.Current?.SupportsMeshletDispatch() == true)
            {
                VPRC_RenderMeshesPassMeshlet.Execute(this);
                return;
            }

            XREngine.Debug.RenderingWarningEvery(
                $"RenderMeshesPass.MeshletUnsupported.{RenderPass}",
                TimeSpan.FromSeconds(5),
                "[RenderDispatch] Meshlet submission requested for pass {0}, but the active renderer does not support meshlet dispatch. Falling back to traditional GPU indirect submission.",
                RenderPass);
        }

        switch (PathIntent)
        {
            case EMeshRenderingPathIntent.Traditional:
            default:
                VPRC_RenderMeshesPassTraditional.Execute(this);
                break;
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        if (RenderPass < 0 && RenderPass != (int)EDefaultRenderPass.PreRender)
            return;

        string passName = PathIntent switch
        {
            EMeshRenderingPathIntent.Meshlet => $"RenderMeshesMeshlet_{RenderPass}_{MeshSubmissionStrategy}",
            _ => $"RenderMeshesTraditional_{RenderPass}_{MeshSubmissionStrategy}",
        };

        var builder = context.Metadata.ForPass(RenderPass, passName, ERenderGraphPassStage.Graphics);
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

    private static string FormatRenderPassName(int renderPass)
        => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
            ? ((EDefaultRenderPass)renderPass).ToString()
            : renderPass.ToString();
}
