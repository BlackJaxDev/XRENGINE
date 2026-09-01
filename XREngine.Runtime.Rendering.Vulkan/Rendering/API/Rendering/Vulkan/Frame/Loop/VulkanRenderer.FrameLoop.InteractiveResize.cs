namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Runs one non-blocking scene presentation attempt for a modal Windows resize
    /// callback. The active swapchain generation remains fixed while WSI scales its
    /// completed images; work that is not already recordable defers through the normal
    /// rejected-image recovery path.
    /// </summary>
    private void RunInteractiveResizeDesktopFramePhases(
        ref VulkanFrameAttempt attempt)
    {
        long phaseStarted = BeginDesktopFramePhase(
            EVulkanFrameStage.ResourcePrepare);
        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.ResourcePrepare,
                PrepareInteractiveResizeSceneAttempt(ref attempt),
                phaseStarted).ShouldContinue)
        {
            return;
        }

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.OutputAcquire);
        EDesktopFrameFlow acquireFlow = AcquireDesktopSwapchainImageCore(
            ref attempt);
        if (acquireFlow != EDesktopFrameFlow.Continue)
        {
            _ = CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.OutputAcquire,
                acquireFlow,
                phaseStarted);
            return;
        }

        PrepareAcquiredDesktopImage(ref attempt);
        if (!CompleteDesktopFramePhase(
                ref attempt,
                EVulkanFrameStage.OutputAcquire,
                acquireFlow,
                phaseStarted).ShouldContinue)
        {
            return;
        }

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.CommandRecord);
        VulkanDesktopFramePhaseResult recordResult = RecordDesktopFrame(
            ref attempt);
        CompleteDesktopFramePhaseTiming(
            ref attempt,
            EVulkanFrameStage.CommandRecord,
            phaseStarted);
        if (!recordResult.ShouldContinue)
            return;

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.QueueSubmit);
        VulkanDesktopFramePhaseResult submitResult = SubmitDesktopFrame(
            ref attempt);
        CompleteDesktopFramePhaseTiming(
            ref attempt,
            EVulkanFrameStage.QueueSubmit,
            phaseStarted);
        if (!submitResult.ShouldContinue)
            return;

        phaseStarted = BeginDesktopFramePhase(EVulkanFrameStage.OutputComplete);
        _ = CompleteDesktopFramePhase(
            ref attempt,
            EVulkanFrameStage.OutputComplete,
            PresentSubmittedDesktopFrame(ref attempt),
            phaseStarted);
    }

    private static EDesktopFrameFlow PrepareInteractiveResizeSceneAttempt(
        ref VulkanFrameAttempt attempt)
    {
        // Resize callbacks must never enter PresentNow's foreground convergence
        // loop. RecordDesktopFrame owns snapshot consumption and all failure cleanup.
        attempt.ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral;
        attempt.WorkClass = ERenderOutputWorkClass.Background;
        return EDesktopFrameFlow.Continue;
    }
}
