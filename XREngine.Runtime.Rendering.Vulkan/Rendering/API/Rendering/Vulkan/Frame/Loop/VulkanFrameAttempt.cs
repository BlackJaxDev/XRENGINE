using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a single attempt to render a frame in the Vulkan rendering loop. 
/// This struct encapsulates the state and resources associated with a frame attempt, 
/// including its identity, phase, flow, ownership states, timing information, and command buffers. 
/// It also tracks the results of operations such as image acquisition and presentation, 
/// as well as any failures that may occur during the frame attempt.
/// </summary>
internal ref struct VulkanFrameAttempt
{
    /// <summary>
    /// The identity of the desktop frame being attempted, 
    /// which includes information such as the frame number, slot, and start timestamp.
    /// </summary>
    public DesktopFrameIdentity Identity;
    /// <summary>
    /// The current phase of the desktop frame attempt, indicating the stage of the rendering process.
    /// </summary>
    public EDesktopFramePhase Phase;
    /// <summary>
    /// The flow of the desktop frame attempt, indicating whether the frame should continue or stop.
    /// </summary>
    public EDesktopFrameFlow Flow;
    /// <summary>
    /// The recovery action to be taken in case of a failure during the frame attempt,
    /// such as retrying the frame or skipping it.
    /// </summary>
    public EDesktopFrameRecoveryAction RecoveryAction;
    /// <summary>
    /// The reason for stopping the desktop frame attempt, which can be used for logging or debugging purposes.
    /// </summary>
    public EDesktopFrameReason Reason;
    /// <summary>
    /// The ownership state of the desktop frame attempt with respect to acquiring resources,
    /// indicating whether the frame has acquired ownership of the necessary resources for rendering.
    /// </summary>
    public EVulkanDesktopAcquireOwnership AcquireOwnership;
    /// <summary>
    /// The ownership state of the desktop frame attempt with respect to uploading resources,
    /// indicating whether the frame has ownership of the resources needed for uploading textures or other data.
    /// </summary>
    public EVulkanDesktopUploadOwnership UploadOwnership;
    /// <summary>
    /// The timing information for the desktop frame attempt, 
    /// which includes performance metrics and timestamps for various stages of the rendering process.
    /// </summary>
    public VulkanFrameTrace Timing;

    /// <summary>
    /// Indicates whether the frame attempt is currently in an interactive resize state,
    /// which may affect how the frame is rendered and presented.
    /// </summary>
    public bool InteractiveResize;
    /// <summary>
    /// Indicates the width of the framebuffer associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public int LiveFramebufferWidth;
    /// <summary>
    /// Indicates the height of the framebuffer associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public int LiveFramebufferHeight;
    /// <summary>
    /// Indicates the width of the window associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public int LiveWindowWidth;
    /// <summary>
    /// Indicates the height of the window associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public int LiveWindowHeight;
    /// <summary>
    /// Indicates the width of the surface associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public uint LiveSurfaceWidth;
    /// <summary>
    /// Indicates the height of the surface associated with the frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public uint LiveSurfaceHeight;
    /// <summary>
    /// Indicates whether the surface associated with the frame attempt is valid and can be used for rendering,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public bool LiveSurfaceValid;
    /// <summary>
    /// Indicates whether the surface associated with the frame attempt matches the swapchain,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public bool SurfaceMatchesSwapchain;
    /// <summary>
    /// Indicates whether the frame attempt can present to a swapchain with mismatched extent,
    /// which may be necessary in certain scenarios where the swapchain size does not match the expected dimensions.
    /// </summary>
    public bool CanPresentMismatchedSwapchainExtent;

    /// <summary>
    /// The index of the image in the swapchain that is being used for the current frame attempt,
    /// which is important for ensuring that the correct image is rendered and presented.
    /// </summary>
    public uint ImageIndex;
    /// <summary>
    /// The result of the image acquisition operation for the current frame attempt,
    /// which indicates whether the acquisition was successful or if there were any errors.
    /// </summary>
    public Result AcquireResult;
    /// <summary>
    /// The semaphore used for signaling the completion of the image acquisition operation for the current frame attempt,
    /// which is important for ensuring that the rendering operations are completed in the correct order.
    /// </summary>
    public Semaphore AcquireSemaphore;
    /// <summary>
    /// The semaphore used for signaling the presentation of the current frame attempt,
    /// which is important for ensuring that the rendered content is displayed at the correct time.
    /// </summary>
    public Semaphore PresentSemaphore;
    /// <summary>
    /// The lease for the frame target associated with the current frame attempt,
    /// which is used to manage the resources and ensure that they are properly released after the frame is completed.
    /// </summary>
    public VulkanFrameTargetLease FrameTargetLease;
    /// <summary>
    /// The timeline value associated with the current frame attempt,
    /// which is used for synchronization and ensuring that the rendering operations are completed in the correct order.
    /// </summary>
    public ulong AcquireTimelineValue;
    /// <summary>
    /// The value of the graphics signal associated with the current frame attempt,
    /// which is used for synchronization and ensuring that the rendering operations are completed in the correct order.
    /// </summary>
    public ulong GraphicsSignalValue;
    /// <summary>Whether the accepted graphics signal was published to both reuse ledgers.</summary>
    public bool SubmissionReuseLedgersPublished;
    /// <summary>Whether a rejected-frame recovery submission was accepted by the graphics queue.</summary>
    public bool RecoverySubmissionAccepted;
    /// <summary>Recovery command pool retained until its accepted submission is retired.</summary>
    public CommandPool RecoveryCommandPool;
    /// <summary>Recovery command buffer retained until its accepted submission is retired.</summary>
    public CommandBuffer RecoveryCommandBuffer;
    /// <summary>Whether deferred reclamation has claimed the accepted recovery command buffer.</summary>
    public bool RecoveryCommandRetirementQueued;

    /// <summary>
    /// The command buffer used for uploading textures during the current frame attempt,
    /// which is important for ensuring that the necessary resources are available for rendering.
    /// </summary>
    public CommandBuffer TextureUploadCommandBuffer;
    /// <summary>
    /// The command pool used for managing the command buffers associated with texture uploads during the current frame attempt,
    /// which is important for ensuring that the necessary resources are available for rendering.
    /// </summary>
    public CommandPool TextureUploadCommandPool;
    /// <summary>Whether the accepted upload batch was published to its completion timeline.</summary>
    public bool TextureUploadTimelinePublished;
    /// <summary>Whether command-buffer reclamation was queued for the accepted upload batch.</summary>
    public bool TextureUploadRetirementQueued;
    /// <summary>
    /// The command buffer used for rendering the scene during the current frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public CommandBuffer SceneCommandBuffer;
    /// <summary>
    /// The command buffer used for rendering the ImGui overlay during the current frame attempt,
    /// which is important for ensuring that the overlay is displayed correctly.
    /// </summary>
    public CommandBuffer ImGuiOverlayCommandBuffer;
    /// <summary>
    /// The command buffer used for rendering dynamic text overlays during the current frame attempt,
    /// which is important for ensuring that the overlays are displayed correctly.
    /// </summary>
    public CommandBuffer DynamicTextOverlayCommandBuffer;
    /// <summary>
    /// Indicates whether the ImGui overlay command buffer has been recorded for the current frame attempt.
    /// </summary>
    public bool HasImGuiOverlayCommandBuffer;
    /// <summary>
    /// Indicates whether the dynamic text overlay command buffer has been recorded for the current frame attempt.
    /// </summary>
    public bool HasDynamicTextOverlayCommandBuffer;
    /// <summary>
    /// Indicates whether the primary scene command buffer has been recorded for the current frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly.
    /// </summary>
    public bool ScenePrimaryRecordedThisFrame;
    /// <summary>
    /// Indicates whether the swapchain should be preserved for the ImGui overlay during the current frame attempt,
    /// which may be necessary in certain scenarios where the overlay needs to be rendered on top of the scene without affecting the swapchain state.
    /// </summary>
    public bool PreserveSwapchainForImGuiOverlay;
    /// <summary>
    /// Indicates the image layout of the swapchain after rendering the scene for the current frame attempt,
    /// which is important for ensuring that the rendered content is displayed correctly and that the swapchain is in the expected state for presentation.
    /// </summary>
    public ImageLayout SwapchainLayoutAfterScene;
    /// <summary>
    /// Indicates the generation of the scene command buffer that is considered dirty for the current frame attempt,
    /// which may be necessary in scenarios where the command buffer needs to be re-recorded due to changes in the scene or rendering context.
    /// </summary>
    public long SceneCommandBufferDirtyGeneration;
    /// <summary>
    /// Indicates the number of times the swapchain has been written to during scene rendering for the current frame attempt,
    /// which may be necessary in scenarios where the swapchain needs to be updated or recreated due to changes in the rendering context or surface properties.
    /// </summary>
    public int SceneSwapchainWriteCount;
    /// <summary>
    /// Indicates the number of times the swapchain has been written to during recovery operations for the current frame attempt,
    /// which may be necessary in scenarios where the swapchain needs to be updated or recreated due to changes in the rendering context or surface properties.
    /// </summary>
    public int RecoverySwapchainWriteCount;
    /// <summary>
    /// The presentation source tuple associated with the current frame attempt,
    /// which contains information about the presentation surface and swapchain used for presenting the rendered content.
    /// </summary>
    public VulkanPresentationSourceTuple PresentationSource;
    /// <summary>
    /// Immutable output-DAG manifest that admitted and ordered the command
    /// buffers owned by this attempt.
    /// </summary>
    public FramePlan? OutputExecutionPlan;
    /// <summary>The immutable scene/render epoch accepted before foreground readiness began.</summary>
    public ulong AcceptedSceneEpoch;
    /// <summary>The target generation sealed into the accepted output plan.</summary>
    public ulong OutputGeneration;
    /// <summary>The source frame whose newly submitted work was presented.</summary>
    public ulong PresentedSourceFrameId;
    /// <summary>Disposition selected by the frozen primary recorder.</summary>
    public EVulkanPrimaryCommandRecordingDisposition PrimaryRecordingDisposition;
    /// <summary>Whether the primary recorder explicitly selected a resident GPU fallback.</summary>
    public bool PrimaryRecordingUsedGpuFallback;
    /// <summary>Source-frame identity carried by the primary recording result.</summary>
    public ulong RecordingSourceFrameId;
    /// <summary>Native queue-submit result for the scene command transaction.</summary>
    public Result SubmitResult;
    /// <summary>Native queue-present result for this attempt.</summary>
    public Result PresentResult;
    /// <summary>Whether the final vkQueuePresent dispatch was issued.</summary>
    public bool PresentDispatched;
    /// <summary>Whether the present wait semaphore was verified against the acquired target lease.</summary>
    public bool PresentWaitSemaphoreProvenanceValid;
    /// <summary>Expected present wait semaphore captured from the acquired target lease.</summary>
    public Semaphore ExpectedPresentWaitSemaphore;
    /// <summary>Stage timestamps retained for the final presentation ledger.</summary>
    public long AcquireStartedTimestamp;
    public long AcquireCompletedTimestamp;
    public long RecordStartedTimestamp;
    public long RecordCompletedTimestamp;
    public long SubmitStartedTimestamp;
    public long SubmitCompletedTimestamp;
    public long PresentStartedTimestamp;
    public long PresentCompletedTimestamp;
    /// <summary>The explicit readiness contract for this desktop transaction.</summary>
    public ERenderOutputReadinessPolicy ReadinessPolicy;
    /// <summary>The explicit work class for this desktop transaction.</summary>
    public ERenderOutputWorkClass WorkClass;
    /// <summary>Whether format-independent required work completed before acquire.</summary>
    public bool PresentNowReadinessCompleted;
    /// <summary>Number of immutable mesh requests accepted by the pre-acquire barrier.</summary>
    public int PresentNowMeshRequestCount;
    /// <summary>Slot-owned logical transaction accepted before WSI acquisition.</summary>
    public VulkanAcceptedFramePlan? AcceptedFramePlan;

    /// <summary>
    /// Indicates whether the resources associated with the current frame attempt have been released,
    /// which is important for ensuring that the resources are properly managed and that there are no memory leaks or resource contention issues.
    /// </summary>
    public bool CollectReleased;
    /// <summary>
    /// Indicates whether the current frame attempt has been submitted for rendering,
    /// which is important for ensuring that the rendering operations are completed and that the rendered content is displayed correctly.
    /// </summary>
    public bool Submitted;
    /// <summary>
    /// Indicates whether the command artifacts associated with the current frame attempt have settled,
    /// which is important for ensuring that the rendering operations are completed and that the rendered content is displayed correctly.
    /// </summary>
    public bool CommandArtifactsSettled;
    /// <summary>
    /// Indicates whether the current frame attempt has been presented to the display,
    /// which is important for ensuring that the rendered content is displayed correctly and that the presentation operations are completed in the correct order.
    /// </summary>
    public bool Presented;
    /// <summary>
    /// Indicates whether the current frame attempt has completed its slot in the rendering loop,
    /// which is important for ensuring that the rendering operations are completed and that the rendered content is displayed correctly.
    /// </summary>
    public bool SlotCompleted;
    /// <summary>
    /// Indicates that the orchestration spine has claimed terminal ownership
    /// settlement. The claim is intentionally one-way: accepted native work
    /// must not be reopened by a later unwind path.
    /// </summary>
    public bool TerminalSettlementClaimed;
    /// <summary>The last typed orchestration result reached by this attempt.</summary>
    public VulkanDesktopFramePhaseResult LastPhaseResult;
    /// <summary>The one typed result published by terminal settlement.</summary>
    public VulkanDesktopFrameTerminalResult TerminalResult;
    /// <summary>Whether terminal settlement published <see cref="TerminalResult"/>.</summary>
    public bool TerminalResultPublished;
    /// <summary>
    /// Indicates whether the current frame attempt has completed its entire lifecycle,
    /// which is important for ensuring that the rendering operations are completed and that the rendered content is displayed correctly.
    /// </summary>
    public Exception? DeferredFailure;
    /// <summary>
    /// Indicates the primary failure that occurred during the current frame attempt, if any,
    /// which is important for diagnosing issues and ensuring that the rendering operations are completed correctly.
    /// </summary>
    public Exception? PrimaryFailure;

    /// <summary>
    /// Initializes a new instance of the <see cref="VulkanFrameAttempt"/> struct with the specified telemetry and identity.
    /// </summary>
    /// <param name="identity">The identity of the desktop frame, containing information such as frame number, slot, and start timestamp.</param>
    public VulkanFrameAttempt(in DesktopFrameIdentity identity)
    {
        this = default;
        Identity = identity;
        Phase = EDesktopFramePhase.Entered;
        Flow = EDesktopFrameFlow.Continue;
        ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact;
        WorkClass = ERenderOutputWorkClass.PresentNow;
    }

    /// <summary>
    /// Gets the frame number of the current frame attempt, which is used for tracking and identifying frames in the rendering loop.
    /// </summary>
    public readonly ulong FrameNumber => Identity.FrameNumber;
    /// <summary>
    /// Gets the frame slot of the current frame attempt, which is used for tracking and identifying frames in the rendering loop.
    /// </summary>
    public readonly int FrameSlot => Identity.FrameSlot;
    /// <summary>
    /// Gets the start timestamp of the current frame attempt, which is used for tracking and identifying frames in the rendering loop.
    /// </summary>
    public readonly long StartTimestamp => Identity.StartTimestamp;

    /// <summary>
    /// Advances the current frame attempt to the specified next phase, ensuring that the transition is legal according to the Vulkan desktop frame policy.
    /// </summary>
    /// <param name="next">The next phase to advance to.</param>
    /// <exception cref="InvalidOperationException">Thrown if the phase transition is illegal according to the Vulkan desktop frame policy.</exception>
    public void AdvanceTo(EDesktopFramePhase next)
    {
        if (!VulkanDesktopFramePolicy.IsLegalPhaseTransition(Phase, next))
            throw new InvalidOperationException($"Illegal desktop Vulkan frame phase transition {Phase} -> {next}.");

        Phase = next;
    }

    /// <summary>
    /// Stops the current frame attempt with the specified reason and optional recovery action, 
    /// indicating that the frame should not continue in the rendering loop.
    /// </summary>
    /// <param name="reason">The reason for stopping the frame attempt.</param>
    /// <param name="recoveryAction">The optional recovery action to take after stopping the frame attempt.</param>
    public void Stop(
        EDesktopFrameReason reason,
        EDesktopFrameRecoveryAction recoveryAction = EDesktopFrameRecoveryAction.None)
    {
        Reason = reason;
        RecoveryAction = recoveryAction;
        Flow = EDesktopFrameFlow.Stop;
    }

    /// <summary>
    /// Transitions the current frame attempt's acquire ownership state to the specified next state, 
    /// ensuring that the transition is legal according to the Vulkan desktop frame policy.
    /// </summary>
    /// <param name="next">The next acquire ownership state to transition to.</param>
    /// <exception cref="InvalidOperationException">Thrown if the acquire ownership transition is illegal according to the Vulkan desktop frame policy.</exception>
    public void TransitionAcquireOwnership(
        EVulkanDesktopAcquireOwnership next)
    {
        if (!VulkanDesktopFramePolicy.TryTransitionAcquireOwnership(AcquireOwnership, next))
            throw new InvalidOperationException($"Illegal desktop acquire ownership transition {AcquireOwnership} -> {next}.");

        AcquireOwnership = next;
    }

    /// <summary>
    /// Transitions the current frame attempt's upload ownership state to the specified next state, 
    /// ensuring that the transition is legal according to the Vulkan desktop frame policy.
    /// </summary>
    /// <param name="next">The next upload ownership state to transition to.</param>
    /// <exception cref="InvalidOperationException">Thrown if the upload ownership transition is illegal according to the Vulkan desktop frame policy.</exception>
    public void TransitionUploadOwnership(
        EVulkanDesktopUploadOwnership next)
    {
        if (!VulkanDesktopFramePolicy.TryTransitionUploadOwnership(UploadOwnership, next))
            throw new InvalidOperationException($"Illegal desktop upload ownership transition {UploadOwnership} -> {next}.");

        UploadOwnership = next;
    }

    /// <summary>
    /// Claims the one terminal settlement pass for this attempt.
    /// </summary>
    public bool TryClaimTerminalSettlement()
    {
        if (TerminalSettlementClaimed)
            return false;

        TerminalSettlementClaimed = true;
        return true;
    }

    /// <summary>Records one typed orchestration-stage result before settlement.</summary>
    public VulkanDesktopFramePhaseResult CompletePhase(
        EVulkanFrameStage stage,
        EDesktopFrameFlow flow)
    {
        if (TerminalResultPublished)
            throw new InvalidOperationException(
                $"Desktop frame stage {stage} cannot run after terminal settlement.");

        LastPhaseResult = new VulkanDesktopFramePhaseResult(stage, flow);
        return LastPhaseResult;
    }

    /// <summary>Publishes the attempt's exactly-once terminal typed result.</summary>
    public void PublishTerminalResult(
        VulkanDesktopFrameTerminalResult result)
    {
        if (TerminalResultPublished)
            throw new InvalidOperationException(
                "Desktop frame attempt already published a terminal result.");
        if (!TerminalSettlementClaimed || !result.IsValid)
            throw new InvalidOperationException(
                "Desktop frame terminal result requires a claimed settlement and a terminal outcome.");

        TerminalResult = result;
        TerminalResultPublished = true;
    }
}
