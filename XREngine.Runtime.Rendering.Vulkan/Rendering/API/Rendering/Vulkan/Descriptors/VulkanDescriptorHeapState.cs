namespace XREngine.Rendering.Vulkan;

/// <summary>Owns descriptor-heap capability, storage, dirty ranges, and counters.</summary>
internal sealed class VulkanDescriptorHeapState
{
    internal VulkanDescriptorHeapNativeFunctions? NativeFunctions;
    internal VulkanDescriptorHeapStorage SamplerStorage;
    internal VulkanDescriptorHeapStorage ResourceStorage;
    internal EVulkanDescriptorBackend ActiveBackend = EVulkanDescriptorBackend.DescriptorSets;
    internal string FallbackReason = "Vulkan logical device is not initialized.";
    internal bool FeatureSupported;
    internal bool CaptureReplaySupported;
    internal bool ShaderUntypedPointersAvailable;
    internal bool NativeApiAvailable;
    internal bool StorageReady;
    internal PhysicalDeviceDescriptorHeapPropertiesEXTNative Properties;
    internal ulong SamplerHighWaterBytes;
    internal ulong ResourceHighWaterBytes;
    internal ulong SamplerWriteCount;
    internal ulong ResourceWriteCount;
    internal ulong SamplerBindCount;
    internal ulong ResourceBindCount;
    internal ulong CopyCount;
    internal ulong CopyBytes;
    internal ulong AllocationFailureCount;
    internal ulong SamplerDirtyStart = ulong.MaxValue;
    internal ulong SamplerDirtyEnd;
    internal ulong ResourceDirtyStart = ulong.MaxValue;
    internal ulong ResourceDirtyEnd;
    internal ulong FrameNumber;
    internal ulong FrameWrites;
    internal ulong FrameCopies;
    internal ulong LastFrameWrites;
    internal ulong LastFrameCopies;

    internal void BeginFrame(ulong frameNumber)
    {
        if (FrameNumber == frameNumber)
            return;

        LastFrameWrites = FrameWrites;
        LastFrameCopies = FrameCopies;
        FrameWrites = 0;
        FrameCopies = 0;
        FrameNumber = frameNumber;
    }
}
