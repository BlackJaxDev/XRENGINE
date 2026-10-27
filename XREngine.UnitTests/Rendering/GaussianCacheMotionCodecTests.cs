using System;
using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Encoding;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class GaussianCacheMotionCodecTests
{
    private IMeshOptimizerBackend _meshopt = null!;

    [OneTimeSetUp]
    public void InstallMeshProcessing()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;
    }

    [TestCase(0, 0)]
    [TestCase(8, 0)]
    [TestCase(4, 1)]
    [TestCase(12, 1)]
    [TestCase(2, 2)]
    [TestCase(6, 2)]
    [TestCase(1, 3)]
    [TestCase(7, 3)]
    public void TemporalLevels_Of_MatchesSpec(int frame, int level)
        => TemporalLevels.Of(frame).ShouldBe((byte)level);

    [Test]
    public void NextNeighbor_LoopWrapsToFrameZero()
        => TemporalLevels.NextNeighbor(12, 4, 16, GaussianCacheLoopMode.Loop).ShouldBe(0);

    [Test]
    public void NextNeighbor_OnceUsesPreviousFrame()
        => TemporalLevels.NextNeighbor(12, 4, 16, GaussianCacheLoopMode.Once).ShouldBe(8);

    private static DriverFrameStore CreateSmoothStore(int vertexCount, int frameCount, double amplitude, GaussianCacheLoopMode mode = GaussianCacheLoopMode.Loop, int noiseLsb = 0)
    {
        DriverFrameStore store = new([vertexCount], frameCount, mode);
        Random random = new(42);
        for (int v = 0; v < vertexCount; v++)
        {
            double baseX = 20000 + (v * 37 % 20000);
            double baseY = 20000 + (v * 91 % 20000);
            double baseZ = 20000 + (v * 53 % 20000);
            double phase = v * 0.05;
            for (int f = 0; f < frameCount; f++)
            {
                double t = (2 * Math.PI * f / frameCount) + phase;
                int index = ((f * vertexCount) + v) * 3;
                int noiseX = noiseLsb > 0 ? random.Next(0, noiseLsb) : 0;
                int noiseY = noiseLsb > 0 ? random.Next(0, noiseLsb) : 0;
                int noiseZ = noiseLsb > 0 ? random.Next(0, noiseLsb) : 0;
                store.Positions[index] = (ushort)(Math.Round(baseX + (amplitude * Math.Sin(t))) + noiseX);
                store.Positions[index + 1] = (ushort)(Math.Round(baseY + (amplitude * Math.Cos(t))) + noiseY);
                store.Positions[index + 2] = (ushort)(Math.Round(baseZ + (amplitude * Math.Sin(2 * t))) + noiseZ);
            }
        }
        return store;
    }

    private List<(MotionBlockDescriptor Block, byte[] Encoded)> EncodeAllLevels(DriverFrameStore source)
    {
        List<(MotionBlockDescriptor, byte[])> blocks = [];
        for (byte level = 0; level < TemporalLevels.Count; level++)
        {
            int count = TemporalLevels.FramesInLevel(level, source.FrameCount);
            MotionBlockDescriptor block = new(0, level, 0, source.VertexCount, TemporalLevels.FirstFrame(level), count, false);
            (byte[] encoded, bool wide) = MotionEncoder.EncodeBlock(block, source, _meshopt);
            blocks.Add((block with { WideResiduals = wide }, encoded));
        }
        return blocks;
    }

    [Test]
    public void Motion_EncodeDecodeAllLevels_ReconstructsGridExactly()
    {
        DriverFrameStore source = CreateSmoothStore(200, 64, 3000);
        DriverFrameStore target = new([200], 64, GaussianCacheLoopMode.Loop);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)3);
    }

    [Test]
    public void Motion_OnceMode_ReconstructsGridExactly()
    {
        DriverFrameStore source = CreateSmoothStore(50, 40, 2000, GaussianCacheLoopMode.Once);
        DriverFrameStore target = new([50], 40, GaussianCacheLoopMode.Once);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
    }

    [Test]
    public void Motion_LargeJump_UsesWideResidualsAndRoundTrips()
    {
        DriverFrameStore source = new([4], 16, GaussianCacheLoopMode.Loop);
        for (int f = 0; f < 16; f++)
        {
            for (int v = 0; v < 4; v++)
            {
                int index = ((f * 4) + v) * 3;
                int value = f < 8 ? 1000 + f : 41000 + f;
                source.Positions[index] = (ushort)value;
                source.Positions[index + 1] = (ushort)(value / 2);
                source.Positions[index + 2] = (ushort)(1000 + v);
            }
        }

        List<(MotionBlockDescriptor Block, byte[] Encoded)> blocks = EncodeAllLevels(source);
        blocks[0].Block.WideResiduals.ShouldBeTrue();

        DriverFrameStore target = new([4], 16, GaussianCacheLoopMode.Loop);
        foreach ((MotionBlockDescriptor block, byte[] encoded) in blocks)
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
    }

    [Test]
    public void Motion_DecodeT0Only_SetsDecodedLevelZero()
    {
        DriverFrameStore source = CreateSmoothStore(20, 32, 1000);
        DriverFrameStore target = new([20], 32, GaussianCacheLoopMode.Loop);

        (MotionBlockDescriptor block, byte[] encoded) = EncodeAllLevels(source)[0];
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)-1);
        MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.DecodedTemporalLevel[0].ShouldBe((sbyte)0);
    }

    [Test]
    public void Motion_RealisticSmoothMotion_EncodesWithinMeasuredBound()
    {
        // Measured encoded/raw ratios for 200 vertices x 64 frames with 1-LSB noise:
        // amplitude 120 -> 0.2669, amplitude 1000 -> 0.3369, amplitude 3000 -> 0.4111 (bound is 1.10 times this: 0.4522).
        foreach (double amplitude in new[] { 120.0, 1000.0, 3000.0 })
        {
            double ratio = MeasureRatio(amplitude);
            TestContext.Out.WriteLine($"Motion ratio amplitude {amplitude}: {ratio:F4}");
            if (amplitude == 3000.0)
                ratio.ShouldBeLessThan(0.4522);
        }
    }

    private double MeasureRatio(double amplitude)
    {
        DriverFrameStore source = CreateSmoothStore(200, 64, amplitude, GaussianCacheLoopMode.Loop, noiseLsb: 2);
        long total = 0;
        foreach ((MotionBlockDescriptor _, byte[] encoded) in EncodeAllLevels(source))
            total += encoded.Length;
        return total / (6.0 * 200 * 64);
    }

    [Test]
    public void Motion_FrameCountRule_RejectsNonMultipleOfEight()
    {
        Should.Throw<ArgumentException>(() => new DriverFrameStore([10], 302, GaussianCacheLoopMode.Loop));
        Should.Throw<ArgumentException>(() => new DriverFrameStore([10], 0, GaussianCacheLoopMode.Loop));
        new DriverFrameStore([10], 304, GaussianCacheLoopMode.Loop).FrameCount.ShouldBe(304);
        new DriverFrameStore([10], 1, GaussianCacheLoopMode.Once).FrameCount.ShouldBe(1);
    }

    [Test]
    public void Motion_PingPongStore_RoundTrips()
    {
        DriverFrameStore source = CreateSmoothStore(50, 32, 2000, GaussianCacheLoopMode.PingPong);
        DriverFrameStore target = new([50], 32, GaussianCacheLoopMode.PingPong);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
    }

    [Test]
    public void Motion_DecodeWithDifferentLoopMode_DoesNotReproduceSource()
    {
        DriverFrameStore source = CreateSmoothStore(50, 32, 2000, GaussianCacheLoopMode.Loop);
        DriverFrameStore target = new([50], 32, GaussianCacheLoopMode.Once);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldNotBe(source.Positions);
    }

    [Test]
    public void Motion_RepeatedBlock_DoesNotOverstateDecodedLevel()
    {
        DriverFrameStore source = CreateSmoothStore(20, 32, 1000);
        DriverFrameStore target = new([20], 32, GaussianCacheLoopMode.Loop);
        MotionBlockDescriptor half = new(0, 0, 0, 10, 0, TemporalLevels.FramesInLevel(0, 32), false);
        (byte[] encoded, _) = MotionEncoder.EncodeBlock(half, source, _meshopt);

        // Repeating a block that covers half of the vertices must not make the level count as complete.
        for (int i = 0; i < 3; i++)
            MotionDecoder.DecodeBlock(half, encoded, target, _meshopt);
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)-1);

        MotionBlockDescriptor other = half with { FirstVertex = 10 };
        (byte[] otherEncoded, _) = MotionEncoder.EncodeBlock(other, source, _meshopt);
        MotionDecoder.DecodeBlock(other, otherEncoded, target, _meshopt);
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)0);
    }

    [Test]
    public void Motion_LevelWithTwoVertexRanges_IsCompleteOnlyWhenBothArrive()
    {
        DriverFrameStore source = new([10, 20], 8, GaussianCacheLoopMode.Loop);
        for (int i = 0; i < source.Positions.Length; i++)
            source.Positions[i] = (ushort)(1000 + (i * 7 % 50));
        DriverFrameStore target = new([10, 20], 8, GaussianCacheLoopMode.Loop);

        MotionBlockDescriptor lower = new(1, 0, 10, 5, 0, 1, false);
        MotionBlockDescriptor upper = new(1, 0, 15, 5, 0, 1, false);
        (byte[] lowerBytes, _) = MotionEncoder.EncodeBlock(lower, source, _meshopt);
        (byte[] upperBytes, _) = MotionEncoder.EncodeBlock(upper, source, _meshopt);

        MotionDecoder.DecodeBlock(lower, lowerBytes, target, _meshopt);
        target.DecodedTemporalLevel[1].ShouldBe((sbyte)-1);
        MotionDecoder.DecodeBlock(upper, upperBytes, target, _meshopt);
        target.DecodedTemporalLevel[1].ShouldBe((sbyte)0);
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)-1);
    }

    [Test]
    public void Motion_BlockOutsideDriverLevel_ThrowsMalformed()
    {
        DriverFrameStore store = new([10, 20], 8, GaussianCacheLoopMode.Loop);
        MotionBlockDescriptor block = new(0, 0, 8, 5, 0, 1, false);
        GaussianCacheFormatException exception = Should.Throw<GaussianCacheFormatException>(
            () => MotionDecoder.DecodeBlock(block, [], store, _meshopt));
        exception.DiagnosticCode.ShouldBe(GaussianCacheFormatException.MalformedCode);
    }

    [Test]
    public void SplatStreams_WriterReaderRoundTrip()
    {
        GaussianCacheSplatStreamsWriter writer = new();
        writer.Append(GaussianCacheSplatAttribute.Binding, [1, 2, 3]);
        writer.Append(GaussianCacheSplatAttribute.ChildCount, []);
        writer.Append(GaussianCacheSplatAttribute.Color, [9]);

        IReadOnlyDictionary<GaussianCacheSplatAttribute, ReadOnlyMemory<byte>> streams = GaussianCacheSplatStreams.Read(writer.ToArray());
        streams.Count.ShouldBe(3);
        streams[GaussianCacheSplatAttribute.Binding].ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        streams[GaussianCacheSplatAttribute.ChildCount].Length.ShouldBe(0);
        streams[GaussianCacheSplatAttribute.Color].ToArray().ShouldBe(new byte[] { 9 });
    }

    [Test]
    public void SplatStreams_TruncatedDuplicateOrUnknown_ThrowsMalformed()
    {
        GaussianCacheSplatStreamsWriter writer = new();
        writer.Append(GaussianCacheSplatAttribute.Binding, [1, 2, 3]);
        byte[] good = writer.ToArray();
        AssertMalformed(good.AsMemory(0, good.Length - 1));
        AssertMalformed(good.AsMemory(0, 3));

        GaussianCacheSplatStreamsWriter duplicate = new();
        duplicate.Append(GaussianCacheSplatAttribute.Scale, [1]);
        duplicate.Append(GaussianCacheSplatAttribute.Scale, [2]);
        AssertMalformed(duplicate.ToArray());

        GaussianCacheSplatStreamsWriter unknown = new();
        unknown.Append((GaussianCacheSplatAttribute)99, [1]);
        AssertMalformed(unknown.ToArray());

        byte[] negative = [1, 0xFF, 0xFF, 0xFF, 0xFF];
        AssertMalformed(negative);

        static void AssertMalformed(ReadOnlyMemory<byte> data)
            => Should.Throw<GaussianCacheFormatException>(() => GaussianCacheSplatStreams.Read(data))
                .DiagnosticCode.ShouldBe(GaussianCacheFormatException.MalformedCode);
    }

    [Test]
    public void Motion_DecodeBeforeNeighbor_ThrowsChunkOutOfOrder()
    {
        DriverFrameStore source = CreateSmoothStore(20, 32, 1000);
        DriverFrameStore target = new([20], 32, GaussianCacheLoopMode.Loop);

        (MotionBlockDescriptor block, byte[] encoded) = EncodeAllLevels(source)[1];
        GaussianCacheFormatException exception = Should.Throw<GaussianCacheFormatException>(
            () => MotionDecoder.DecodeBlock(block, encoded, target, _meshopt));
        exception.DiagnosticCode.ShouldBe(GaussianCacheFormatException.ChunkOutOfOrderCode);
    }

    [Test]
    public void ChunkSections_WriterReaderRoundTrip()
    {
        byte[] payloadA = [1, 2, 3, 4, 5];
        byte[] payloadB = [];
        GaussianCacheSection a = new(GaussianCacheSectionKind.DriverMotion, 1, 2, GaussianCacheSection.WideResidualsFlag, 10, 20, 30, 40, payloadA);
        GaussianCacheSection b = new(GaussianCacheSectionKind.SplatStreams, 0, 0, 0, 0, 5, 0, 0, payloadB);

        GaussianCacheChunkWriter writer = new();
        writer.Append(a);
        writer.Append(b);
        writer.Length.ShouldBe((GaussianCacheSection.HeaderSize * 2) + payloadA.Length);

        IReadOnlyList<GaussianCacheSection> sections = GaussianCacheChunkReader.Read(writer.ToArray());
        sections.Count.ShouldBe(2);
        sections[0].Kind.ShouldBe(GaussianCacheSectionKind.DriverMotion);
        (sections[0].Level, sections[0].TemporalLevel, sections[0].First, sections[0].Count, sections[0].FrameFirst, sections[0].FrameCount)
            .ShouldBe(((byte)1, (byte)2, 10, 20, 30, 40));
        sections[0].WideResiduals.ShouldBeTrue();
        sections[0].Payload.ToArray().ShouldBe(payloadA);
        sections[1].Kind.ShouldBe(GaussianCacheSectionKind.SplatStreams);
        sections[1].Payload.Length.ShouldBe(0);
    }

    [Test]
    public void ChunkSections_PayloadLongerThanBuffer_ThrowsMalformed()
    {
        GaussianCacheChunkWriter writer = new();
        writer.Append(new GaussianCacheSection(GaussianCacheSectionKind.DriverIndices, 0, 0, 0, 0, 0, 0, 0, new byte[8]));
        byte[] data = writer.ToArray();

        GaussianCacheFormatException exception = Should.Throw<GaussianCacheFormatException>(
            () => GaussianCacheChunkReader.Read(data.AsMemory(0, data.Length - 1)));
        exception.DiagnosticCode.ShouldBe(GaussianCacheFormatException.MalformedCode);
    }
}
