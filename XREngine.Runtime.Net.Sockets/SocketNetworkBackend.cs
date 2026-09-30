using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Profiling;

namespace XREngine.Networking;

/// <summary>Installs socket transport, profiler, and native networking component capabilities.</summary>
public static class SocketNetworkBackend
{
    public static void Register()
    {
        NetworkTransportServices.Current = new SocketNetworkTransportBackend();
        UdpProfilerSender.Backend = new SocketProfilerTransportBackend();
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new TcpClientComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new TcpServerComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new UdpSocketComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new NetworkDiscoveryComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new FaceMotion3DCaptureComponent());
    }
}
