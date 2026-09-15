namespace XREngine.Networking;

/// <summary>Observed client correction values for a bounded prediction sample.</summary>
public readonly record struct ClientPredictionMetrics(
    long CorrectionCount,
    double LastCorrectionMagnitudeMeters,
    double PeakCorrectionMagnitudeMeters);
