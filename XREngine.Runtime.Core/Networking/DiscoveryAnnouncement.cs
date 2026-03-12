namespace XREngine.Components;

/// <summary>
/// Payload broadcast during discovery.
/// </summary>
public class DiscoveryAnnouncement
{
    public string Magic { get; set; } = "XRENGINE-DISCOVERY";
    public string BeaconId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string MulticastGroup { get; set; } = "239.0.0.222";
    public int MulticastPort { get; set; } = 5000;
    public int UdpServerSendPort { get; set; } = 5000;
    public int UdpClientReceivePort { get; set; } = 5001;
    public long TimestampUtc { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public ENetworkingType AdvertisedRole { get; set; } = ENetworkingType.Server;
}