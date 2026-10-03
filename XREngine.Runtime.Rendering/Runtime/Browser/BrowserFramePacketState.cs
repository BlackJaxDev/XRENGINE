namespace XREngine.Rendering;

/// <summary>Ownership states for the reusable synchronous packet arena.</summary>
internal enum BrowserFramePacketState : byte
{
    Idle,
    Writing,
    Sealed,
    Consuming,
    Faulted
}
