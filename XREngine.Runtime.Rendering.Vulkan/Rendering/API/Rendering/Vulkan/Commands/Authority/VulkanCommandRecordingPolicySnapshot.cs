using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Recording policy sampled once by the frame loop. Primary recording must not
/// query mutable output, planner, or renderer configuration while encoding.
/// </summary>
internal readonly record struct VulkanCommandRecordingPolicySnapshot(
    bool UseDynamicRendering,
    bool AllowSynchronousResourceUploads,
    bool FreshSerialRecording,
    bool IsExternalSwapchainTarget,
    bool PreserveSwapchainForOverlay,
    bool TransitionSwapchainToPresent,
    bool PreferKhrDynamicRendering = false,
    ImageLayout FinalTargetLayout = ImageLayout.PresentSrcKhr,
    ERenderOutputReadinessPolicy ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral,
    ERenderOutputWorkClass WorkClass = ERenderOutputWorkClass.Background,
    ulong SourceFrameId = 0UL,
    bool AllowArtifactReuse = true,
    bool AllowSecondaryDeferral = true,
    bool AllowIndirectSecondaryArtifactReuse = false,
    EVulkanQueueOverlapMode QueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly,
    bool AllowAdvancedQueueOverlap = false,
    bool InitializeOutputColor = false)
{
    /// <summary>Present-now outputs must either record fresh work or fail explicitly.</summary>
    internal bool IsPresentNow => WorkClass == ERenderOutputWorkClass.PresentNow;

    internal bool AllowsArtifactReuse => AllowArtifactReuse && !IsPresentNow &&
        !FreshSerialRecording;

    /// <summary>Allows the exact indirect packet lane without enabling primary replay.</summary>
    internal bool AllowsIndirectSecondaryArtifactReuse => !IsPresentNow &&
        !FreshSerialRecording && (AllowArtifactReuse || AllowIndirectSecondaryArtifactReuse);

    internal bool AllowsSecondaryDeferral => AllowSecondaryDeferral && !IsPresentNow;
}
