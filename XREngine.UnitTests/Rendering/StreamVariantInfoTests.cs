using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class StreamVariantInfoTests
{
    [Test]
    public void ParseFromMasterPlaylist_ReturnsHighestBandwidthFirst()
    {
        const string playlist = """
#EXTM3U
#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,FRAME-RATE=60,CODECS="avc1",NAME="720p60"
mid/index.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=6200000,RESOLUTION=1920x1080,FRAME-RATE=60,CODECS="avc1",NAME="1080p60"
hi/index.m3u8
""";

        IReadOnlyList<StreamVariantInfo> variants = StreamVariantInfo.ParseFromMasterPlaylist("https://cdn.example.com/master.m3u8", playlist);

        variants.Count.ShouldBe(2);
        variants[0].Bandwidth.ShouldBe(6_200_000);
        variants[0].Url.ShouldBe("https://cdn.example.com/hi/index.m3u8");
        variants[0].DisplayLabel.ShouldBe("1080p60 (6.2 Mbps)");
        variants[1].Bandwidth.ShouldBe(2_500_000);
    }

    [Test]
    public void ParseFromMasterPlaylist_UsesVideoAttributeWhenNameIsMissing()
    {
        const string playlist = """
#EXTM3U
#EXT-X-STREAM-INF:BANDWIDTH=1100000,RESOLUTION=854x480,VIDEO="main"
480/index.m3u8
""";

        IReadOnlyList<StreamVariantInfo> variants = StreamVariantInfo.ParseFromMasterPlaylist("https://cdn.example.com/master.m3u8", playlist);

        variants.Count.ShouldBe(1);
        variants[0].Name.ShouldBe("main");
        variants[0].Url.ShouldBe("https://cdn.example.com/480/index.m3u8");
    }

    [Test]
    public void ParseFromMasterPlaylist_ReturnsEmptyForMediaPlaylist()
    {
        const string playlist = """
#EXTM3U
#EXT-X-TARGETDURATION:4
#EXTINF:4,
segment0.ts
""";

        StreamVariantInfo.ParseFromMasterPlaylist("https://cdn.example.com/live/index.m3u8", playlist)
            .ShouldBeEmpty();
    }
}
