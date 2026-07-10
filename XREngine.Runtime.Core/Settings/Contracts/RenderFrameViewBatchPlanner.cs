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
                "quad-view layered view set"));
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

        AppendUnplannedSequentialViews(viewSet, ref batches, plannedMask);
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
                view.DebugName));
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
        if (!supportsMixedLayerExtents && !SameExtent(leftView, rightView))
            return;

        ulong mask = (1UL << left) | (1UL << right);
        batches.Append(new(
            ERenderFrameViewBatchKind.LayeredStereoPair,
            mask,
            Math.Min((int)leftView.OutputLayer, (int)rightView.OutputLayer),
            debugName));
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
        if (!supportsMixedLayerExtents &&
            (!SameExtent(first, viewSet.GetView(rightWide)) ||
             !SameExtent(first, viewSet.GetView(leftInset)) ||
             !SameExtent(first, viewSet.GetView(rightInset))))
            return false;

        mask =
            (1UL << leftWide) |
            (1UL << rightWide) |
            (1UL << leftInset) |
            (1UL << rightInset);
        return true;
    }

    private static void AppendUnplannedSequentialViews(
        in RenderFrameViewSet viewSet,
        ref RenderFrameViewBatchPlanBuilder batches,
        ulong plannedMask)
    {
        for (int i = 0; i < viewSet.ViewCount; i++)
        {
            ulong mask = 1UL << i;
            if ((plannedMask & mask) != 0UL)
                continue;

            RenderFrameViewDescriptor view = viewSet.GetView(i);
            batches.Append(new(
                ERenderFrameViewBatchKind.SequentialView,
                mask,
                (int)view.OutputLayer,
                view.DebugName));
        }
    }

    private static bool SameExtent(
        in RenderFrameViewDescriptor first,
        in RenderFrameViewDescriptor second)
        => first.ViewRect.Width == second.ViewRect.Width &&
           first.ViewRect.Height == second.ViewRect.Height;
}
