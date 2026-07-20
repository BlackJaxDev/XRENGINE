using XREngine.Networking;

namespace XREngine;

internal sealed class EngineRuntimeNetworkDiscoveryHostServices : IRuntimeNetworkDiscoveryHostServices
{
    public object? ConfigureNetworking(NetworkDiscoveryConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GameStartupSettings startup = new()
        {
            NetworkingType = settings.NetworkingType,
            ServerIP = settings.ServerIP,
            UdpMulticastGroupIP = settings.UdpMulticastGroupIP,
            UdpMulticastPort = settings.UdpMulticastPort,
            UdpServerSendPort = settings.UdpServerSendPort,
            UdpClientRecievePort = settings.UdpClientReceivePort,
        };
        return Engine.ConfigureNetworking(startup);
    }
}
