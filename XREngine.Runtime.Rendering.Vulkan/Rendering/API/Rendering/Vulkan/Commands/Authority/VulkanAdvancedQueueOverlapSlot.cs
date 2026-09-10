using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retained native artifacts for one frame slot of the presentationless advanced
/// queue-overlap schedule. The frame loop owns recording and sets
/// <see cref="Recorded"/> only after every primary has been sealed.
/// </summary>
internal sealed class VulkanAdvancedQueueOverlapSlot
{
    internal VulkanAdvancedQueueOverlapSlot(uint frameSlot)
        => FrameSlot = frameSlot;

    internal uint FrameSlot { get; }
    internal CommandPool Pool;
    internal CommandBuffer Final;
    internal CommandBuffer Prefix;
    internal CommandBuffer Classification;
    internal CommandBuffer Independent;
    internal Semaphore Ready;
    internal Semaphore Classified;
    internal Fence PrefixFence;
    internal Fence ClassificationFence;
    internal Fence IndependentFence;
    internal Fence FinalFence;
    internal int AmbientOcclusionOperation;
    internal int ClassificationOperation;
    internal int NativeOpaqueOperation;
    internal bool Recorded;
    internal bool PrefixAccepted;
    internal bool ClassificationAccepted;
    internal bool IndependentAccepted;
    internal bool FinalAccepted;

    internal bool HasAcceptedPrefixes
        => PrefixAccepted || ClassificationAccepted || IndependentAccepted;

    internal void ResetForRecording(CommandBuffer final)
    {
        Final = final;
        AmbientOcclusionOperation = -1;
        ClassificationOperation = -1;
        NativeOpaqueOperation = -1;
        Recorded = false;
        FinalAccepted = false;
    }
}
