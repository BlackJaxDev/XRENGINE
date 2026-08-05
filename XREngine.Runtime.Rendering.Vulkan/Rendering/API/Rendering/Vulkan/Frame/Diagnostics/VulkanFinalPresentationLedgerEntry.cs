using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable final-presentation evidence for one desktop frame attempt.
/// </summary>
internal readonly record struct VulkanFinalPresentationLedgerEntry(
    ulong FrameNumber,
    int FrameSlot,
    uint ImageIndex,
    ulong SwapchainGeneration,
    ulong SwapchainHandle,
    uint SwapchainWidth,
    uint SwapchainHeight,
    int LiveFramebufferWidth,
    int LiveFramebufferHeight,
    bool InteractiveResize,
    string? SourceName,
    uint SourceWidth,
    uint SourceHeight,
    bool SourceReady,
    ulong SourceDescriptorEpoch,
    ulong SourceDescriptorGeneration,
    ulong SourceImage,
    ulong SourceView,
    ulong SourceSampler,
    ImageLayout SourceTrackedLayout,
    VulkanFinalPresentationDescriptorObservation Descriptor,
    ulong SceneCommandBuffer,
    ulong SceneCommandRecordingGeneration,
    bool ScenePrimaryRecordedThisFrame,
    ulong CommandPlannerRevision,
    ulong CommandFrameOpContextId,
    ulong CommandResourceGeneration,
    ulong CommandDescriptorGeneration,
    long CommandDirtyGeneration,
    int SceneSwapchainWriteCount,
    int RecoverySwapchainWriteCount,
    bool HadValidPriorSwapchainContent,
    bool HasImGuiOverlay,
    bool HasDynamicTextOverlay,
    Result PresentResult,
    bool PresentAccepted,
    bool HasValidFrameContent,
    bool InvariantFailed,
    string? InvariantFailure);
