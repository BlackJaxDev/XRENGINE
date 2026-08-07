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
    /// <param name="telemetry">The Vulkan frame telemetry used for tracking frame timing and performance.</param>
    /// <param name="identity">The identity of the desktop frame, containing information such as frame number, slot, and start timestamp.</param>
    public VulkanFrameAttempt(VulkanFrameTelemetry telemetry, in DesktopFrameIdentity identity)
    {
        this = default;
        Identity = identity;
        Timing = telemetry.BeginFrame(identity);
        Phase = EDesktopFramePhase.Entered;
        Flow = EDesktopFrameFlow.Continue;
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
}
