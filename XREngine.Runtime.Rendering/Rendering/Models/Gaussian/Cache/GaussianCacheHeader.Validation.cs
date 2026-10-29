using System;
using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// The limits of the cache format and the checks that a header obeys them.
public sealed partial class GaussianCacheHeader
{
    /// <summary>The meshoptimizer vertex codec version of every encoded stream of the format.</summary>
    public const ushort CurrentMotionCodecVersion = 1;

    /// <summary>The largest number of splat levels. A section names its level in one byte.</summary>
    public const int MaxSplatLevels = byte.MaxValue;

    /// <summary>
    /// The largest number of splat nodes in all levels: 2^24, the largest capacity of the splat renderer
    /// (<c>GaussianSplatRenderer.MaxCapacity</c>). The largest bake budget (a world) has 300,000 leaves and fewer than
    /// 350,000 nodes.
    /// </summary>
    public const int MaxTotalSplatNodes = 1 << 24;

    /// <summary>The largest number of driver vertices: 2^20. The largest bake budget (a world) has 16,000.</summary>
    public const int MaxDriverVertices = 1 << 20;

    /// <summary>
    /// The largest number of driver triangles in all driver levels: 2^22. Each level has about two triangles for each
    /// vertex, so the limit leaves space for 2^20 vertices in more than one level.
    /// </summary>
    public const int MaxTotalDriverTriangles = 1 << 22;

    /// <summary>
    /// The largest number of stored frames: 4,096, more than two minutes at 30 frames per second. The largest bake
    /// budget is 10 s at 30 frames per second: 304 frames.
    /// </summary>
    public const int MaxFrameCount = 4096;

    /// <summary>
    /// The largest number of stored driver positions: driver vertices × frames ≤ 2^24. A position uses 6 bytes, so the
    /// frame store is at most 96 MiB. The largest bake budget is 16,000 × 304 = 4,864,000 positions.
    /// </summary>
    public const long MaxDriverFramePositions = 1L << 24;

    /// <summary>
    /// The largest number of chunks: 2^14. A cache of the largest bake budget (48 MiB) in chunks of the smallest chunk
    /// data limit of the builder (4 KiB) has 12,288 chunks.
    /// </summary>
    public const int MaxChunkCount = 1 << 14;

    /// <summary>The largest data size of one chunk: 4 MiB, the chunk payload limit of the format.</summary>
    public const int MaxChunkBytes = 4 * 1024 * 1024;

    /// <summary>The length of a chunk hash: 64 lowercase hexadecimal characters of a SHA-256 hash.</summary>
    public const int ChunkHashLength = 64;

    /// <summary>
    /// Checks that the header obeys the limits of the format, so that a reader can allocate the arrays of the header
    /// without a large allocation from a small file. <see cref="Read"/> and the runtime decode call it.
    /// </summary>
    /// <exception cref="GaussianCacheFormatException">
    /// The format version, the representation, or the motion codec version is not supported, or a field is out of its
    /// range (<see cref="GaussianCacheFormatException.MalformedCode"/>), or a count is above a limit of the format
    /// (<see cref="GaussianCacheFormatException.LimitExceededCode"/>).
    /// </exception>
    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.UnsupportedFormatVersionCode,
                $"Gaussian cache format version {FormatVersion} is not supported. The supported version is {CurrentFormatVersion}.");
        if (Representation != GaussianCacheRepresentation.BakedDriver)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.UnsupportedRepresentationCode,
                $"Gaussian cache representation {(byte)Representation} is not supported.");
        if (MotionCodecVersion != CurrentMotionCodecVersion)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.UnsupportedMotionCodecVersionCode,
                $"Gaussian cache motion codec version {MotionCodecVersion} is not supported. The supported version is {CurrentMotionCodecVersion}.");

        ValidateSplatCounts();
        ValidateDriverCounts();

        if (FrameCount != 1 && (FrameCount <= 0 || (FrameCount & 7) != 0))
            throw Malformed($"the frame count {FrameCount} is not 1 or a positive multiple of 8");
        if (FrameCount > MaxFrameCount)
            throw Limit($"the frame count {FrameCount} is more than {MaxFrameCount}");
        long driverVertices = DriverVertexCounts.Length > 0 ? DriverVertexCounts[^1] : 0;
        if (driverVertices * FrameCount > MaxDriverFramePositions)
            throw Limit($"{driverVertices} driver vertices in {FrameCount} frames are more than {MaxDriverFramePositions} positions");
        if (!float.IsFinite(FrameRate) || FrameRate <= 0.0f)
            throw Malformed($"the frame rate {FrameRate} is not finite and more than 0");
        if (!float.IsFinite(DurationSeconds) || DurationSeconds < 0.0f)
            throw Malformed($"the duration {DurationSeconds} is not finite and 0 or more");
        if (LoopMode is not (GaussianCacheLoopMode.Loop or GaussianCacheLoopMode.PingPong or GaussianCacheLoopMode.Once))
            throw Malformed($"the loop mode {(byte)LoopMode} is not defined");
        if (!IsFinite(Bounds.Min) || !IsFinite(Bounds.Max) || Vector3.Max(Bounds.Min, Bounds.Max) != Bounds.Max)
            throw Malformed("the bounds are not finite, or the minimum is more than the maximum");
        if (GridBits is < 1 or > GridQuantizer.MaxGridBits)
            throw Malformed($"the grid bits {GridBits} are not from 1 to {GridQuantizer.MaxGridBits}");
        if (!float.IsFinite(ColorScale) || ColorScale <= 0.0f)
            throw Malformed($"the color scale {ColorScale} is not finite and more than 0");

        ValidateChunks();
    }

    private void ValidateSplatCounts()
    {
        int[] counts = SplatLevelCounts ?? [];
        int[] staticCounts = StaticSplatCounts ?? [];
        if (counts.Length == 0 || counts.Length > MaxSplatLevels)
            throw Malformed($"the header has {counts.Length} splat levels, not from 1 to {MaxSplatLevels}");
        if (staticCounts.Length != counts.Length)
            throw Malformed("the header does not have one static count for each splat level");

        long total = 0;
        for (int level = 0; level < counts.Length; level++)
        {
            if (counts[level] <= 0 || staticCounts[level] < 0 || staticCounts[level] > counts[level])
                throw Malformed($"the splat counts of level {level} are not valid");
            total += counts[level];
        }

        if (total > MaxTotalSplatNodes)
            throw Limit($"{total} splat nodes are more than {MaxTotalSplatNodes}");
    }

    private void ValidateDriverCounts()
    {
        int[] vertices = DriverVertexCounts ?? [];
        int[] triangles = DriverTriangleCounts ?? [];
        if (triangles.Length != vertices.Length || (vertices.Length != 0 && vertices.Length != (SplatLevelCounts?.Length ?? 0)))
            throw Malformed("the header must have no driver level, or one driver level with a triangle count for each splat level");

        int previous = 0;
        long totalTriangles = 0;
        for (int level = 0; level < vertices.Length; level++)
        {
            if (vertices[level] < previous)
                throw Malformed($"the cumulative driver vertex count of level {level} is less than the count of the level before it");
            if (triangles[level] < 0)
                throw Malformed($"the driver triangle count of level {level} is negative");
            previous = vertices[level];
            totalTriangles += triangles[level];
        }

        if (previous > MaxDriverVertices)
            throw Limit($"{previous} driver vertices are more than {MaxDriverVertices}");
        if (totalTriangles > MaxTotalDriverTriangles)
            throw Limit($"{totalTriangles} driver triangles are more than {MaxTotalDriverTriangles}");
    }

    private void ValidateChunks()
    {
        GaussianCacheChunkInfo[] chunks = Chunks ?? [];
        if (chunks.Length == 0)
            throw Malformed("the chunk table is empty");
        if (chunks.Length > MaxChunkCount)
            throw Limit($"{chunks.Length} chunks are more than {MaxChunkCount}");

        int splatLevels = SplatLevelCounts.Length;
        int driverLevels = Math.Max(1, DriverVertexCounts.Length);
        for (int index = 0; index < chunks.Length; index++)
        {
            GaussianCacheChunkInfo chunk = chunks[index];
            if (chunk.Index != index)
                throw Malformed($"entry {index} of the chunk table has the index {chunk.Index}");
            if (chunk.Kind is not (GaussianCacheChunkKind.Root or GaussianCacheChunkKind.DriverLevel or GaussianCacheChunkKind.SplatLevel or GaussianCacheChunkKind.DriverTemporal))
                throw Malformed($"chunk {index} has the kind {(byte)chunk.Kind}, which is not defined");
            if ((chunk.Kind == GaussianCacheChunkKind.Root) != (index == 0))
                throw Malformed("chunk 0, and only chunk 0, must be the root chunk");
            if (chunk.SplatLevel >= splatLevels || chunk.DriverLevel >= driverLevels || chunk.TemporalLevel >= TemporalLevels.Count)
                throw Malformed($"chunk {index} names a level that does not exist");
            if (chunk.ByteLength < 0 || chunk.ByteLength > MaxChunkBytes)
                throw Limit($"chunk {index} has {chunk.ByteLength} bytes, which is not from 0 to {MaxChunkBytes}");
            if (!IsHash(chunk.Sha256))
                throw Malformed($"chunk {index} does not have a SHA-256 hash of {ChunkHashLength} lowercase hexadecimal characters");
        }
    }

    private static bool IsHash(string? hash)
    {
        if (hash is null || hash.Length != ChunkHashLength)
            return false;
        foreach (char character in hash)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static GaussianCacheFormatException Limit(string reason)
        => new(GaussianCacheFormatException.LimitExceededCode, $"Gaussian cache header is above a limit of the format: {reason}.");
}
