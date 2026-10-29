using System.IO;
using System.Numerics;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Cache;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks that the cache reader stops with a format error, and allocates nothing large, for hostile header values,
/// sizes, chunk orders, and chunk paths.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GaussianCacheFormatLimitsTests
{
    private const string Malformed = "GaussianCache.Malformed";
    private const string LimitExceeded = "GaussianCache.LimitExceeded";
    private const string InvalidChunkPath = "GaussianCache.InvalidChunkPath";

    [Test]
    public void HeaderRead_ValidHeader_ReadsTheSameValues()
    {
        GaussianCacheHeader header = CreateHeader();

        GaussianCacheHeader read = Read(Write(header));

        read.SplatLevelCounts.ShouldBe(header.SplatLevelCounts);
        read.FrameCount.ShouldBe(header.FrameCount);
        read.Chunks.Length.ShouldBe(header.Chunks.Length);
    }

    [TestCase((ushort)0)]
    [TestCase((ushort)2)]
    public void HeaderRead_OtherFormatVersion_ThrowsUnsupportedFormatVersion(ushort version)
        => ReadCode(header => header.FormatVersion = version).ShouldBe("GaussianCache.UnsupportedFormatVersion");

    [TestCase((ushort)0)]
    [TestCase((ushort)2)]
    public void HeaderRead_OtherMotionCodecVersion_ThrowsUnsupportedMotionCodecVersion(ushort version)
        => ReadCode(header => header.MotionCodecVersion = version).ShouldBe("GaussianCache.UnsupportedMotionCodecVersion");

    [Test]
    public void HeaderRead_UndefinedLoopMode_ThrowsMalformed()
        => ReadCode(header => header.LoopMode = (GaussianCacheLoopMode)3).ShouldBe(Malformed);

    [TestCase((byte)0)]
    [TestCase((byte)17)]
    public void HeaderRead_GridBitsOutOfRange_ThrowsMalformed(byte gridBits)
        => ReadCode(header => header.GridBits = gridBits).ShouldBe(Malformed);

    [TestCase(float.NaN)]
    [TestCase(0.0f)]
    [TestCase(-30.0f)]
    [TestCase(float.PositiveInfinity)]
    public void HeaderRead_FrameRateNotFiniteAndPositive_ThrowsMalformed(float rate)
        => ReadCode(header => header.FrameRate = rate).ShouldBe(Malformed);

    [TestCase(float.NaN)]
    [TestCase(0.0f)]
    [TestCase(float.PositiveInfinity)]
    public void HeaderRead_ColorScaleNotFiniteAndPositive_ThrowsMalformed(float scale)
        => ReadCode(header => header.ColorScale = scale).ShouldBe(Malformed);

    [Test]
    public void HeaderRead_FrameCountNotMultipleOf8_ThrowsMalformed()
        => ReadCode(header => header.FrameCount = 300).ShouldBe(Malformed);

    [Test]
    public void HeaderRead_TooManyFrames_ThrowsLimitExceeded()
        => ReadCode(header => header.FrameCount = 8_192).ShouldBe(LimitExceeded);

    [Test]
    public void HeaderRead_HugeSplatCountsInSmallHeader_ThrowsLimitExceeded()
    {
        // The header is a few hundred bytes, but its counts ask for 3 x 2^24 records.
        GaussianCacheHeader header = CreateHeader();
        header.SplatLevelCounts = [1 << 24, 1 << 24, 1 << 24];
        byte[] bytes = Write(header);
        bytes.Length.ShouldBeLessThan(1_024);

        Should.Throw<GaussianCacheFormatException>(() => Read(bytes)).DiagnosticCode.ShouldBe(LimitExceeded);
    }

    [Test]
    public void HeaderRead_TooManyDriverVertices_ThrowsLimitExceeded()
        => ReadCode(header => header.DriverVertexCounts = [500, 2_000, (1 << 20) + 1]).ShouldBe(LimitExceeded);

    [Test]
    public void HeaderRead_TooManyDriverPositions_ThrowsLimitExceeded()
        => ReadCode(header =>
        {
            header.DriverVertexCounts = [500, 2_000, 1 << 20];
            header.FrameCount = 32;
        }).ShouldBe(LimitExceeded);

    [Test]
    public void HeaderRead_TooManyDriverTriangles_ThrowsLimitExceeded()
        => ReadCode(header => header.DriverTriangleCounts = [900, 3_900, 1 << 22]).ShouldBe(LimitExceeded);

    [Test]
    public void HeaderValidate_TooManyChunks_ThrowsLimitExceeded()
    {
        GaussianCacheHeader header = CreateHeader();
        GaussianCacheChunkInfo[] chunks = new GaussianCacheChunkInfo[(1 << 14) + 1];
        chunks[0] = new GaussianCacheChunkInfo(0, GaussianCacheChunkKind.Root, 0, 0, 0, 7, new string('a', 64));
        for (int index = 1; index < chunks.Length; index++)
            chunks[index] = new GaussianCacheChunkInfo(index, GaussianCacheChunkKind.SplatLevel, 2, 2, 0, 7, new string('b', 64));
        header.Chunks = chunks;

        Should.Throw<GaussianCacheFormatException>(() => Read(Write(header))).DiagnosticCode.ShouldBe(LimitExceeded);
    }

    [TestCase("short")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [TestCase("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void HeaderRead_ChunkHashNotLowercaseSha256Hex_ThrowsMalformed(string hash)
        => ReadCode(header => header.Chunks[2] = header.Chunks[2] with { Sha256 = hash }).ShouldBe(Malformed);

    [Test]
    public void HeaderRead_SecondRootChunk_ThrowsMalformed()
        => ReadCode(header => header.Chunks[1] = header.Chunks[1] with { Kind = GaussianCacheChunkKind.Root }).ShouldBe(Malformed);

    [Test]
    public void HeaderRead_UndefinedChunkKind_ThrowsMalformed()
        => ReadCode(header => header.Chunks[1] = header.Chunks[1] with { Kind = (GaussianCacheChunkKind)9 }).ShouldBe(Malformed);

    [Test]
    public void HeaderRead_ChunkLevelOutOfRange_ThrowsMalformed()
        => ReadCode(header => header.Chunks[3] = header.Chunks[3] with { SplatLevel = 3 }).ShouldBe(Malformed);

    [Test]
    public void AcceptChunk_SplatsBeforeTheirDriverTriangles_ThrowsMalformed()
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build();
        GaussianCacheChunkInfo[] infos = build.Root.Header.Chunks;
        int triangles = Array.FindIndex(infos, info => info.Kind == GaussianCacheChunkKind.DriverLevel && info.DriverLevel == 1);
        int splats = Array.FindIndex(infos, info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 1);
        splats.ShouldBe(triangles + 1);

        // Swap the two chunks, so that the splats of level 1 arrive before the triangles of driver level 1.
        GaussianCacheHeader header = Read(Write(build.Root.Header));
        GaussianCacheChunkInfo[] swapped = [.. infos];
        swapped[triangles] = infos[splats] with { Index = triangles };
        swapped[splats] = infos[triangles] with { Index = splats };
        header.Chunks = swapped;
        GaussianCache cache = new() { Header = header, RootChunkData = build.Root.RootChunkData, ChunkAssetPaths = build.Root.ChunkAssetPaths };
        for (int index = 1; index < triangles; index++)
            cache.AcceptChunk(build.Chunks[index - 1]);

        GaussianCacheFormatException failure = Should.Throw<GaussianCacheFormatException>(
            () => cache.AcceptChunk(new GaussianCacheChunk { Index = triangles, Data = build.Chunks[splats - 1].Data }));

        failure.DiagnosticCode.ShouldBe(Malformed);
        TestContext.Out.WriteLine(failure.Message);
    }

    [TestCase("C:/cache/chunk_0001.asset")]
    [TestCase("C:chunk_0001.asset")]
    [TestCase("/chunk_0001.asset")]
    [TestCase("\\chunk_0001.asset")]
    [TestCase("\\\\server\\share\\chunk_0001.asset")]
    [TestCase("../chunk_0001.asset")]
    [TestCase("parts/../../chunk_0001.asset")]
    [TestCase("parts\\..\\..\\chunk_0001.asset")]
    [TestCase("parts//chunk_0001.asset")]
    [TestCase("")]
    public void Runtime_ChunkPathNotInCacheFolder_ThrowsInvalidChunkPath(string path)
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build();
        string[] paths = [.. build.Root.ChunkAssetPaths];
        paths[^1] = path;
        GaussianCache cache = new() { Header = build.Root.Header, RootChunkData = build.Root.RootChunkData, ChunkAssetPaths = paths };

        Should.Throw<GaussianCacheFormatException>(() => _ = cache.Runtime).DiagnosticCode.ShouldBe(InvalidChunkPath);
    }

    [Test]
    public void Runtime_ChunkPathInSubfolder_Decodes()
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build();
        string[] paths = [.. build.Root.ChunkAssetPaths.Select(path => "parts/" + path)];
        GaussianCache cache = new() { Header = build.Root.Header, RootChunkData = build.Root.RootChunkData, ChunkAssetPaths = paths };

        cache.Runtime.GetLoadedNodeCount(0).ShouldBe(build.Root.Header.SplatLevelCounts[0]);
    }

    [TestCase(typeof(EndOfStreamException))]
    [TestCase(typeof(ArgumentOutOfRangeException))]
    [TestCase(typeof(OverflowException))]
    public void ApplyChunk_DecodeReadFails_ThrowsDecodeFailed(Type exceptionType)
    {
        GaussianCacheBuildResult build = GaussianCacheTestSphere.Shared.Build();
        GaussianCacheRuntimeData runtime = GaussianCacheRuntimeData.Create(build.Root.Header);
        Exception fault = (Exception)Activator.CreateInstance(exceptionType)!;

        GaussianCacheFormatException failure = Should.Throw<GaussianCacheFormatException>(
            () => runtime.ApplyChunk(build.Root.RootChunkData, new FaultyDecodeBackend(GaussianCacheTestSphere.Meshopt, fault)));

        failure.DiagnosticCode.ShouldBe("GaussianCache.DecodeFailed");
        failure.Message.ShouldContain(exceptionType.Name);
    }

    private static string ReadCode(Action<GaussianCacheHeader> change)
    {
        GaussianCacheHeader header = CreateHeader();
        change(header);
        return Should.Throw<GaussianCacheFormatException>(() => Read(Write(header))).DiagnosticCode;
    }

    private static byte[] Write(GaussianCacheHeader header)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            header.Write(writer);
        return stream.ToArray();
    }

    private static GaussianCacheHeader Read(byte[] bytes)
    {
        using BinaryReader reader = new(new MemoryStream(bytes));
        return GaussianCacheHeader.Read(reader);
    }

    /// <summary>A valid header of an animated cache with three levels and five chunks.</summary>
    private static GaussianCacheHeader CreateHeader()
        => new()
        {
            MotionCodecVersion = 1,
            SplatLevelCounts = [2_000, 8_000, 30_000],
            StaticSplatCounts = [100, 400, 1_500],
            DriverVertexCounts = [500, 2_000, 6_000],
            DriverTriangleCounts = [900, 3_900, 11_800],
            FrameCount = 64,
            FrameRate = 30.0f,
            DurationSeconds = 64.0f / 30.0f,
            LoopMode = GaussianCacheLoopMode.PingPong,
            Bounds = new AABB(new Vector3(-1.0f, -2.0f, -3.0f), new Vector3(1.5f, 2.5f, 3.5f)),
            GridBits = 12,
            ColorScale = 1.75f,
            Chunks =
            [
                new GaussianCacheChunkInfo(0, GaussianCacheChunkKind.Root, 0, 0, 0, 7, new string('a', 64)),
                new GaussianCacheChunkInfo(1, GaussianCacheChunkKind.DriverTemporal, 0, 0, 1, 1_000, new string('0', 64)),
                new GaussianCacheChunkInfo(2, GaussianCacheChunkKind.DriverLevel, 1, 1, 0, 2_000, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
                new GaussianCacheChunkInfo(3, GaussianCacheChunkKind.SplatLevel, 1, 1, 0, 3_000, new string('f', 64)),
                new GaussianCacheChunkInfo(4, GaussianCacheChunkKind.DriverTemporal, 2, 2, 3, 4_000, new string('b', 64)),
            ],
        };

    /// <summary>A backend that throws a given exception from the vertex decode, and gives every other call to a real backend.</summary>
    private sealed class FaultyDecodeBackend(IMeshOptimizerBackend inner, Exception fault) : IMeshOptimizerBackend
    {
        public string VersionKey => inner.VersionKey;
        public nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles) => inner.BuildMeshletsBound(indexCount, maxVertices, maxTriangles);
        public nuint BuildNativeMeshletClusters(MeshletBuildMode buildMode, MeshOptimizerMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight)
            => inner.BuildNativeMeshletClusters(buildMode, meshlets, meshletVertices, meshletTriangles, indices, vertexPositions, vertexCount, maxVertices, minTriangles, maxTriangles, coneWeight, splitFactor, fillWeight);
        public void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level) => inner.OptimizeMeshletLevel(meshletVertices, meshletTriangles, level);
        public MeshOptimizerBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount)
            => inner.ComputeMeshletBounds(meshletVertices, meshletTriangles, vertexPositions, vertexCount);
        public int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles) => inner.EncodeMeshlet(vertices, triangles, maxVertices, maxTriangles);
        public int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError)
            => inner.Simplify(indices, positions, vertexCount, targetIndexCount, targetError, options, out resultError);
        public int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
            => inner.SimplifyWithAttributes(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
        public int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
            => inner.SimplifyWithUpdate(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
        public int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError)
            => inner.SimplifySloppy(indices, positions, vertexCount, targetIndexCount, targetError, vertexLock, out resultError);
        public float SimplifyScale(float[] positions, int vertexCount) => inner.SimplifyScale(positions, vertexCount);
        public int EncodeVertexBufferBound(int vertexCount, int vertexSize) => inner.EncodeVertexBufferBound(vertexCount, vertexSize);
        public int EncodeVertexBuffer(Span<byte> destination, ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize, int level = 3, int version = 1)
            => inner.EncodeVertexBuffer(destination, vertices, vertexCount, vertexSize, level, version);
        public void DecodeVertexBuffer(Span<byte> destination, int vertexCount, int vertexSize, ReadOnlySpan<byte> encoded) => throw fault;
        public int DecodeVertexVersion(ReadOnlySpan<byte> encoded) => inner.DecodeVertexVersion(encoded);
        public int EncodeIndexBufferBound(int indexCount, int vertexCount) => inner.EncodeIndexBufferBound(indexCount, vertexCount);
        public int EncodeIndexBuffer(Span<byte> destination, ReadOnlySpan<uint> indices) => inner.EncodeIndexBuffer(destination, indices);
        public void DecodeIndexBuffer(Span<uint> destination, ReadOnlySpan<byte> encoded) => inner.DecodeIndexBuffer(destination, encoded);
        public void OptimizeVertexCache(Span<uint> destination, ReadOnlySpan<uint> indices, int vertexCount) => inner.OptimizeVertexCache(destination, indices, vertexCount);
        public void EncodeFilterQuat(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> quaternionsXyzw)
            => inner.EncodeFilterQuat(destination, count, stride, bits, quaternionsXyzw);
        public void EncodeFilterExp(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> data, MeshOptimizerExpMode mode)
            => inner.EncodeFilterExp(destination, count, stride, bits, data, mode);
        public void EncodeFilterColor(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> rgba) => inner.EncodeFilterColor(destination, count, stride, bits, rgba);
        public void DecodeFilterQuat(Span<byte> buffer, int count, int stride) => inner.DecodeFilterQuat(buffer, count, stride);
        public void DecodeFilterExp(Span<byte> buffer, int count, int stride) => inner.DecodeFilterExp(buffer, count, stride);
        public void DecodeFilterColor(Span<byte> buffer, int count, int stride) => inner.DecodeFilterColor(buffer, count, stride);
        public void SpatialSortRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
            => inner.SpatialSortRemap(destination, positions, vertexCount, positionsStride);
        public void SpatialClusterPoints(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int clusterSize)
            => inner.SpatialClusterPoints(destination, positions, vertexCount, positionsStride, clusterSize);
        public void GeneratePositionRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
            => inner.GeneratePositionRemap(destination, positions, vertexCount, positionsStride);
        public int FilterIndexBuffer(Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<float> vertices, int vertexCount, int vertexSize, int vertexStride)
            => inner.FilterIndexBuffer(destination, indices, vertices, vertexCount, vertexSize, vertexStride);
    }
}
