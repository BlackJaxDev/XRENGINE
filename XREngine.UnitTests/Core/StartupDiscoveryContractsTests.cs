using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Core;

public sealed class StartupDiscoveryContractsTests
{
    [Test]
    public void DiscoveryAnnouncement_DefaultsRemainStable()
    {
        DiscoveryAnnouncement announcement = new();

        announcement.Magic.ShouldBe("XRENGINE-DISCOVERY");
        announcement.MulticastGroup.ShouldBe("239.0.0.222");
        announcement.MulticastPort.ShouldBe(5000);
        announcement.UdpServerSendPort.ShouldBe(5000);
        announcement.UdpClientReceivePort.ShouldBe(5001);
        announcement.AdvertisedRole.ShouldBe(ENetworkingType.Server);
    }

    [Test]
    public void ENetworkingType_ParsesCaseInsensitively()
    {
        Enum.TryParse<ENetworkingType>("client", true, out ENetworkingType mode).ShouldBeTrue();
        mode.ShouldBe(ENetworkingType.Client);
    }

    [Test]
    public void EVRRuntime_DefaultAutoValueRemainsZero()
    {
        ((int)EVRRuntime.Auto).ShouldBe(0);
    }
}