using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class MipmapLevelTests
{
    [TestCase(1u, 1u, 0)]
    [TestCase(2u, 2u, 1)]
    [TestCase(4u, 4u, 2)]
    [TestCase(1024u, 1024u, 10)]
    [TestCase(2048u, 2048u, 11)]
    [TestCase(1920u, 1080u, 10)]
    public void GetSmallestMipmapLevel_IsExactFloorLog2(uint width, uint height, int expected)
    {
        XRTexture.GetSmallestMipmapLevel(width, height).ShouldBe(expected);
    }

    [Test]
    public void GetSmallestMipmapLevel_RespectsSmallestAllowedMipmapLevel()
    {
        // 2048 => floor(log2)=11, but clamp to 4.
        XRTexture.GetSmallestMipmapLevel(2048, 2048, smallestAllowedMipmapLevel: 4).ShouldBe(4);
    }
}
