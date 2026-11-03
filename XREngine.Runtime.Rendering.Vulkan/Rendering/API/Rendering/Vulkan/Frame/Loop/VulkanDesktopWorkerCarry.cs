using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The state that a split desktop frame carries from its first half to its second
/// half. The first half prepares the frame slot, builds the plan and starts the
/// worker recording; the second half, at the next desktop opportunity, reuses the
/// same frame slot, joins the worker, and copies, submits and presents. The frame
/// slot is not advanced between the halves, so its timeline fence covers the
/// worker's commands. One carry exists for each frame loop and it is reused.
/// </summary>
internal sealed class VulkanDesktopWorkerCarry
{
    /// <summary>True from the worker submit until the second half takes the result or discards it.</summary>
    internal bool IsActive { get; private set; }

    internal int FrameSlot { get; private set; } = -1;
    internal ulong FirstHalfFrameNumber { get; private set; }
    internal long SubmitTicks { get; private set; }

    internal VulkanAcceptedFramePlan? AcceptedPlan { get; private set; }
    internal ulong AcceptedSceneEpoch { get; private set; }
    internal ulong OutputGeneration { get; private set; }
    internal ERenderOutputReadinessPolicy ReadinessPolicy { get; private set; }
    internal ERenderOutputWorkClass WorkClass { get; private set; }
    internal int PresentNowMeshRequestCount { get; private set; }
    internal Image DesktopOffscreenImage { get; private set; }

    /// <summary>The sealed plan that the worker records; the result contract uses it.</summary>
    internal FramePlan? FramePlan { get; private set; }

    /// <summary>The primary command buffer that the worker records into.</summary>
    internal CommandBuffer PrimaryCommandBuffer { get; private set; }

    // Operation arrays whose visibility input leases stay held until the worker
    // finished recording from them.
    internal FrameOp[] StaticOperations { get; private set; } = [];
    internal int StaticOperationCount { get; private set; }
    internal FrameOp[] DynamicUiOperations { get; private set; } = [];
    internal int DynamicUiOperationCount { get; private set; }
    internal FrameOp[] TextureUploadOperations { get; private set; } = [];
    internal int TextureUploadOperationCount { get; private set; }

    /// <summary>The CPU batch, lane arena guard and join result of the worker recording.</summary>
    internal VulkanPendingDesktopRecord Record { get; } = new();

    internal void BeginFirstHalf(
        int frameSlot,
        ulong frameNumber,
        long submitTicks,
        Image desktopOffscreenImage,
        FramePlan framePlan,
        CommandBuffer primaryCommandBuffer,
        FrameOp[] staticOperations,
        int staticOperationCount,
        FrameOp[] dynamicUiOperations,
        int dynamicUiOperationCount,
        FrameOp[] textureUploadOperations,
        int textureUploadOperationCount)
    {
        if (IsActive)
            throw new InvalidOperationException("A desktop worker recording is already pending.");

        IsActive = true;
        FrameSlot = frameSlot;
        FirstHalfFrameNumber = frameNumber;
        SubmitTicks = submitTicks;
        DesktopOffscreenImage = desktopOffscreenImage;
        FramePlan = framePlan;
        PrimaryCommandBuffer = primaryCommandBuffer;
        StaticOperations = staticOperations;
        StaticOperationCount = staticOperationCount;
        DynamicUiOperations = dynamicUiOperations;
        DynamicUiOperationCount = dynamicUiOperationCount;
        TextureUploadOperations = textureUploadOperations;
        TextureUploadOperationCount = textureUploadOperationCount;
    }

    /// <summary>Takes the readiness state of the first half. The attempt must not settle the plan.</summary>
    internal void CaptureReadiness(ref VulkanFrameAttempt attempt)
    {
        AcceptedPlan = attempt.AcceptedFramePlan;
        AcceptedSceneEpoch = attempt.AcceptedSceneEpoch;
        OutputGeneration = attempt.OutputGeneration;
        ReadinessPolicy = attempt.ReadinessPolicy;
        WorkClass = attempt.WorkClass;
        PresentNowMeshRequestCount = attempt.PresentNowMeshRequestCount;
        attempt.AcceptedFramePlan = null;
    }

    /// <summary>Gives the second-half attempt the readiness state and target of the first half.</summary>
    internal void RestoreReadiness(ref VulkanFrameAttempt attempt)
    {
        attempt.AcceptedFramePlan = AcceptedPlan;
        attempt.AcceptedSceneEpoch = AcceptedSceneEpoch;
        attempt.OutputGeneration = OutputGeneration;
        attempt.ReadinessPolicy = ReadinessPolicy;
        attempt.WorkClass = WorkClass;
        attempt.PresentNowMeshRequestCount = PresentNowMeshRequestCount;
        attempt.PresentNowReadinessCompleted = true;
        attempt.UsesDesktopOffscreenTarget = true;
        attempt.DesktopOffscreenImage = DesktopOffscreenImage;
    }

    /// <summary>Clears the carry after the second half took it or after a discard.</summary>
    internal void Clear()
    {
        IsActive = false;
        FrameSlot = -1;
        FirstHalfFrameNumber = 0UL;
        SubmitTicks = 0L;
        AcceptedPlan = null;
        AcceptedSceneEpoch = 0UL;
        OutputGeneration = 0UL;
        ReadinessPolicy = default;
        WorkClass = default;
        PresentNowMeshRequestCount = 0;
        DesktopOffscreenImage = default;
        FramePlan = null;
        PrimaryCommandBuffer = default;
        StaticOperations = [];
        StaticOperationCount = 0;
        DynamicUiOperations = [];
        DynamicUiOperationCount = 0;
        TextureUploadOperations = [];
        TextureUploadOperationCount = 0;
    }
}
