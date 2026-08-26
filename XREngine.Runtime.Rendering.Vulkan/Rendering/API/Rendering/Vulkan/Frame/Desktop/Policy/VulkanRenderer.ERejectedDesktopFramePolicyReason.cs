namespace XREngine.Rendering.Vulkan;

internal enum ERejectedDesktopFramePolicyReason
{
    AcquireUnavailable = 0,
    DeviceLost = 1,
    ImageNeverPresented = 2,
    NoCompletedFinalWrite = 3,
    ReuseCompletedContent = 4,
    DeferredInitializationClear = 5,
    PresentNowFreshOutputRequired = 6,
}
