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

    private static DriverFrameStore CreateSmoothStore(int vertexCount, int frameCount, double amplitude, GaussianCacheLoopMode mode = GaussianCacheLoopMode.Loop)
    {
        DriverFrameStore store = new(vertexCount, frameCount, 1, mode);
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
                store.Positions[index] = (ushort)Math.Round(baseX + (amplitude * Math.Sin(t)));
                store.Positions[index + 1] = (ushort)Math.Round(baseY + (amplitude * Math.Cos(t)));
                store.Positions[index + 2] = (ushort)Math.Round(baseZ + (amplitude * Math.Sin(2 * t)));
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
        DriverFrameStore target = new(200, 64, 1);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)3);
    }

    [Test]
    public void Motion_OnceMode_ReconstructsGridExactly()
    {
        DriverFrameStore source = CreateSmoothStore(50, 40, 2000, GaussianCacheLoopMode.Once);
        DriverFrameStore target = new(50, 40, 1, GaussianCacheLoopMode.Once);

        foreach ((MotionBlockDescriptor block, byte[] encoded) in EncodeAllLevels(source))
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
    }

    [Test]
    public void Motion_LargeJump_UsesWideResidualsAndRoundTrips()
    {
        DriverFrameStore source = new(4, 16, 1);
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

        DriverFrameStore target = new(4, 16, 1);
        foreach ((MotionBlockDescriptor block, byte[] encoded) in blocks)
            MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.Positions.ShouldBe(source.Positions);
    }

    [Test]
    public void Motion_DecodeT0Only_SetsDecodedLevelZero()
    {
        DriverFrameStore source = CreateSmoothStore(20, 32, 1000);
        DriverFrameStore target = new(20, 32, 1);

        (MotionBlockDescriptor block, byte[] encoded) = EncodeAllLevels(source)[0];
        target.DecodedTemporalLevel[0].ShouldBe((sbyte)-1);
        MotionDecoder.DecodeBlock(block, encoded, target, _meshopt);

        target.DecodedTemporalLevel[0].ShouldBe((sbyte)0);
    }

    [Test]
    public void Motion_SmoothMotion_EncodesBelowQuarterOfRaw()
    {
        DriverFrameStore source = CreateSmoothStore(200, 64, 120);
        long total = 0;
        foreach ((MotionBlockDescriptor _, byte[] encoded) in EncodeAllLevels(source))
            total += encoded.Length;

        double raw = 6.0 * 200 * 64;
        TestContext.Out.WriteLine($"Motion encoded {total} B of {raw} B raw, ratio {total / raw:F4}");
        total.ShouldBeLessThan((long)(0.25 * raw));
    }

    [Test]
    public void Motion_DecodeBeforeNeighbor_ThrowsChunkOutOfOrder()
    {
        DriverFrameStore source = CreateSmoothStore(20, 32, 1000);
        DriverFrameStore target = new(20, 32, 1);

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
