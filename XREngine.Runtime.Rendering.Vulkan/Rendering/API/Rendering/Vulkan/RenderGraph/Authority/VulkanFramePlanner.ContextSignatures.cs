using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal sealed partial class VulkanFramePlanner
{
    internal static bool FrameOpContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null || context.PassMetadata is { Count: > 0 };

    internal static bool ResolveFrameOpContextStereoEnabled(in FrameOpContext context)
        => context.PipelineInstance?.RenderState.StereoPass
            ?? RuntimeEngine.Rendering.State.IsStereoPass;

    internal static bool ResolveFrameOpContextShadowPass(in FrameOpContext context)
        => context.PipelineInstance?.RenderState.ShadowPass
            ?? RuntimeEngine.Rendering.State.IsShadowPass;

    internal static bool ResolveFrameOpContextMultiviewEnabled(
        in FrameOpContext context,
        bool stereoEnabled)
    {
        if (!stereoEnabled)
            return false;

        string pipelineTypeName = context.PipelineInstance?.AssignedPipeline?.GetType().Name ?? string.Empty;
        return pipelineTypeName.Contains("MultiView", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("Multiview", StringComparison.OrdinalIgnoreCase);
    }

    internal static VulkanFrameOpPlannerStateKey BuildFrameOpPlannerStateKey(in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            context.LogicalViewId,
            ResolveFrameOpContextResourceRegistrySignature(context),
            ComputePassMetadataSignature(context.PassMetadata),
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.SubmissionQueueFamily);

    internal static VulkanInteractiveResizePlannerContextKey BuildInteractiveResizePlannerContextKey(
        in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context));

    /// <summary>
    /// Returns the physical-plan identity for an output. Command recording continues to use the
    /// concrete target identity, but rotating desktop target/FBO instances must not manufacture a
    /// new allocator owner when their pipeline, named attachment contract, and extent are compatible.
    /// </summary>
    internal static int ResolveResourcePlanOutputTargetIdentity(in FrameOpContext context)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return context.OutputTargetIdentity;

        if (context.OutputFrameBufferIdentity != 0)
            return context.OutputFrameBufferIdentity;

        return HashCode.Combine(
            (int)context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity);
    }

    internal static bool FrameOpMatchesPlannerStateKey(FrameOp op, in VulkanFrameOpPlannerStateKey key)
        => FrameOpContextHasPlannerResources(op.Context) &&
            FrameOpContextMatchesPlannerStateKey(op.Context, key);

    internal static bool FrameOpContextMatchesPlannerStateKey(in FrameOpContext context, in VulkanFrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            context.LogicalViewId == key.LogicalViewId &&
            ResolveFrameOpContextResourceRegistrySignature(context) == key.ResourceRegistrySignature &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.DescriptorGeneration == key.DescriptorGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    internal static bool FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            context.LogicalViewId == key.LogicalViewId &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.DescriptorGeneration == key.DescriptorGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    internal static int ComputeOutputFrameBufferIdentity(string? outputFrameBufferName)
        => string.IsNullOrWhiteSpace(outputFrameBufferName)
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(outputFrameBufferName!);

    internal static int ResolveFrameOpContextResourceRegistrySignature(in FrameOpContext context)
        => context.ResourceRegistrySignatureSnapshot ?? ComputeResourceRegistrySignature(context.ResourceRegistry);

    internal static ulong ResolveFrameOpContextDescriptorGeneration(RenderResourceRegistry? registry)
        => unchecked((ulong)(uint)ComputeResourceRegistrySignature(registry));

    internal static FrameOpContext RefreshFrameOpContextRecordingFingerprint(in FrameOpContext context)
        => context with { RecordingFingerprint = ComputeFrameOpContextRecordingFingerprint(context) };

    internal static ulong ComputeFrameOpContextRecordingFingerprint(in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D4F50435458UL);
        hash.Add((int)context.ContextKind);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputFrameBufferIdentity);
        hash.Add(context.OutputTargetIdentity);
        hash.Add(context.LogicalViewId);
        hash.Add(context.OutputTargetName);
        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        hash.Add(ResolveFrameOpContextResourceRegistrySignature(context));
        hash.Add(ComputePassMetadataSignature(context.PassMetadata));
        hash.Add(context.ResourceGeneration);
        hash.Add(context.DescriptorGeneration);
        hash.Add(context.SubmissionQueueFamily);
        return hash.ToHash();
    }


    internal static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)
    {
        FrameOpContext fallback = ops[0].Context;
        FrameOpContext best = fallback;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            FrameOpContext context = op.Context;
            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanSwapchainContextCoalescer.TargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return best;
    }

    internal static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops, in VulkanFrameOpPlannerStateKey key)
    {
        FrameOpContext best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            FrameOpContext context = op.Context;
            if (!hasBest)
            {
                best = context;
                hasBest = true;
            }

            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanSwapchainContextCoalescer.TargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return hasBest ? best : SelectPrimaryPlannerContext(ops);
    }

    internal static int ScoreFrameOpFrameBufferTargets(FrameOp op, RenderResourceRegistry registry)
    {
        int score = ScoreFrameOpFrameBufferTarget(op.Context.OutputFrameBuffer, registry);
        score += ScoreFrameOpFrameBufferTarget(op.Target, registry);
        if (op is BlitOp blit)
        {
            score += ScoreFrameOpFrameBufferTarget(blit.InFbo, registry);
            score += ScoreFrameOpFrameBufferTarget(blit.OutFbo, registry);
        }

        return score;
    }

    internal static int ScoreFrameOpFrameBufferTarget(XRFrameBuffer? target, RenderResourceRegistry registry)
    {
        if (target is null)
            return 0;

        return !string.IsNullOrWhiteSpace(target.Name) &&
            registry.FrameBufferRecords.ContainsKey(target.Name)
                ? 256
                : 32;
    }

    internal static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
    {
        int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
        if (compare != 0)
            return compare;

        compare = ((int)left.ContextKind).CompareTo((int)right.ContextKind);
        if (compare != 0)
            return compare;

        compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
        if (compare != 0)
            return compare;

        compare = ResolveFrameOpContextResourceRegistrySignature(left)
            .CompareTo(ResolveFrameOpContextResourceRegistrySignature(right));
        if (compare != 0)
            return compare;

        compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
        if (compare != 0)
            return compare;

        compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
        if (compare != 0)
            return compare;

        compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
        if (compare != 0)
            return compare;

        compare = left.DescriptorGeneration.CompareTo(right.DescriptorGeneration);
        if (compare != 0)
            return compare;

        return ComputePassMetadataSignature(left.PassMetadata).CompareTo(ComputePassMetadataSignature(right.PassMetadata));
    }

    internal static uint ResolvePositiveDimension(uint? primary, int? secondary, uint tertiary, uint fallback)
    {
        if (primary.HasValue && primary.Value > 0)
            return primary.Value;

        if (secondary.HasValue && secondary.Value > 0)
            return (uint)secondary.Value;

        return tertiary > 0 ? tertiary : fallback;
    }

    internal static (uint DisplayWidth, uint DisplayHeight, uint InternalWidth, uint InternalHeight) ResolveExternalFrameOpResourceDimensions(
        in Extent2D externalExtent,
        uint? pipelineInternalWidth,
        uint? pipelineInternalHeight,
        int? viewportInternalWidth,
        int? viewportInternalHeight,
        uint contextInternalWidth = 0u,
        uint contextInternalHeight = 0u)
    {
        uint displayWidth = Math.Max(externalExtent.Width, 1u);
        uint displayHeight = Math.Max(externalExtent.Height, 1u);
        uint internalWidth = ResolvePositiveDimension(
            pipelineInternalWidth,
            viewportInternalWidth,
            contextInternalWidth,
            displayWidth);
        uint internalHeight = ResolvePositiveDimension(
            pipelineInternalHeight,
            viewportInternalHeight,
            contextInternalHeight,
            displayHeight);

        return (displayWidth, displayHeight, internalWidth, internalHeight);
    }
}
