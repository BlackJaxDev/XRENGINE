using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal struct DesktopFrameTiming
{
    public TimeSpan WaitFrameSlot;
    public TimeSpan AcquireImage;
    public TimeSpan RecordCommandBuffer;
    public TimeSpan SnapshotImGuiOverlay;
    public TimeSpan RecordSceneCommandBuffer;
    public TimeSpan RecordImGuiOverlay;
    public TimeSpan RecordDynamicUiTextOverlay;
    public TimeSpan SubmitQueue;
    public TimeSpan TrimStaging;
    public TimeSpan PresentQueue;
    public TimeSpan SampleTimingQueries;
    public TimeSpan DrainRetiredResources;
    public TimeSpan AcquireBridgeSubmit;
    public TimeSpan WaitSwapchainImage;
    public TimeSpan ResetDynamicUniformRing;
}

