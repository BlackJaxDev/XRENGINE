namespace XREngine.Rendering;

/// <summary>
/// Supplies input snapshots and mouse-capture requests to a local player's viewport.
/// The source remains owned by the host that publishes its snapshots.
/// </summary>
public interface IRuntimeLocalPlayerInputSource
{
    WindowInputSnapshot ConsumeInputSnapshot();
    void RequestMouseCapture(bool captured);
}
