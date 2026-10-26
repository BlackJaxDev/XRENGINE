using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking.Imaging;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BakeTonemapperTests
{
    [Test]
    public void ToSrgb8_Black_IsZero()
        => Map(0f, 0f, 0f, 0f).ShouldBe(new byte[] { 0, 0, 0, 0 });

    [Test]
    public void ToSrgb8_LinearOne_Is232()
    {
        // ACES(1) = 0.8038, and the sRGB encoding of 0.8038 is 0.9082, which rounds to 232.
        Map(1f, 1f, 1f, 1f).ShouldBe(new byte[] { 232, 232, 232, 255 });
    }

    [Test]
    public void ToSrgb8_VeryBright_ClampsTo255()
        => Map(1000f, 1000f, 1000f, 1000f).ShouldBe(new byte[] { 255, 255, 255, 255 });

    [Test]
    public void ToSrgb8_HugeOrInfinite_ClampsTo255()
        => Map(1e30f, float.PositiveInfinity, float.MaxValue, 1f).ShouldBe(new byte[] { 255, 255, 255, 255 });

    [Test]
    public void ToSrgb8_NegativeOrNaN_IsZero()
        => Map(-5f, float.NaN, -0.01f, -1f).ShouldBe(new byte[] { 0, 0, 0, 0 });

    [Test]
    public void ToSrgb8_HalfAlpha_Rounds()
        => Map(0f, 0f, 0f, 0.5f)[3].ShouldBe((byte)128);

    [Test]
    public void ToSrgb8_Exposure_ScalesColorBeforeCurve()
    {
        byte[] scaled = new byte[4];
        BakeTonemapper.ToSrgb8([0.5f, 0.5f, 0.5f, 1f], scaled, exposure: 2f);

        scaled.ShouldBe(Map(1f, 1f, 1f, 1f));
    }

    [Test]
    public void ToSrgb8_LengthMismatch_Throws()
        => Should.Throw<ArgumentException>(() => BakeTonemapper.ToSrgb8(new float[8], new byte[4]));

    private static byte[] Map(float r, float g, float b, float a)
    {
        byte[] result = new byte[4];
        BakeTonemapper.ToSrgb8([r, g, b, a], result);
        return result;
    }
}
