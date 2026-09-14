namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Describes whether active swapchains remain usable after a cleanup attempt.</summary>
internal enum OpenXrSwapchainCleanupOutcome : byte
{
    Completed,
    DeferredBeforeDetachment,
    FailedAfterDetachment,
}
