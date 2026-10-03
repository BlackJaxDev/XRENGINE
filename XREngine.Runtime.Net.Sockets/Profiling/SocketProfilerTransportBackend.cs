namespace XREngine.Data.Profiling;

/// <summary>Installs the desktop profiler's socket and worker lifetime.</summary>
internal sealed class SocketProfilerTransportBackend : IProfilerTransportBackend
{
    public bool IsRunning => NativeUdpProfilerSender.IsRunning;
    public void Start(int port) => NativeUdpProfilerSender.Start(port);
    public void Stop() => NativeUdpProfilerSender.Stop();
}
