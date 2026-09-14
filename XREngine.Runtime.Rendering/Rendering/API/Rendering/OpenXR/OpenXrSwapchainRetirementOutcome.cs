namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Distinguishes safe admission deferral from failure after child resources changed.</summary>
public enum OpenXrSwapchainRetirementOutcome : byte
{
    Unsupported,
    DeferredBeforeDetachment,
    Retired,
    FailedAfterDetachment,
}
