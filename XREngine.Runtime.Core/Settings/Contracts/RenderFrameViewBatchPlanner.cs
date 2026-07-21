namespace XREngine;

public static partial class RenderFrameViewBatchPlanner
{
    public static RenderFrameViewBatchPlan Plan(
        in RenderFrameViewSet viewSet,
        in RenderFrameViewBatchCapabilities capabilities)
        => viewSet.RenderMode switch
        {
            EVrViewRenderMode.SinglePassStereo => PlanLayered(viewSet, capabilities),
            EVrViewRenderMode.ParallelCommandBufferRecording when capabilities.SupportsParallelCommandBufferRecording =>
                PlanOneBatchPerView(viewSet, ERenderFrameViewBatchKind.ParallelCommandBufferRecording),
            _ => PlanOneBatchPerView(viewSet, ERenderFrameViewBatchKind.SequentialView),
        };

    private static RenderFrameViewBatchPlan PlanLayered(
        in RenderFrameViewSet viewSet,
        in RenderFrameViewBatchCapabilities capabilities)
    {
        RenderFrameViewBatchPlanBuilder batches = default;
        ulong plannedMask = 0UL;

        if (capabilities.SupportsLayeredQuadView &&
            viewSet.IsQuadViewSet &&
            capabilities.MaxLayerCount >= 4 &&
            TryBuildQuadMask(viewSet, capabilities.SupportsMixedLayerExtents, out ulong quadMask))
        {
            batches.Append(new(
                ERenderFrameViewBatchKind.LayeredViewSet,
                quadMask,
                OutputLayerBase: 0,
                "quad-view layered view set",
                GetStructuralIdentity(viewSet, quadMask)));
            plannedMask |= quadMask;
        }

        if (plannedMask == 0UL && capabilities.SupportsLayeredStereoPairs && capabilities.MaxLayerCount >= 2)
        {
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftWide,
                EVrOutputViewKind.RightWide,
                "quad wide stereo pair",
                ref batches,
                ref plannedMask);
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftInset,
                EVrOutputViewKind.RightInset,
                "quad inset stereo pair",
                ref batches,
                ref plannedMask);
            TryAppendPair(
                viewSet,
                capabilities.SupportsMixedLayerExtents,
                EVrOutputViewKind.LeftEye,
                EVrOutputViewKind.RightEye,
                "stereo pair",
                ref batches,
                ref plannedMask);
        }

        ulong requiredMask = (1UL << viewSet.ViewCount) - 1UL;
        if (plannedMask != requiredMask)
        {
            throw new InvalidOperationException(
                $"Strict SinglePassStereo could not plan layered coverage for every active view. plannedMask=0x{plannedMask:X} requiredMask=0x{requiredMask:X}. Sequential fallback is forbidden.");
        }

        return batches.ToPlan();
    }

    private static RenderFrameViewBatchPlan PlanOneBatchPerView(
        in RenderFrameViewSet viewSet,
        ERenderFrameViewBatchKind kind)
    {
        RenderFrameViewBatchPlanBuilder batches = default;
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            RenderFrameViewDescriptor view = viewSet.GetView(i);
            batches.Append(new(
                kind,
                1UL << i,
                (int)view.OutputLayer,
                kind == ERenderFrameViewBatchKind.ParallelCommandBufferRecording
                    ? "parallel command-buffer recording selected"
                    : "sequential rendering selected explicitly",
                GetStructuralIdentity(viewSet, 1UL << i)));
        }

        return batches.ToPlan();
    }

    private static void TryAppendPair(
        in RenderFrameViewSet viewSet,
        bool supportsMixedLayerExtents,
        EVrOutputViewKind leftKind,
        EVrOutputViewKind rightKind,
        string debugName,
        ref RenderFrameViewBatchPlanBuilder batches,
        ref ulong plannedMask)
    {
        int left = viewSet.FindFirstView(leftKind);
        int right = viewSet.FindFirstView(rightKind);
        if (left < 0 || right < 0)
            return;

        RenderFrameViewDescriptor leftView = viewSet.GetView(left);
        RenderFrameViewDescriptor rightView = viewSet.GetView(right);
        if (!AreLayeredTargetsCompatible(leftView, rightView, supportsMixedLayerExtents))
            return;

        ulong mask = (1UL << left) | (1UL << right);
        batches.Append(new(
            ERenderFrameViewBatchKind.LayeredStereoPair,
            mask,
            Math.Min((int)leftView.OutputLayer, (int)rightView.OutputLayer),
            debugName,
            GetStructuralIdentity(viewSet, mask)));
        plannedMask |= mask;
    }

    private static bool TryBuildQuadMask(
        in RenderFrameViewSet viewSet,
        bool supportsMixedLayerExtents,
        out ulong mask)
    {
        mask = 0UL;
        int leftWide = viewSet.FindFirstView(EVrOutputViewKind.LeftWide);
        int rightWide = viewSet.FindFirstView(EVrOutputViewKind.RightWide);
        int leftInset = viewSet.FindFirstView(EVrOutputViewKind.LeftInset);
        int rightInset = viewSet.FindFirstView(EVrOutputViewKind.RightInset);
        if (leftWide < 0 || rightWide < 0 || leftInset < 0 || rightInset < 0)
            return false;

        RenderFrameViewDescriptor first = viewSet.GetView(leftWide);
        if (!AreLayeredTargetsCompatible(first, viewSet.GetView(rightWide), supportsMixedLayerExtents) ||
            !AreLayeredTargetsCompatible(first, viewSet.GetView(leftInset), supportsMixedLayerExtents) ||
            !AreLayeredTargetsCompatible(first, viewSet.GetView(rightInset), supportsMixedLayerExtents))
            return false;

        mask =
            (1UL << leftWide) |
            (1UL << rightWide) |
            (1UL << leftInset) |
            (1UL << rightInset);
        return true;
    }

    private static bool AreLayeredTargetsCompatible(
        in RenderFrameViewDescriptor first,
        in RenderFrameViewDescriptor second,
        bool supportsMixedLayerExtents)
    {
        if (!supportsMixedLayerExtents && !SameExtent(first, second))
            return false;

        RenderFrameViewTargetDescriptor firstTarget = first.Target;
        RenderFrameViewTargetDescriptor secondTarget = second.Target;
        if (!firstTarget.IsSpecified && !secondTarget.IsSpecified)
            return true;
        if (!firstTarget.IsSpecified || !secondTarget.IsSpecified)
            return false;

        return firstTarget.Backend == secondTarget.Backend &&
               firstTarget.FormatIdentity == secondTarget.FormatIdentity &&
               firstTarget.SampleCount == secondTarget.SampleCount &&
               firstTarget.SupportsColorAttachmentLayout &&
               secondTarget.SupportsColorAttachmentLayout &&
               firstTarget.SupportsTransferDestinationLayout &&
               secondTarget.SupportsTransferDestinationLayout;
    }

    public static ulong GetStructuralIdentity(in RenderFrameViewSet viewSet, ulong viewMask)
    {
        ulong validMask = (1UL << viewSet.ViewCount) - 1UL;
        if (viewMask == 0UL || (viewMask & ~validMask) != 0UL)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewMask),
                $"View mask 0x{viewMask:X} must select one or more active views from 0x{validMask:X}.");
        }
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        Add(ref hash, viewMask, prime);

        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            if ((viewMask & (1UL << i)) == 0UL)
                continue;

            RenderFrameViewDescriptor view = viewSet.GetView(i);
            RenderFrameViewTargetDescriptor target = view.Target;
            Add(ref hash, (ulong)view.Kind, prime);
            Add(ref hash, view.OutputLayer, prime);
            Add(ref hash, (ulong)(uint)view.ViewRect.Width, prime);
            Add(ref hash, (ulong)(uint)view.ViewRect.Height, prime);
            Add(ref hash, (ulong)target.Backend, prime);
            Add(ref hash, target.OutputIdentity, prime);
            Add(ref hash, target.SwapchainIdentity, prime);
            Add(ref hash, target.AttachmentSignature, prime);
            Add(ref hash, target.FormatIdentity, prime);
            Add(ref hash, target.SampleCount, prime);
            Add(ref hash, target.LayerCount, prime);
            Add(ref hash, target.ResourceGeneration, prime);
            Add(ref hash, target.TemporalGeneration, prime);
        }

        return hash == 0UL ? 1UL : hash;
    }

    private static void Add(ref ulong hash, ulong value, ulong prime)
    {
        hash ^= value;
        hash *= prime;
    }

    private static bool SameExtent(
        in RenderFrameViewDescriptor first,
        in RenderFrameViewDescriptor second)
        => first.ViewRect.Width == second.ViewRect.Width &&
           first.ViewRect.Height == second.ViewRect.Height;
}
