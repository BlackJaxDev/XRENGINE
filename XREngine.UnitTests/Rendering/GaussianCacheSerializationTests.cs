using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Cache;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GaussianCacheSerializationTests
{
    [Test]
    public void CookedBinary_RoundTrips_GaussianCache()
    {
        GaussianCache original = new()
        {
            Header = CreateHeader(),
            RootChunkData = [1, 2, 3, 4, 5, 250, 251],
            ChunkAssetPaths = ["a/chunk1.asset", "a/chunk2.asset", "a/chunk3.asset", "a/chunk4.asset"],
        };

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        GaussianCache? clone = CookedBinarySerializer.Deserialize(typeof(GaussianCache), bytes) as GaussianCache;

        clone.ShouldNotBeNull();
        clone.RootChunkData.ShouldBe(original.RootChunkData);
        clone.ChunkAssetPaths.ShouldBe(original.ChunkAssetPaths);
        AssertHeadersEqual(original.Header, clone.Header);
    }

    [Test]
    public void CookedBinary_RoundTrips_GaussianCacheChunk()
    {
        GaussianCacheChunk original = new() { Index = 3, Data = [9, 8, 7, 0, 255] };

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        GaussianCacheChunk? clone = CookedBinarySerializer.Deserialize(typeof(GaussianCacheChunk), bytes) as GaussianCacheChunk;

        clone.ShouldNotBeNull();
        clone.Index.ShouldBe(3);
        clone.Data.ShouldBe(original.Data);
    }

    [Test]
    public void PublishedRegistry_ReportsChunkDependenciesInOrder()
    {
        GaussianCache cache = new() { ChunkAssetPaths = ["x/1.asset", "x/2.asset", "x/3.asset"] };

        PublishedCookedAssetRegistry.TryGetDependencies(cache, out var dependencies).ShouldBeTrue();

        dependencies.ShouldNotBeNull();
        dependencies.Count.ShouldBe(3);
        for (int i = 0; i < 3; i++)
        {
            dependencies[i].AssetPath.ShouldBe(cache.ChunkAssetPaths[i]);
            dependencies[i].AssetType.ShouldBe(typeof(GaussianCacheChunk));
        }
    }

    [Test]
    public void HeaderRead_Trained4D_ThrowsUnsupportedRepresentation()
        => Should.Throw<GaussianCacheFormatException>(() => ReadWith(GaussianCacheRepresentation.Trained4D, GaussianCacheHeader.CurrentFormatVersion))
            .DiagnosticCode.ShouldBe("GaussianCache.UnsupportedRepresentation");

    [Test]
    public void HeaderRead_FormatVersion2_ThrowsUnsupportedFormatVersion()
        => Should.Throw<GaussianCacheFormatException>(() => ReadWith(GaussianCacheRepresentation.BakedDriver, 2))
            .DiagnosticCode.ShouldBe("GaussianCache.UnsupportedFormatVersion");

    [Test]
    public void HeaderRead_HugeArrayLength_ThrowsMalformed()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(GaussianCacheHeader.CurrentFormatVersion);
            writer.Write((byte)GaussianCacheRepresentation.BakedDriver);
            writer.Write((ushort)1);
            writer.Write(int.MaxValue);
        }
        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Should.Throw<GaussianCacheFormatException>(() => GaussianCacheHeader.Read(reader))
            .DiagnosticCode.ShouldBe("GaussianCache.Malformed");
    }

    [Test]
    public void HeaderRead_NegativeArrayLength_ThrowsMalformed()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(GaussianCacheHeader.CurrentFormatVersion);
            writer.Write((byte)GaussianCacheRepresentation.BakedDriver);
            writer.Write((ushort)1);
            writer.Write(-5);
        }
        stream.Position = 0;
        using BinaryReader reader = new(stream);

        Should.Throw<GaussianCacheFormatException>(() => GaussianCacheHeader.Read(reader))
            .DiagnosticCode.ShouldBe("GaussianCache.Malformed");
    }

    [Test]
    public void HeaderRead_TruncatedStream_ThrowsMalformed()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            CreateHeader().Write(writer);
        byte[] truncated = stream.ToArray()[..40];
        using BinaryReader reader = new(new MemoryStream(truncated));

        Should.Throw<GaussianCacheFormatException>(() => GaussianCacheHeader.Read(reader))
            .DiagnosticCode.ShouldBe("GaussianCache.Malformed");
    }

    [Test]
    public void HeaderWrite_ByteCount_MatchesCalculatedSize()
    {
        GaussianCacheHeader header = CreateHeader();
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            header.Write(writer);

        stream.Length.ShouldBe(header.CalculateByteSize());
    }

    private static void ReadWith(GaussianCacheRepresentation representation, ushort formatVersion)
    {
        GaussianCacheHeader header = CreateHeader();
        header.Representation = representation;
        header.FormatVersion = formatVersion;
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            header.Write(writer);
        stream.Position = 0;
        using BinaryReader reader = new(stream);
        GaussianCacheHeader.Read(reader);
    }

    private static GaussianCacheHeader CreateHeader()
    {
        string[] hashes =
        [
            new string('a', 64), new string('0', 64), "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new string('f', 64), new string('b', 64),
        ];
        return new GaussianCacheHeader
        {
            MotionCodecVersion = 1,
            SplatLevelCounts = [2000, 8000, 30000],
            StaticSplatCounts = [100, 400, 1500],
            DriverVertexCounts = [500, 2000, 6000],
            DriverTriangleCounts = [900, 3900, 11800],
            FrameCount = 300,
            FrameRate = 30.0f,
            DurationSeconds = 10.0f,
            LoopMode = GaussianCacheLoopMode.PingPong,
            Bounds = new AABB(new Vector3(-1.0f, -2.0f, -3.0f), new Vector3(1.5f, 2.5f, 3.5f)),
            GridBits = 12,
            ColorScale = 1.75f,
            CameraHints = new GaussianCacheCameraHints
            {
                OrbitTarget = new Vector3(0.0f, 1.0f, 0.0f),
                MinRadius = 0.5f,
                MaxRadius = 6.0f,
                DefaultRadius = 2.5f,
                DefaultYawDegrees = 20.0f,
                DefaultPitchDegrees = -10.0f,
                Path =
                [
                    new GaussianCacheCameraKey(0.0f, new Vector3(0.0f, 1.0f, 3.0f), Quaternion.Identity),
                    new GaussianCacheCameraKey(5.0f, new Vector3(3.0f, 1.0f, 0.0f), Quaternion.Normalize(new Quaternion(0.0f, 0.7f, 0.0f, 0.7f))),
                ],
            },
            Chunks =
            [
                new GaussianCacheChunkInfo(0, GaussianCacheChunkKind.Root, 0, 0, 0, 7, hashes[0]),
                new GaussianCacheChunkInfo(1, GaussianCacheChunkKind.DriverLevel, 0, 1, 0, 1000, hashes[1]),
                new GaussianCacheChunkInfo(2, GaussianCacheChunkKind.SplatLevel, 1, 1, 0, 2000, hashes[2]),
                new GaussianCacheChunkInfo(3, GaussianCacheChunkKind.SplatLevel, 2, 2, 0, 3000, hashes[3]),
                new GaussianCacheChunkInfo(4, GaussianCacheChunkKind.DriverTemporal, 2, 2, 3, 4000, hashes[4]),
            ],
        };
    }

    private static void AssertHeadersEqual(GaussianCacheHeader expected, GaussianCacheHeader actual)
    {
        actual.FormatVersion.ShouldBe(expected.FormatVersion);
        actual.Representation.ShouldBe(expected.Representation);
        actual.MotionCodecVersion.ShouldBe(expected.MotionCodecVersion);
        actual.SplatLevelCounts.ShouldBe(expected.SplatLevelCounts);
        actual.StaticSplatCounts.ShouldBe(expected.StaticSplatCounts);
        actual.DriverVertexCounts.ShouldBe(expected.DriverVertexCounts);
        actual.DriverTriangleCounts.ShouldBe(expected.DriverTriangleCounts);
        actual.FrameCount.ShouldBe(expected.FrameCount);
        actual.FrameRate.ShouldBe(expected.FrameRate);
        actual.DurationSeconds.ShouldBe(expected.DurationSeconds);
        actual.LoopMode.ShouldBe(expected.LoopMode);
        actual.Bounds.Min.ShouldBe(expected.Bounds.Min);
        actual.Bounds.Max.ShouldBe(expected.Bounds.Max);
        actual.GridBits.ShouldBe(expected.GridBits);
        actual.ColorScale.ShouldBe(expected.ColorScale);
        actual.Chunks.ShouldBe(expected.Chunks);

        actual.CameraHints.ShouldNotBeNull();
        GaussianCacheCameraHints e = expected.CameraHints!;
        GaussianCacheCameraHints a = actual.CameraHints;
        a.OrbitTarget.ShouldBe(e.OrbitTarget);
        a.MinRadius.ShouldBe(e.MinRadius);
        a.MaxRadius.ShouldBe(e.MaxRadius);
        a.DefaultRadius.ShouldBe(e.DefaultRadius);
        a.DefaultYawDegrees.ShouldBe(e.DefaultYawDegrees);
        a.DefaultPitchDegrees.ShouldBe(e.DefaultPitchDegrees);
        a.Path.ShouldBe(e.Path);
    }
}
