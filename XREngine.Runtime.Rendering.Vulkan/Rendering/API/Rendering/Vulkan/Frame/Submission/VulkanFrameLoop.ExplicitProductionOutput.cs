using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    // The explicit execution guard gives this renderer one transaction owner.
    // Install before collection and clear on every exit, including rejected
    // logical plans. Nested shadow/probe outputs retain their own contracts.
    private RenderOutputRequest _explicitProductionOutputContract;

    private static RenderOutputRequest CreateExplicitProductionOutputContract(
        in VulkanExplicitFrameTargetPreview preview,
        ulong frameNumber,
        bool backgroundCapture)
    {
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Secondary,
            backgroundCapture ? EFrameOutputKind.SceneCapture : EFrameOutputKind.DesktopScene,
            frameNumber);
        if (!backgroundCapture)
            return request;

        return request with
        {
            WorkClass = ERenderOutputWorkClass.Background,
            ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
            FallbackPolicy = ERenderOutputFallbackPolicy.None,
            Schedule = request.Schedule with
            {
                DesiredRateHz = 0.0f,
                MaxCpuBudgetMs = 0.0,
                MaxGpuBudgetMs = 0.0,
                MaxContentAgeFrames = 0u,
            },
            Target = request.Target with
            {
                TargetGeneration = preview.TargetGeneration,
                DisplayWidth = preview.Output.Properties.Width,
                DisplayHeight = preview.Output.Properties.Height,
                InternalWidth = preview.Output.Properties.Width,
                InternalHeight = preview.Output.Properties.Height,
                FormatCompatibilityKey =
                    ((ulong)(uint)preview.CompatibilityTarget.ImageFormat << 32) |
                    (uint)preview.CompatibilityTarget.DepthFormat,
                SampleCount = preview.Output.Properties.SampleCount,
            },
        };
    }

    /// <summary>
    /// Freezes the host's background capture contract into the ordinary scene
    /// producer context. This is output authority, independent of diagnostic
    /// flags and of any desktop viewport scheduling snapshot.
    /// </summary>
    private FrameOpContext ApplyExplicitProductionOutputContract(in FrameOpContext context)
    {
        if (!_explicitProductionOutputContract.IsDefined ||
            _explicitProductionOutputContract.WorkClass != ERenderOutputWorkClass.Background ||
            context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return context;

        return context with
        {
            ContextKind = EVulkanFrameOpContextKind.SceneCapture,
            OutputSchedulingRequest = _explicitProductionOutputContract,
            OutputSchedulingInstanceIdentity = _explicitProductionOutputContract.OutputId,
        };
    }
}
