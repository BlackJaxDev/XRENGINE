using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal ref struct VulkanFrameAttempt
{
    public DesktopFrameIdentity Identity;
    public EDesktopFramePhase Phase;
    public EDesktopFrameFlow Flow;
    public EDesktopFrameRecoveryAction RecoveryAction;
    public EDesktopFrameReason Reason;
    public EVulkanDesktopAcquireOwnership AcquireOwnership;
    public EVulkanDesktopUploadOwnership UploadOwnership;
    public DesktopFrameTiming Timing;

    public bool InteractiveResize;
    public int LiveFramebufferWidth;
    public int LiveFramebufferHeight;
    public int LiveWindowWidth;
    public int LiveWindowHeight;
    public uint LiveSurfaceWidth;
    public uint LiveSurfaceHeight;
    public bool LiveSurfaceValid;
    public bool SurfaceMatchesSwapchain;
    public bool CanPresentMismatchedSwapchainExtent;

    public uint ImageIndex;
    public Result AcquireResult;
    public Semaphore AcquireSemaphore;
    public Semaphore PresentSemaphore;
    public VulkanFrameTargetLease FrameTargetLease;
    public ulong AcquireTimelineValue;
    public ulong GraphicsSignalValue;

    public CommandBuffer TextureUploadCommandBuffer;
    public CommandPool TextureUploadCommandPool;
    public CommandBuffer SceneCommandBuffer;
    public CommandBuffer ImGuiOverlayCommandBuffer;
    public CommandBuffer DynamicTextOverlayCommandBuffer;
    public bool HasImGuiOverlayCommandBuffer;
    public bool HasDynamicTextOverlayCommandBuffer;
    public bool ScenePrimaryRecordedThisFrame;
    public bool PreserveSwapchainForImGuiOverlay;
    public ImageLayout SwapchainLayoutAfterScene;
    public long SceneCommandBufferDirtyGeneration;
    public int SceneSwapchainWriteCount;
    public int RecoverySwapchainWriteCount;

    public bool CollectReleased;
    public bool Submitted;
    public bool Presented;
    public bool SlotCompleted;
    public Exception? DeferredFailure;
    public Exception? PrimaryFailure;

    public VulkanFrameAttempt(in DesktopFrameIdentity identity)
    {
        this = default;
        Identity = identity;
        Phase = EDesktopFramePhase.Entered;
        Flow = EDesktopFrameFlow.Continue;
    }

    public readonly ulong FrameNumber => Identity.FrameNumber;
    public readonly int FrameSlot => Identity.FrameSlot;
    public readonly long StartTimestamp => Identity.StartTimestamp;

    public void AdvanceTo(EDesktopFramePhase next)
    {
        if (!VulkanDesktopFramePolicy.IsLegalPhaseTransition(
                Phase,
                next))
        {
            throw new InvalidOperationException(
                $"Illegal desktop Vulkan frame phase transition {Phase} -> {next}.");
        }

        Phase = next;
    }

    public void Stop(
        EDesktopFrameReason reason,
        EDesktopFrameRecoveryAction recoveryAction = EDesktopFrameRecoveryAction.None)
    {
        Reason = reason;
        RecoveryAction = recoveryAction;
        Flow = EDesktopFrameFlow.Stop;
    }

    public void TransitionAcquireOwnership(
        EVulkanDesktopAcquireOwnership next)
    {
        if (!VulkanDesktopFramePolicy.TryTransitionAcquireOwnership(
                AcquireOwnership,
                next))
        {
            throw new InvalidOperationException(
                $"Illegal desktop acquire ownership transition {AcquireOwnership} -> {next}.");
        }

        AcquireOwnership = next;
    }

    public void TransitionUploadOwnership(
        EVulkanDesktopUploadOwnership next)
    {
        if (!VulkanDesktopFramePolicy.TryTransitionUploadOwnership(
                UploadOwnership,
                next))
        {
            throw new InvalidOperationException(
                $"Illegal desktop upload ownership transition {UploadOwnership} -> {next}.");
        }

        UploadOwnership = next;
    }
}
