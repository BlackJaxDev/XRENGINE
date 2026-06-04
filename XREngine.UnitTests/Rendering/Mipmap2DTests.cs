using ImageMagick;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class Mipmap2DTests
{
    [Test]
    public void SetFromImage_PreservesDecodedRgba8ColorData()
    {
        byte[] rgba =
        [
            255, 0, 0, 255,
            0, 128, 255, 64,
        ];

        MagickReadSettings settings = new()
        {
            Width = 2,
            Height = 1,
            Format = MagickFormat.Rgba,
            Depth = 8,
        };

        using MagickImage image = new(rgba, settings);
        Mipmap2D mipmap = new(image);

        byte[] actual = mipmap.DataBytes.ShouldNotBeNull();
        actual.ShouldBe(rgba);
    }
}
