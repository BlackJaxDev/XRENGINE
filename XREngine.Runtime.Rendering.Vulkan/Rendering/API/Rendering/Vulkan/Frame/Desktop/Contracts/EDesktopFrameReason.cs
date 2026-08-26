using System;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal enum EDesktopFrameReason
{
    None,
    ZeroSurface,
    ResizePending,
    ResourceGenerationBlocked,
    FrameGenerationModeChanged,
    FrameSlotBusy,
    AcquireNotReady,
    AcquireTimeout,
    AcquireOutOfDate,
    AcquireSurfaceLost,
    AcquireDeviceLost,
    AcquireUnexpectedFailure,
    RecordingDeferred,
    RecordingResourceRetired,
    RecordingFailed,
    OverlayRecordingFailed,
    RecordingDirtied,
    PresentNowReadinessFailed,
    SubmitFailed,
    PresentOutOfDate,
    PresentSuboptimal,
    PresentSurfaceLost,
    PresentDeviceLost,
    PresentUnexpectedFailure,
    Success,
}

