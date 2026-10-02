namespace XREngine.Networking;

/// <summary>Cumulative same-thread allocation and wall-time samples for an opt-in networking scope.</summary>
public readonly record struct NetworkMeasurementSnapshot(long Samples, long PayloadBytes, long AllocatedBytes, double Milliseconds);
