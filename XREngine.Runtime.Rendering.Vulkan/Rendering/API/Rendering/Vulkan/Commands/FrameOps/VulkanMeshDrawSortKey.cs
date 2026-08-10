namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable canonical-order fields captured once for a mesh frame operation.
/// Sorting may compare the same draw dozens of times; keeping these scalar
/// fields beside the operation avoids repeatedly traversing the large pending
/// draw snapshot and renderer/material object graph.
/// </summary>
internal readonly struct VulkanMeshDrawSortKey
{
    private VulkanMeshDrawSortKey(
        bool canCanonicalize,
        int schedulingIdentity,
        int pipelineIdentity,
        int viewportIdentity,
        int outputTargetIdentity,
        int contextKind,
        bool stereoEnabled,
        bool multiviewEnabled,
        int targetIdentity,
        bool shadowPass,
        int shadowBucket,
        int materialIdentity,
        int rendererIdentity,
        uint instanceCount,
        int billboardMode)
    {
        CanCanonicalize = canCanonicalize;
        SchedulingIdentity = schedulingIdentity;
        PipelineIdentity = pipelineIdentity;
        ViewportIdentity = viewportIdentity;
        OutputTargetIdentity = outputTargetIdentity;
        ContextKind = contextKind;
        StereoEnabled = stereoEnabled;
        MultiviewEnabled = multiviewEnabled;
        TargetIdentity = targetIdentity;
        ShadowPass = shadowPass;
        ShadowBucket = shadowBucket;
        MaterialIdentity = materialIdentity;
        RendererIdentity = rendererIdentity;
        InstanceCount = instanceCount;
        BillboardMode = billboardMode;
    }

    internal bool CanCanonicalize { get; }
    internal int SchedulingIdentity { get; }
    internal int PipelineIdentity { get; }
    internal int ViewportIdentity { get; }
    internal int OutputTargetIdentity { get; }
    internal int ContextKind { get; }
    internal bool StereoEnabled { get; }
    internal bool MultiviewEnabled { get; }
    internal int TargetIdentity { get; }
    internal bool ShadowPass { get; }
    internal int ShadowBucket { get; }
    internal int MaterialIdentity { get; }
    internal int RendererIdentity { get; }
    internal uint InstanceCount { get; }
    internal int BillboardMode { get; }

    internal static VulkanMeshDrawSortKey Capture(MeshDrawOp operation)
    {
        ref readonly PendingMeshDraw draw = ref operation.DrawRef;
        VkMeshRenderer? renderer = draw.Renderer;
        bool canCanonicalize =
            renderer is not null &&
            !draw.BlendEnabled &&
            !operation.PreserveSubmissionOrder &&
            operation.Context.PipelineInstance?.Pipeline is not
                UserInterfaceRenderPipeline;
        if (!canCanonicalize)
            return default;

        FrameOpContext context = operation.Context;
        bool shadowPass = draw.ShadowUniformState.IsShadowPass;
        int rendererIdentity = renderer!.GetHashCode();
        int materialIdentity = draw.MaterialOverride?.GetHashCode() ?? 0;
        return new VulkanMeshDrawSortKey(
            canCanonicalize: true,
            context.SchedulingIdentity,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputTargetIdentity,
            (int)context.ContextKind,
            context.StereoEnabled,
            context.MultiviewEnabled,
            operation.Target?.GetHashCode() ?? 0,
            shadowPass,
            shadowPass
                ? VulkanCommandRuntime.ResolveShadowCommandChainBucket(
                    rendererIdentity,
                    materialIdentity)
                : 0,
            materialIdentity,
            rendererIdentity,
            draw.Instances,
            (int)draw.BillboardMode);
    }
}
