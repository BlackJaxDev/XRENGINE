using System;
using XREngine.Rendering.RenderGraph;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

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
    {
        get
        {
            EMeshSubmissionStrategy meshSubmissionStrategy = ResolveEffectiveMeshSubmissionStrategy();
            if (!IsMeshletRequested(meshSubmissionStrategy))
                return $"VPRC_RenderMeshesPass[{FormatRenderPassName(RenderPass)};{meshSubmissionStrategy};{PathIntent}]";

            AbstractRenderer? renderer = AbstractRenderer.Current;
            EMeshSubmissionStrategy selectedStrategy = ResolveSelectedMeshletSubmissionStrategy(renderer, meshSubmissionStrategy);
            string fallbackReason = selectedStrategy.IsAnyMeshletStrategy()
                ? "Ready"
                : renderer?.MeshletDispatchUnsupportedReason ?? "No active renderer.";

            return $"VPRC_RenderMeshesPass[{FormatRenderPassName(RenderPass)};Requested={meshSubmissionStrategy};Selected={selectedStrategy};{PathIntent};MeshletDialect={renderer?.MeshShaderDialect ?? EMeshShaderDialect.None};FallbackReason={fallbackReason}]";
        }
    }

    protected override bool ShouldExecuteThisFrame()
    {
        XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        return activeInstance?.ActiveMeshRenderCommands.HasRenderingCommands(RenderPass) == true;
    }

    protected override void Execute()
    {
        EMeshSubmissionStrategy meshSubmissionStrategy = ResolveEffectiveMeshSubmissionStrategy();
        bool forceMeshletDebugDisplay = ShouldForceMeshletDebugDisplay(meshSubmissionStrategy);
        if (forceMeshletDebugDisplay)
            meshSubmissionStrategy = ResolveMeshletDebugDisplayStrategy();

        if (IsMeshletRequested(meshSubmissionStrategy))
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer?.SupportsMeshletDispatch() == true)
            {
                VPRC_RenderMeshesPassMeshlet.Execute(this, meshSubmissionStrategy);
                return;
            }

            EMeshSubmissionStrategy fallbackStrategy = ResolveSelectedMeshletSubmissionStrategy(renderer, meshSubmissionStrategy);

            XREngine.Debug.RenderingWarningEvery(
                $"RenderMeshesPass.MeshletUnsupported.{RenderPass}",
                TimeSpan.FromSeconds(5),
                "[RenderDispatch] Meshlet submission requested for pass {0}, but production meshlet dispatch is unavailable. Fallback={1}; Dialect={2}; DirectTaskDispatch={3}; IndirectCountTaskDispatch={4}; Reason={5}.",
                RenderPass,
                fallbackStrategy,
                renderer?.MeshShaderDialect ?? EMeshShaderDialect.None,
                renderer?.SupportsDirectMeshTaskDispatch() == true,
                renderer?.SupportsIndirectCountMeshTaskDispatch() == true,
                renderer?.MeshletDispatchUnsupportedReason ?? "No active renderer.");

            meshSubmissionStrategy = fallbackStrategy;
        }

        switch (PathIntent)
        {
            case EMeshRenderingPathIntent.Traditional:
            default:
                VPRC_RenderMeshesPassTraditional.Execute(this, meshSubmissionStrategy);
                break;
        }
    }

    private EMeshSubmissionStrategy ResolveEffectiveMeshSubmissionStrategy()
    {
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.LastWindowViewport;

        return viewport?.MeshSubmissionStrategyOverride ?? MeshSubmissionStrategy;
    }

    private bool IsMeshletRequested(EMeshSubmissionStrategy meshSubmissionStrategy)
        => meshSubmissionStrategy.IsAnyMeshletStrategy() ||
           PathIntent == EMeshRenderingPathIntent.Meshlet;

    private bool ShouldForceMeshletDebugDisplay(EMeshSubmissionStrategy meshSubmissionStrategy)
    {
        if (meshSubmissionStrategy == EMeshSubmissionStrategy.CpuDirect ||
            meshSubmissionStrategy.IsAnyMeshletStrategy() ||
            !SupportsMeshletDebugDisplayPass(RenderPass))
        {
            return false;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer?.SupportsMeshletDispatch() != true)
            return false;

        XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRCamera? camera = activeInstance?.RenderState.SceneCamera
            ?? activeInstance?.RenderState.RenderingCamera
            ?? activeInstance?.LastSceneCamera
            ?? activeInstance?.LastRenderingCamera;

        return GpuBvhDebugSettings.IsMeshletDebugDisplayEnabled(camera);
    }

    private static bool SupportsMeshletDebugDisplayPass(int renderPass)
        => renderPass == (int)EDefaultRenderPass.OpaqueDeferred ||
           renderPass == (int)EDefaultRenderPass.OpaqueForward ||
           renderPass == (int)EDefaultRenderPass.MaskedForward ||
           renderPass == (int)EDefaultRenderPass.TransparentForward ||
           renderPass == (int)EDefaultRenderPass.WeightedBlendedOitForward ||
           renderPass == (int)EDefaultRenderPass.PerPixelLinkedListForward ||
           renderPass == (int)EDefaultRenderPass.DepthPeelingForward;

    private static EMeshSubmissionStrategy ResolveSelectedMeshletSubmissionStrategy(
        AbstractRenderer? renderer,
        EMeshSubmissionStrategy requestedStrategy)
    {
        if (renderer?.SupportsMeshletDispatch() == true)
        {
            return requestedStrategy.IsAnyMeshletStrategy()
                ? requestedStrategy
                : EMeshSubmissionStrategy.GpuMeshletZeroReadback;
        }

        EMeshSubmissionStrategy fallbackStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true);
        if (!fallbackStrategy.IsAnyMeshletStrategy())
            return fallbackStrategy;

        if (renderer?.SupportsIndirectCountDraw() == true)
            return EMeshSubmissionStrategy.GpuIndirectZeroReadback;

        return VulkanFeatureProfile.EnforceStrictNoFallbacks
            ? EMeshSubmissionStrategy.CpuDirect
            : EMeshSubmissionStrategy.GpuIndirectInstrumented;
    }

    private static EMeshSubmissionStrategy ResolveMeshletDebugDisplayStrategy()
        => RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging
            ? EMeshSubmissionStrategy.GpuMeshletInstrumented
            : EMeshSubmissionStrategy.GpuMeshletZeroReadback;

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
            var stencilLoad = target.ConsumeStencilLoadOp();

            builder.UseColorAttachment(
                MakeFboColorResource(target.Name),
                target.ColorAccess,
                colorLoad,
                target.GetColorStoreOp());

            UseRenderTargetDepthStencilAttachments(builder, target, depthLoad, stencilLoad);
        }
    }

    private static string FormatRenderPassName(int renderPass)
        => Enum.IsDefined(typeof(EDefaultRenderPass), renderPass)
            ? ((EDefaultRenderPass)renderPass).ToString()
            : renderPass.ToString();
}
