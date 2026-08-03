using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal enum EDesktopFramePhase
{
    Entered,
    PreflightComplete,
    SlotReady,
    ImageAcquired,
    ImageReady,
    Recorded,
    Validated,
    Submitted,
    Presented,
    Recovered,
    Finalized,
}

