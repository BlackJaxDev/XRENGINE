using System.Diagnostics;

namespace XREngine.Networking;

/// <summary>A synchronous scope; never retain it across an await or a thread handoff.</summary>
internal readonly struct NetworkMeasurementScope : IDisposable
{
    private readonly NetworkMeasurementCounter? _counter;
    private readonly int _payloadBytes;
    private readonly long _allocatedBytes, _timestamp;

    internal NetworkMeasurementScope(NetworkMeasurementCounter counter, int payloadBytes)
    {
        _counter = counter;
        _payloadBytes = payloadBytes;
        _allocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _timestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_counter is { } counter)
            counter.Record(_payloadBytes, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytes, Stopwatch.GetTimestamp() - _timestamp);
    }
}
