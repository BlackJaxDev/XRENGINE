namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral capabilities of the final output acquired for one frame.
/// These describe host policy, not graphics-API features or native handles.
/// </summary>
[Flags]
public enum RenderFrameOutputCapabilities
{
    None = 0,
    Presentation = 1 << 0,
    DesktopOverlays = 1 << 1,
    ExternallyOwnedImages = 1 << 2,
    HiddenAreaMask = 1 << 3,
    /// <summary>The final attachment's sample count does not override the scene's authored internal AA samples.</summary>
    IndependentSceneSamples = 1 << 4,
}
