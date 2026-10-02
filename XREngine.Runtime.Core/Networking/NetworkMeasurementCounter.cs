using System.Diagnostics;

namespace XREngine.Networking;

/// <summary>Retains allocation-free aggregate measurements; subtract snapshots to select a warm interval.</summary>
public sealed class NetworkMeasurementCounter
{
    private readonly object _sync = new();
    private long _samples, _payloadBytes, _allocatedBytes, _ticks;
    private volatile bool _enabled;

    public bool Enabled { get => _enabled; set => _enabled = value; }

    internal NetworkMeasurementScope Begin(int payloadBytes)
        => _enabled ? new(this, payloadBytes) : default;

    internal void Record(int payloadBytes, long allocatedBytes, long ticks)
    {
        lock (_sync)
        {
            _samples++;
            _payloadBytes += payloadBytes;
            _allocatedBytes += allocatedBytes;
            _ticks += ticks;
        }
    }

    public NetworkMeasurementSnapshot Snapshot()
    {
        lock (_sync)
            return new(_samples, _payloadBytes, _allocatedBytes, _ticks * 1000.0 / Stopwatch.Frequency);
    }
}
