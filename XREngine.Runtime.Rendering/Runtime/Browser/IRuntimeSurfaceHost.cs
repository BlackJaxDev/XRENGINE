namespace XREngine.Rendering;

/// <summary>Receives surface and input snapshots and schedules frames without platform dependencies.</summary>
public interface IRuntimeSurfaceHost
{
    /// <summary>The latest host-owned surface snapshot.</summary>
    RuntimeSurfaceState Surface { get; }

    /// <summary>The latest input snapshot, separate from renderer presentation properties.</summary>
    RuntimeInputState Input { get; }

    /// <summary>Publishes a new surface snapshot.</summary>
    void UpdateSurface(RuntimeSurfaceState surface);

    /// <summary>Publishes a new input snapshot.</summary>
    void UpdateInput(RuntimeInputState input);

    /// <summary>Discards the previous accepted frame timestamp after a pause or clock change.</summary>
    void ResetFrameClock();

    /// <summary>Accepts a renderable frame when enough time has elapsed since the last accepted frame.</summary>
    bool TryBeginFrame(double timestampMilliseconds, double minimumFrameIntervalMilliseconds, out double deltaSeconds);
}
