using XREngine.Networking;

namespace XREngine;

public abstract partial class BaseNetworkingManager
{
    public NetworkMeasurementCounter HighRateSendMeasurements { get; } = new();
    public NetworkMeasurementCounter HighRateReceiveMeasurements { get; } = new();
    public NetworkMeasurementCounter PoseApplyMeasurements { get; } = new();
    public NetworkMeasurementCounter PoseRelayMeasurements { get; } = new();

    /// <summary>Enables opt-in synchronous scopes. Counters remain cumulative so warmup can be subtracted.</summary>
    public void SetRuntimeMeasurementsEnabled(bool enabled)
    {
        HighRateSendMeasurements.Enabled = enabled;
        HighRateReceiveMeasurements.Enabled = enabled;
        PoseApplyMeasurements.Enabled = enabled;
        PoseRelayMeasurements.Enabled = enabled;
    }
}
