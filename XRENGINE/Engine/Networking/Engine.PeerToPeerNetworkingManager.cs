using System.Net;

namespace XREngine
{
    public class PeerToPeerNetworkingManager : BaseNetworkingManager
        {
            public override bool IsServer => false;
            public override bool IsClient => false;
            public override bool IsP2P => true;

            public void Start(
                IPAddress udpMulticastGroupIP,
                int udpMulticastPort,
                IPAddress serverIP)
            {
                Debug.Out($"Starting client with udp(multicast: {udpMulticastGroupIP}:{udpMulticastPort}) sending to server at ({serverIP}:{udpMulticastPort})");
                StartUdpMulticastReceiver(serverIP, udpMulticastGroupIP, udpMulticastPort);
                StartUdpMulticastSender(udpMulticastGroupIP, udpMulticastPort);
            }

            protected override async Task SendUDP()
            {
                //Send to all peers
                await ConsumeAndSendUDPQueue(UdpMulticastSender, MulticastEndPoint);
            }

        }

}
