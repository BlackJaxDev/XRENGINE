using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Frozen eligibility analysis for one physical resource-plan generation.
/// Graph intervals identify candidates, not native synchronization or content
/// initialization proof. Allocation remains fail-closed in every mode until
/// those contracts and positive-path runtime validation exist.
/// </summary>
internal sealed class VulkanTransientAttachmentPlan
{
    private VulkanTransientAttachmentPlan(
        EVulkanTransientAttachmentMode mode,
        int candidateCount,
        int candidateAliasPairCount,
        int candidateLazyAllocationCount,
        string activationBlockReason)
    {
        Mode = mode;
        CandidateCount = candidateCount;
        CandidateAliasPairCount = candidateAliasPairCount;
        CandidateLazyAllocationCount = candidateLazyAllocationCount;
        ActivationBlockReason = activationBlockReason;
    }

    internal static VulkanTransientAttachmentPlan Baseline { get; } = new(
        EVulkanTransientAttachmentMode.Baseline,
        0,
        0,
        0,
        "baseline policy");

    internal EVulkanTransientAttachmentMode Mode { get; }

    internal int CandidateCount { get; }

    internal int CandidateAliasPairCount { get; }

    internal int CandidateLazyAllocationCount { get; }

    // An environment switch and ordinal pass separation are never sufficient
    // authority to share images or drop required image usages. In particular,
    // equal-layout writes still need a native alias-handoff dependency.
    internal bool IsActive => false;

    internal string ActivationBlockReason { get; }

    internal static VulkanTransientAttachmentPlan Build(
        VulkanResourcePlan resourcePlan,
        VulkanCompiledRenderGraphPlan graphPlan,
        VulkanResourcePlanner resourcePlanner,
        in FrameOpContext context,
        bool isOpenXrOrVr)
    {
        EVulkanTransientAttachmentMode mode = ResolveMode();
        var requests = new List<VulkanAllocationRequest>();
        var lifetimeByResource = new Dictionary<string, VulkanTransientAttachmentLifetimeEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (VulkanAllocationRequest request in resourcePlan.TransientTextures)
        {
            if (!request.SupportsAliasing &&
                request.TransientAttachmentPolicy != VulkanTransientAttachmentPolicy.PreferLazilyAllocated)
            {
                continue;
            }

            requests.Add(request);
            lifetimeByResource.Add(request.Name, new VulkanTransientAttachmentLifetimeEvidence());
        }

        var submissionByPass = new Dictionary<int, (int Index, ERenderGraphPassStage Queue)>();
        foreach (RenderGraphPlanSubmission submission in graphPlan.Submissions)
        {
            foreach (int passIndex in submission.PassIndices)
                submissionByPass[passIndex] = (submission.SubmissionIndex, submission.Queue);
        }

        foreach (RenderGraphPlanPass pass in graphPlan.Passes)
        {
            bool hasSubmission = submissionByPass.TryGetValue(
                pass.PassIndex,
                out (int Index, ERenderGraphPassStage Queue) submission);
            foreach (RenderGraphPlanResourceUse usage in pass.Resources)
            {
                foreach (string resourceName in ExpandLogicalResources(usage, resourcePlanner))
                {
                    if (!lifetimeByResource.TryGetValue(resourceName, out VulkanTransientAttachmentLifetimeEvidence? lifetime))
                        continue;

                    lifetime.Observe(
                        pass.Order,
                        hasSubmission ? submission.Index : -1,
                        hasSubmission && submission.Queue == ERenderGraphPassStage.Graphics,
                        usage.ResourceType,
                        usage.Imported);
                }
            }
        }

        int candidateAliasPairCount = 0;
        int candidateLazyAllocationCount = 0;
        for (int requestIndex = 0; requestIndex < requests.Count; requestIndex++)
        {
            VulkanAllocationRequest request = requests[requestIndex];
            VulkanTransientAttachmentLifetimeEvidence lifetime = lifetimeByResource[request.Name];
            if (request.TransientAttachmentPolicy == VulkanTransientAttachmentPolicy.PreferLazilyAllocated &&
                lifetime.HasUse &&
                lifetime.GraphicsQueueOnly &&
                lifetime.AttachmentOnly &&
                !lifetime.Imported)
            {
                candidateLazyAllocationCount++;
            }

            if (!request.SupportsAliasing || !lifetime.IsGraphicsQueueCandidate)
                continue;

            for (int otherIndex = requestIndex + 1; otherIndex < requests.Count; otherIndex++)
            {
                VulkanAllocationRequest other = requests[otherIndex];
                if (!other.SupportsAliasing ||
                    !request.AliasKey.Equals(other.AliasKey) ||
                    !DeclaredIntervalsDoNotOverlap(lifetime, lifetimeByResource[other.Name]))
                {
                    continue;
                }

                candidateAliasPairCount++;
            }
        }

        string blockReason = mode != EVulkanTransientAttachmentMode.ProofGated
            ? $"mode={mode}"
            : isOpenXrOrVr
                ? "OpenXR/VR planner generations remain fail-closed"
                : context.ContextKind != EVulkanFrameOpContextKind.MainViewport
                    ? $"context={context.ContextKind} is not the main viewport"
                    : "native dependency/initialization and positive-path validation pending";
        return new VulkanTransientAttachmentPlan(
            mode,
            requests.Count,
            candidateAliasPairCount,
            candidateLazyAllocationCount,
            blockReason);
    }

    internal string Describe()
        => $"mode={Mode} active={IsActive} candidates={CandidateCount} " +
           $"candidateAliasPairs={CandidateAliasPairCount} candidateLazy={CandidateLazyAllocationCount} " +
           $"block='{ActivationBlockReason}'";

    private static EVulkanTransientAttachmentMode ResolveMode()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.VulkanTransientAttachmentMode);
        if (string.IsNullOrWhiteSpace(configured))
            return EVulkanTransientAttachmentMode.Baseline;

        if (Enum.TryParse(configured, ignoreCase: true, out EVulkanTransientAttachmentMode mode) &&
            Enum.IsDefined(mode))
            return mode;

        throw new InvalidOperationException(
            $"Unsupported {XREngineEnvironmentVariables.VulkanTransientAttachmentMode} value " +
            $"'{configured}'. Expected Baseline, Analyze, or ProofGated.");
    }

    private static bool DeclaredIntervalsDoNotOverlap(
        VulkanTransientAttachmentLifetimeEvidence left,
        VulkanTransientAttachmentLifetimeEvidence right)
    {
        if (!left.IsGraphicsQueueCandidate || !right.IsGraphicsQueueCandidate)
            return false;

        bool leftBeforeRight =
            left.LastPassOrder < right.FirstPassOrder &&
            left.LastSubmissionIndex <= right.FirstSubmissionIndex;
        bool rightBeforeLeft =
            right.LastPassOrder < left.FirstPassOrder &&
            right.LastSubmissionIndex <= left.FirstSubmissionIndex;
        return leftBeforeRight || rightBeforeLeft;
    }

    private static IEnumerable<string> ExpandLogicalResources(
        RenderGraphPlanResourceUse usage,
        VulkanResourcePlanner planner)
    {
        if (!VulkanResourceBindingKey.TryParse(usage.Name, out VulkanResourceBindingKey binding))
            yield break;

        switch (binding.Kind)
        {
            case EVulkanResourceBindingKind.Output:
                if (planner.TryGetOutputFrameBufferDescriptor(out FrameBufferResourceDescriptor? output) &&
                    output is not null)
                {
                    foreach (string resourceName in ExpandFrameBufferResources(
                                 output,
                                 ResolveOutputSlot(usage.ResourceType),
                                 planner))
                    {
                        yield return resourceName;
                    }
                }
                yield break;

            case EVulkanResourceBindingKind.FrameBuffer:
                if (planner.TryGetFrameBufferDescriptor(binding.Name, out FrameBufferResourceDescriptor? frameBuffer) &&
                    frameBuffer is not null)
                {
                    foreach (string resourceName in ExpandFrameBufferResources(frameBuffer, binding.Slot, planner))
                        yield return resourceName;
                }
                yield break;

            case EVulkanResourceBindingKind.Buffer:
                yield break;

            case EVulkanResourceBindingKind.Texture:
            case EVulkanResourceBindingKind.Unqualified:
                yield return planner.ResolveImageResourceName(binding.Name);
                yield break;
        }
    }

    private static IEnumerable<string> ExpandFrameBufferResources(
        FrameBufferResourceDescriptor descriptor,
        string slot,
        VulkanResourcePlanner planner)
    {
        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (MatchesSlot(attachment.Attachment, slot) &&
                !string.IsNullOrWhiteSpace(attachment.ResourceName))
            {
                yield return planner.ResolveImageResourceName(attachment.ResourceName);
            }
        }
    }

    private static string ResolveOutputSlot(ERenderPassResourceType resourceType)
        => resourceType switch
        {
            ERenderPassResourceType.DepthAttachment => "depth",
            ERenderPassResourceType.StencilAttachment => "stencil",
            _ => "color",
        };

    private static bool MatchesSlot(EFrameBufferAttachment attachment, string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return false;

        if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Length > 5 && int.TryParse(slot.AsSpan(5), out int colorIndex))
            {
                EFrameBufferAttachment expected =
                    (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
                return attachment == expected;
            }

            return attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;
        }

        if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        return false;
    }

}
