using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable output target request lowered from a frame-operation context.
/// It deliberately carries both display and internal extents so an output never
/// inherits allocation dimensions from an unrelated context.
/// </summary>
internal readonly record struct OutputRequest(
    EFrameOutputKind OutputKind,
    EVrOutputViewKind ViewKind,
    ulong StableOutputId,
    ulong StableViewFamilyId,
    int OutputTargetIdentity,
    int OutputFrameBufferIdentity,
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    ulong ContextFingerprint,
    ulong ProducerDependencySetId,
    ulong ConsumerDependencySetId,
    RenderOutputRequest SchedulingRequest)
{
    /// <summary>
    /// Creates an explicit terminal output when a PresentNow transaction has
    /// no authored frame operations. The scheduling contract is still a real
    /// output declaration: it participates in the output DAG and requires the
    /// recorder to publish a fresh terminal image for this frame.
    /// </summary>
    internal static OutputRequest FromSchedulingRequest(
        in RenderOutputRequest request)
    {
        if (!request.IsDefined)
            throw new ArgumentException(
                "A synthetic output requires a defined scheduling request.",
                nameof(request));

        RenderOutputTargetDescriptor target = request.Target;
        int targetIdentity = FoldIdentity(target.StableTargetId);
        if (targetIdentity == 0)
            targetIdentity = FoldIdentity(request.OutputId);

        return new(
            request.OutputKind,
            request.ViewKind,
            request.OutputId,
            request.ViewFamilyId,
            targetIdentity,
            targetIdentity,
            ResolveContextKind(request.OutputKind),
            PipelineIdentity: 0,
            ViewportIdentity: 0,
            target.DisplayWidth,
            target.DisplayHeight,
            target.InternalWidth,
            target.InternalHeight,
            target.TargetGeneration,
            target.TargetGeneration,
            target.FormatCompatibilityKey,
            request.ProducerDependencySetId == 0UL
                ? request.OutputId
                : request.ProducerDependencySetId,
            request.ConsumerDependencySetId,
            request);
    }

    internal static OutputRequest FromContext(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind = null)
    {
        ResolveOutputIdentity(
            context,
            openXrViewKind,
            out EFrameOutputKind outputKind,
            out EVrOutputViewKind viewKind,
            out ulong outputId,
            out ulong viewFamilyId);
        return new(
            outputKind,
            viewKind,
            outputId,
            viewFamilyId,
            context.OutputTargetIdentity,
            context.OutputFrameBufferIdentity,
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.RecordingFingerprint,
            context.OutputProducerDependencySetId == 0UL
                ? outputId
                : context.OutputProducerDependencySetId,
            context.OutputConsumerDependencySetId,
            context.OutputSchedulingRequest);
    }

    internal RenderOutputRequest ToGraphRequest(
        ulong frameId,
        ERenderOutputReadinessPolicy? readinessPolicyOverride,
        ERenderOutputWorkClass? workClassOverride,
        out RenderOutputSchedulingDecision decision)
    {
        bool hasSchedulingOverride = readinessPolicyOverride.HasValue ||
            workClassOverride.HasValue;
        bool hasSchedulingSnapshot =
            RuntimeRenderingHostServices.Presentation.TryGetRenderOutputSchedulingSnapshot(
                StableOutputId,
                OutputKind,
                ViewKind,
                frameId,
                out RenderOutputSchedulingSnapshot scheduling);
        bool hasMandatoryPresentNowContract =
            SchedulingRequest.IsDefined &&
            SchedulingRequest.WorkClass == ERenderOutputWorkClass.PresentNow &&
            SchedulingRequest.ReadinessPolicy ==
                ERenderOutputReadinessPolicy.BlockForExact;
        RenderOutputRequest request = hasMandatoryPresentNowContract
            ? SchedulingRequest
            : hasSchedulingSnapshot
            ? scheduling.Request
            : SchedulingRequest.IsDefined
                ? SchedulingRequest
                : RenderOutputRequest.CreateDefault(ViewKind, OutputKind, frameId);
        if (hasSchedulingOverride)
        {
            request = request with
            {
                ReadinessPolicy = readinessPolicyOverride ??
                    request.ReadinessPolicy,
                WorkClass = workClassOverride ?? request.WorkClass,
            };
        }
        RenderOutputTargetDescriptor target = request.Target with
        {
            StableTargetId = StableOutputId,
            TargetGeneration = ResourceGeneration,
            DisplayWidth = DisplayWidth,
            DisplayHeight = DisplayHeight,
            InternalWidth = InternalWidth,
            InternalHeight = InternalHeight,
            FormatCompatibilityKey = ContextFingerprint,
        };
        RenderOutputRequest resolved = request with
        {
            OutputId = StableOutputId,
            ViewFamilyId = StableViewFamilyId,
            Target = target,
            ProducerDependencySetId = ProducerDependencySetId,
            ConsumerDependencySetId = ConsumerDependencySetId,
            FrameId = frameId,
        };
        decision = hasSchedulingOverride || hasMandatoryPresentNowContract
            ? RuntimeRenderingHostServices.Presentation.PlanRenderOutput(
                resolved,
                isDue: true,
                ERenderOutputPolicyReason.None)
            : hasSchedulingSnapshot
            ? scheduling.Decision
            : RuntimeRenderingHostServices.Presentation.PlanRenderOutput(
                resolved,
                isDue: true,
                ERenderOutputPolicyReason.None);
        return resolved;
    }

    internal static int CompareDeterministically(in OutputRequest left, in OutputRequest right)
    {
        int result = left.StableOutputId.CompareTo(right.StableOutputId);
        if (result != 0)
            return result;
        result = left.OutputKind.CompareTo(right.OutputKind);
        if (result != 0)
            return result;
        result = left.StableViewFamilyId.CompareTo(right.StableViewFamilyId);
        if (result != 0)
            return result;
        result = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
        return result != 0
            ? result
            : left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
    }

    /// <summary>
    /// Compares the output terminal identity without treating a particular
    /// operation input as a separate output publication.
    /// </summary>
    internal bool MatchesOutput(in OutputRequest other)
        => OutputKind == other.OutputKind &&
           ViewKind == other.ViewKind &&
           StableOutputId == other.StableOutputId &&
           StableViewFamilyId == other.StableViewFamilyId &&
           OutputTargetIdentity == other.OutputTargetIdentity &&
           OutputFrameBufferIdentity == other.OutputFrameBufferIdentity &&
           ContextKind == other.ContextKind &&
           PipelineIdentity == other.PipelineIdentity &&
           ViewportIdentity == other.ViewportIdentity;

    /// <summary>
    /// Compares the stable scheduling identity of this terminal with an
    /// externally required output contract. Target generations and native
    /// identities are deliberately excluded because they are rebound after a
    /// physical image is acquired.
    /// </summary>
    internal bool MatchesSchedulingContract(in RenderOutputRequest contract)
        => OutputKind == contract.OutputKind &&
           ViewKind == contract.ViewKind &&
           StableOutputId == contract.OutputId &&
           StableViewFamilyId == contract.ViewFamilyId;

    /// <summary>
    /// Promotes an authored terminal to the caller's mandatory scheduling
    /// policy without replacing its native producer identity or dataflow.
    /// </summary>
    internal OutputRequest WithSchedulingContract(
        in RenderOutputRequest contract)
        => this with { SchedulingRequest = contract };

    private static EFrameOutputKind ResolveOutputKind(EVulkanFrameOpContextKind contextKind)
        => contextKind switch
        {
            EVulkanFrameOpContextKind.OpenXrEye => EFrameOutputKind.OpenXREyeSubmit,
            EVulkanFrameOpContextKind.OpenXrMirror => EFrameOutputKind.DesktopMirror,
            EVulkanFrameOpContextKind.SceneCapture => EFrameOutputKind.SceneCapture,
            EVulkanFrameOpContextKind.LightProbeCapture => EFrameOutputKind.LightProbeCapture,
            EVulkanFrameOpContextKind.Shadow => EFrameOutputKind.Shadow,
            EVulkanFrameOpContextKind.UiPreview => EFrameOutputKind.UiPreview,
            EVulkanFrameOpContextKind.DiagnosticCapture => EFrameOutputKind.Diagnostic,
            _ => EFrameOutputKind.DesktopScene,
        };

    private static EVulkanFrameOpContextKind ResolveContextKind(
        EFrameOutputKind outputKind)
        => outputKind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                EVulkanFrameOpContextKind.OpenXrEye,
            EFrameOutputKind.DesktopMirror or EFrameOutputKind.VrPickupMirror or
                EFrameOutputKind.InWorldMirror =>
                EVulkanFrameOpContextKind.OpenXrMirror,
            EFrameOutputKind.SceneCapture =>
                EVulkanFrameOpContextKind.SceneCapture,
            EFrameOutputKind.LightProbeCapture or
                EFrameOutputKind.ReflectionProbeCapture or
                EFrameOutputKind.ImageBasedLighting =>
                EVulkanFrameOpContextKind.LightProbeCapture,
            EFrameOutputKind.Shadow => EVulkanFrameOpContextKind.Shadow,
            EFrameOutputKind.UiPreview => EVulkanFrameOpContextKind.UiPreview,
            EFrameOutputKind.Diagnostic =>
                EVulkanFrameOpContextKind.DiagnosticCapture,
            _ => EVulkanFrameOpContextKind.MainViewport,
        };

    private static int FoldIdentity(ulong identity)
        => unchecked((int)(identity ^ (identity >> 32)));

    private static EVrOutputViewKind ResolveViewKind(
        EVulkanFrameOpContextKind contextKind,
        EVrOutputViewKind? openXrViewKind)
        => contextKind == EVulkanFrameOpContextKind.OpenXrEye
            ? openXrViewKind ?? EVrOutputViewKind.LeftEye
            : contextKind == EVulkanFrameOpContextKind.OpenXrMirror && openXrViewKind.HasValue
                ? openXrViewKind.Value
            : contextKind == EVulkanFrameOpContextKind.DiagnosticCapture
                ? EVrOutputViewKind.Debug
                : EVrOutputViewKind.Secondary;

    private static ulong ComputeStableOutputId(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind)
    {
        EFrameOutputKind outputKind = ResolveOutputKind(context.ContextKind);
        EVrOutputViewKind viewKind = ResolveViewKind(context.ContextKind, openXrViewKind);
        if (context.OutputSchedulingInstanceIdentity != 0UL)
        {
            ulong contractIdentity = RenderOutputRequest.CreateDefault(
                viewKind,
                outputKind).OutputId;
            ulong canonical = 1469598103934665603UL;
            canonical = (canonical ^ contractIdentity) * 1099511628211UL;
            canonical = (canonical ^ context.OutputSchedulingInstanceIdentity) * 1099511628211UL;
            return canonical == 0UL ? 1UL : canonical;
        }

        ulong hash = 1469598103934665603UL;
        Add(ref hash, (ulong)(uint)outputKind);
        Add(ref hash, unchecked((ulong)(uint)context.OutputTargetIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputFrameBufferIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.PipelineIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.ViewportIdentity));
        Add(ref hash, (ulong)(uint)viewKind);
        // Paired OpenXR plans are target-neutral: logical view identity, not
        // an acquired image identity, keeps the two eye terminals distinct.
        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye)
            Add(ref hash, context.LogicalViewId);
        return hash == 0UL ? 1UL : hash;
    }

    private static void ResolveOutputIdentity(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind,
        out EFrameOutputKind outputKind,
        out EVrOutputViewKind viewKind,
        out ulong outputId,
        out ulong viewFamilyId)
    {
        RenderOutputRequest canonical = context.OutputSchedulingRequest;
        EFrameOutputKind inferredKind = ResolveOutputKind(context.ContextKind);
        bool acceptsCanonical = canonical.IsDefined && (context.ContextKind switch
        {
            EVulkanFrameOpContextKind.OpenXrEye =>
                canonical.OutputKind == EFrameOutputKind.OpenXREyeSubmit,
            EVulkanFrameOpContextKind.OpenXrMirror =>
                canonical.OutputKind == EFrameOutputKind.DesktopMirror,
            EVulkanFrameOpContextKind.SceneCapture =>
                canonical.OutputKind == EFrameOutputKind.SceneCapture,
            EVulkanFrameOpContextKind.LightProbeCapture =>
                canonical.OutputKind is EFrameOutputKind.LightProbeCapture or
                    EFrameOutputKind.ReflectionProbeCapture or
                    EFrameOutputKind.ImageBasedLighting,
            EVulkanFrameOpContextKind.Shadow =>
                canonical.OutputKind == EFrameOutputKind.Shadow,
            EVulkanFrameOpContextKind.UiPreview =>
                canonical.OutputKind == EFrameOutputKind.UiPreview,
            EVulkanFrameOpContextKind.DiagnosticCapture =>
                canonical.OutputKind == EFrameOutputKind.Diagnostic,
            _ => canonical.OutputKind is EFrameOutputKind.DesktopScene or
                EFrameOutputKind.EditorScenePanel,
        });
        if (acceptsCanonical)
        {
            outputKind = canonical.OutputKind;
            viewKind = canonical.ViewKind;
            outputId = canonical.OutputId;
            viewFamilyId = canonical.ViewFamilyId;
            return;
        }

        outputKind = inferredKind;
        viewKind = ResolveViewKind(context.ContextKind, openXrViewKind);
        outputId = ComputeStableOutputId(context, openXrViewKind);
        viewFamilyId = ComputeStableViewFamilyId(context, openXrViewKind);
    }

    private static ulong ComputeStableViewFamilyId(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind)
    {
        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye)
            return 0x58525F0000000001UL;

        ulong hash = 1099511628211UL;
        Add(ref hash, (ulong)(uint)ResolveOutputKind(context.ContextKind));
        Add(ref hash, unchecked((ulong)(uint)context.PipelineIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.ViewportIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputTargetIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputFrameBufferIdentity));
        return hash == 0UL ? 1UL : hash;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
