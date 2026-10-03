namespace XREngine.Data.Profiling;

/// <summary>Controls an optional desktop profiler transport independently of packet collection.</summary>
public interface IProfilerTransportBackend
{
    bool IsRunning { get; }
    void Start(int port);
    void Stop();
}
