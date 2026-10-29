using System.Numerics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GaussianCacheEncodingTests
{
    [Test]
    public void ChooseGridBits_TwoMeterAvatar_Is11()
    {
        AABB bounds = new(new Vector3(-1f, 0f, -1f), new Vector3(1f, 2f, 1f));
        GridQuantizer.ChooseGridBits(bounds, 0.0005f).ShouldBe((byte)11);
    }

    [Test]
    public void ChooseGridBits_KilometerWorld_Returns16()
    {
        AABB bounds = new(new Vector3(-500f), new Vector3(500f));
        GridQuantizer.ChooseGridBits(bounds, 0.002f).ShouldBe((byte)16);
    }

    [Test]
    public void GridQuantizer_RoundTrip_ErrorWithinMax()
    {
        AABB bounds = new(new Vector3(-1f, 0f, -0.5f), new Vector3(1f, 2f, 0.5f));
        GridQuantizer quantizer = new(bounds, 11);
        Random random = new(1234);
        for (int i = 0; i < 10000; i++)
        {
            Vector3 p = new(
                bounds.Min.X + (float)random.NextDouble() * (bounds.Max.X - bounds.Min.X),
                bounds.Min.Y + (float)random.NextDouble() * (bounds.Max.Y - bounds.Min.Y),
                bounds.Min.Z + (float)random.NextDouble() * (bounds.Max.Z - bounds.Min.Z));
            quantizer.Quantize(p, out ushort x, out ushort y, out ushort z);
            Vector3 q = quantizer.Dequantize(x, y, z);
            MathF.Abs(q.X - p.X).ShouldBeLessThanOrEqualTo(quantizer.MaxErrorMeters + 1e-6f);
            MathF.Abs(q.Y - p.Y).ShouldBeLessThanOrEqualTo(quantizer.MaxErrorMeters + 1e-6f);
            MathF.Abs(q.Z - p.Z).ShouldBeLessThanOrEqualTo(quantizer.MaxErrorMeters + 1e-6f);
        }
    }

    [Test]
    public void GridQuantizer_DegenerateAxis_QuantizesToZeroAndReturnsMin()
    {
        AABB bounds = new(new Vector3(0f, 3f, 0f), new Vector3(1f, 3f, 1f));
        GridQuantizer quantizer = new(bounds, 10);
        quantizer.Quantize(new Vector3(0.5f, 3f, 0.5f), out _, out ushort y, out _);
        y.ShouldBe((ushort)0);
        quantizer.Dequantize(0, 0, 0).Y.ShouldBe(3f);
    }

    [Test]
    public void GridQuantizer_OutOfBoundsPoint_Clamps()
    {
        AABB bounds = new(Vector3.Zero, Vector3.One);
        GridQuantizer quantizer = new(bounds, 8);
        quantizer.Quantize(new Vector3(-5f, 5f, 0.5f), out ushort x, out ushort y, out _);
        x.ShouldBe((ushort)0);
        y.ShouldBe((ushort)255);
    }

    [Test]
    public void ZigZag_RoundTripsInt16Range()
    {
        for (int v = short.MinValue; v <= short.MaxValue; v++)
            ZigZag.Decode16(ZigZag.Encode16(v)).ShouldBe(v);
        ZigZag.Encode16(0).ShouldBe((ushort)0);
        ZigZag.Encode16(-1).ShouldBe((ushort)1);
        ZigZag.Encode16(1).ShouldBe((ushort)2);
        foreach (int v in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            ZigZag.Decode32(ZigZag.Encode32(v)).ShouldBe(v);
    }

    [Test]
    public void ZigZag_Encode16_OutOfRange_Throws()
    {
        Should.Throw<OverflowException>(() => ZigZag.Encode16(short.MaxValue + 1));
        Should.Throw<OverflowException>(() => ZigZag.Encode16(short.MinValue - 1));
    }

    [Test]
    public void PlaneCoordinate_RoundTrip_ErrorBelowTwoPointFiveEMinusFive()
    {
        for (int i = 0; i <= 3000; i++)
        {
            float v = -1f + 3f * i / 3000f;
            MathF.Abs(PlaneCoordinate.Unpack(PlaneCoordinate.Pack(v)) - v).ShouldBeLessThan(2.5e-5f);
        }
    }

    [Test]
    public void PlaneCoordinate_OutOfRangeAndNaN_ClampsAndPacksZero()
    {
        PlaneCoordinate.Pack(-7f).ShouldBe((ushort)0);
        PlaneCoordinate.Pack(9f).ShouldBe((ushort)65535);
        PlaneCoordinate.Pack(float.NaN).ShouldBe(PlaneCoordinate.Pack(0f));
    }

    [Test]
    public void Srgb8_RoundTripsAll256Values()
    {
        for (int i = 0; i < 256; i++)
            Srgb8.FromLinear(Srgb8.ToLinear((byte)i)).ShouldBe((byte)i);
        Srgb8.FromLinear(-1f).ShouldBe((byte)0);
        Srgb8.FromLinear(2f).ShouldBe((byte)255);
    }

    [Test]
    public void SplatRecord_SizeIs48()
        => Unsafe.SizeOf<GaussianCacheSplatRecord>().ShouldBe(48);

    [Test]
    public void TriangleFrame_Reconstruct_RecoversPointOffPlane()
    {
        Vector3 a = new(0.2f, 0.1f, -0.3f);
        Vector3 b = new(1.1f, 0.3f, 0.2f);
        Vector3 c = new(0.4f, 1.2f, 0.5f);
        const float u = 1.4f, v = -0.2f, h = 0.03f;
        Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        Vector3 expected = a + u * (b - a) + v * (c - a) + h * n;
        Vector3.Distance(TriangleFrame.Reconstruct(a, b, c, u, v, h), expected).ShouldBeLessThan(1e-6f);
    }

    [Test]
    public void TriangleFrame_Compute_MapsAxesToTangentNormalBitangent()
    {
        Vector3 a = Vector3.Zero, b = new(2f, 0f, 0f), c = new(0f, 3f, 0f);
        TriangleFrame.TryCompute(a, b, c, out Quaternion frame).ShouldBeTrue();
        Vector3.Distance(Vector3.Transform(Vector3.UnitX, frame), Vector3.UnitX).ShouldBeLessThan(1e-6f);
        Vector3.Distance(Vector3.Transform(Vector3.UnitZ, frame), Vector3.UnitZ).ShouldBeLessThan(1e-6f);
        Vector3.Distance(Vector3.Transform(Vector3.UnitY, frame), Vector3.UnitY).ShouldBeLessThan(1e-6f);
        TriangleFrame.TryCompute(a, new Vector3(0f, 2f, 0f), new Vector3(-3f, 0f, 0f), out frame).ShouldBeTrue();
        Vector3.Distance(Vector3.Transform(Vector3.UnitX, frame), Vector3.UnitY).ShouldBeLessThan(1e-6f);
        Vector3.Distance(Vector3.Transform(Vector3.UnitY, frame), -Vector3.UnitX).ShouldBeLessThan(1e-6f);
    }

    [Test]
    public void TriangleFrame_Degenerate_ReturnsFalse()
    {
        Vector3 a = Vector3.Zero;
        TriangleFrame.TryCompute(a, Vector3.UnitX, 2f * Vector3.UnitX, out _).ShouldBeFalse();
        // Cross lengths of 1e-14 and 1e-10 sit clearly below and above the 1e-12 threshold.
        TriangleFrame.TryCompute(a, new Vector3(1e-7f, 0f, 0f), new Vector3(0f, 1e-7f, 0f), out _).ShouldBeFalse();
        TriangleFrame.TryCompute(a, new Vector3(1e-5f, 0f, 0f), new Vector3(0f, 1e-5f, 0f), out _).ShouldBeTrue();
    }
}

