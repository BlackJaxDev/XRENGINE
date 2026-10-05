namespace XREngine.Components.Lights;

/// <summary>CPU and GPU submission stages of one exact offscreen capture.</summary>
internal enum EStagedOffscreenCaptureState : byte
{
    Idle,
    Requested,
    Collecting,
    Collected,
    Swapping,
    Published,
    Rendering,
}
