namespace XREngine.Networking;

/// <summary>
/// Engine-independent networking settings exchanged by LAN discovery and its host adapter.
/// </summary>
public sealed class NetworkDiscoveryConnectionSettings
{
    public ENetworkingType NetworkingType { get; set; } = ENetworkingType.Local;
    public string ServerIP { get; set; } = "127.0.0.1";
    public string UdpMulticastGroupIP { get; set; } = "239.0.0.222";
    public int UdpMulticastPort { get; set; } = 5000;
    public int UdpServerSendPort { get; set; } = 5000;
    public int UdpClientReceivePort { get; set; } = 5001;
}