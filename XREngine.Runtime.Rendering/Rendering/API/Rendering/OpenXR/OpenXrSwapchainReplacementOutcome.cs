namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Describes the point reached by an in-session OpenXR swapchain replacement.
/// </summary>
internal enum OpenXrSwapchainReplacementOutcome
{
    /// <summary>The active generation was retained because detachment is not yet safe.</summary>
    DeferredBeforeDetachment,

    /// <summary>A complete replacement generation was created in the current session.</summary>
    Replaced,

    /// <summary>The prior generation was detached, but a complete replacement could not be created.</summary>
    FailedAfterDetachment,
}
