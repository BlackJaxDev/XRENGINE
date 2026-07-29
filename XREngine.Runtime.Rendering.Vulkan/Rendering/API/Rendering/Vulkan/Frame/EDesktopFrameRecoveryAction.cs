using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal enum EDesktopFrameRecoveryAction
{
    None,
    ConsumeAcquire,
    PresentRejectedImage,
    RecreateSwapchain,
    RecreateSurface,
    DeviceLost,
}

