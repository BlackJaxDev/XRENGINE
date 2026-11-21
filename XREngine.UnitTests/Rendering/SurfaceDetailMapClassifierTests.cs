using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SurfaceDetailMapClassifierTests
{
    private const int Size = 64;

    [Test]
    public void FlatTangentNormals_ClassifyAsNormal()
    {
        // Small slope variation around a +Z normal, as in a typical tangent-space map.
        byte[] pixels = Fill((x, y) => ((byte)(100 + (x % 8) * 7), (byte)(100 + (y % 8) * 7), 240));

        SurfaceDetailMapClassifier.Classify(pixels, Size, Size).ShouldBe(ESurfaceDetailMapMode.Normal);
    }

    [Test]
    public void BrightBricksWithDarkMortar_ClassifyAsHeight()
    {
        // Height field: bright tops, thin dark grooves on one side of the base value.
        byte[] pixels = Fill((x, y) => x % 16 < 2 || y % 8 < 1 ? Gray(60) : Gray(210));

        SurfaceDetailMapClassifier.Classify(pixels, Size, Size).ShouldBe(ESurfaceDetailMapMode.Height);
    }

    [Test]
    public void EmbossedRidgesOnGrayBase_ClassifyAsBump()
    {
        // Emboss: flat mid-gray base with a bright edge and a dark edge per ridge.
        byte[] pixels = Fill((x, y) => (x % 16) switch
        {
            0 or 1 => Gray(220),
            2 or 3 => Gray(40),
            _ => Gray(128),
        });

        SurfaceDetailMapClassifier.Classify(pixels, Size, Size).ShouldBe(ESurfaceDetailMapMode.Bump);
    }

    [Test]
    public void ColoredTexture_ClassifiesAsAlbedo()
    {
        byte[] pixels = Fill((x, y) => x % 2 == 0 ? ((byte)200, (byte)60, (byte)40) : ((byte)50, (byte)160, (byte)70));

        SurfaceDetailMapClassifier.Classify(pixels, Size, Size).ShouldBe(ESurfaceDetailMapMode.Albedo);
    }

    [Test]
    public void UniformGray_ClassifiesAsBump()
    {
        byte[] pixels = Fill((x, y) => Gray(128));

        SurfaceDetailMapClassifier.Classify(pixels, Size, Size).ShouldBe(ESurfaceDetailMapMode.Bump);
    }

    private static (byte R, byte G, byte B) Gray(byte value) => (value, value, value);

    private static byte[] Fill(System.Func<int, int, (byte R, byte G, byte B)> color)
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                (byte r, byte g, byte b) = color(x, y);
                int offset = (y * Size + x) * 4;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }
}
