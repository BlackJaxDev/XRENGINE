using System;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>
/// Holds the decoded data of a cache on the CPU: the splat records, the tree links, the driver triangles, and the driver
/// frames. Each accepted chunk adds to it, and the renderer uploads it to the GPU. The arrays have their full size from
/// the start. The loaded flags and the frame store tell which parts hold decoded data.
/// </summary>
public sealed partial class GaussianCacheRuntimeData
{
    private readonly int[] _staticCounts;
    private readonly int[] _driverVertexCounts;
    private readonly int[] _loadedNodeCounts;
    private readonly uint[] _childPrefix;
    private readonly int[] _loadedTriangleCounts;

    private GaussianCacheRuntimeData(GaussianCacheHeader header, DriverFrameStore frames)
    {
        int levelCount = header.SplatLevelCounts.Length;
        _staticCounts = [.. header.StaticSplatCounts];
        _driverVertexCounts = [.. header.DriverVertexCounts];
        _loadedNodeCounts = new int[levelCount];
        _childPrefix = new uint[levelCount];
        _loadedTriangleCounts = new int[header.DriverTriangleCounts.Length];

        LevelRecords = new GaussianCacheSplatRecord[levelCount][];
        LevelNodeLoaded = new bool[levelCount][];
        ParentIndex = new int[levelCount][];
        for (int level = 0; level < levelCount; level++)
        {
            int count = header.SplatLevelCounts[level];
            LevelRecords[level] = new GaussianCacheSplatRecord[count];
            LevelNodeLoaded[level] = new bool[count];
            ParentIndex[level] = new int[count];
            Array.Fill(ParentIndex[level], -1);
        }

        DriverLevelIndices = new uint[header.DriverTriangleCounts.Length][];
        for (int level = 0; level < DriverLevelIndices.Length; level++)
            DriverLevelIndices[level] = new uint[header.DriverTriangleCounts[level] * 3];

        Frames = frames;
    }

    /// <summary>The splat records of each level. Level 0 is the coarsest.</summary>
    public GaussianCacheSplatRecord[][] LevelRecords { get; }

    /// <summary>For each level, true for each node whose record is decoded.</summary>
    public bool[][] LevelNodeLoaded { get; }

    /// <summary>
    /// For each level, the index of the parent of each node in the level before it. The value is -1 for each node of
    /// level 0, and for each node whose parent record is not decoded yet.
    /// </summary>
    public int[][] ParentIndex { get; }

    /// <summary>The triangle list of each driver level, as indices into the driver vertices.</summary>
    public uint[][] DriverLevelIndices { get; }

    /// <summary>The quantized driver vertex positions of each frame, and the parts that are decoded.</summary>
    public DriverFrameStore Frames { get; }

    /// <summary>
    /// Returns the number of decoded nodes of a splat level. The decoded nodes are always the first nodes of the level,
    /// so a node is decoded when its index is less than this number.
    /// </summary>
    public int GetLoadedNodeCount(int level) => _loadedNodeCounts[level];

    /// <summary>
    /// Returns the number of decoded triangles of a driver level. The decoded triangles are always the first triangles
    /// of the level.
    /// </summary>
    public int GetLoadedTriangleCount(int driverLevel) => _loadedTriangleCounts[driverLevel];

    /// <summary>Makes empty runtime data for a header and checks that its counts agree and obey the limits of the format.</summary>
    /// <exception cref="GaussianCacheFormatException">The counts of the header do not agree or are above a limit.</exception>
    internal static GaussianCacheRuntimeData Create(GaussianCacheHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        // The limits of the format bound every allocation below.
        header.Validate();
        int[] counts = header.SplatLevelCounts;
        int[] staticCounts = header.StaticSplatCounts;
        if (counts.Length == 0 || counts.Length > byte.MaxValue)
            throw Malformed("The header must have from 1 to 255 splat levels.");
        if (staticCounts.Length != counts.Length)
            throw Malformed("The header must have one static count for each splat level.");
        for (int level = 0; level < counts.Length; level++)
        {
            if (counts[level] <= 0 || staticCounts[level] < 0 || staticCounts[level] > counts[level])
                throw Malformed($"The splat counts of level {level} are not valid.");
        }

        int driverLevels = header.DriverVertexCounts.Length;
        if (header.DriverTriangleCounts.Length != driverLevels || (driverLevels != 0 && driverLevels != counts.Length))
            throw Malformed("The header must have no driver level, or one driver level with a triangle count for each splat level.");
        foreach (int triangleCount in header.DriverTriangleCounts)
        {
            if (triangleCount < 0 || triangleCount > int.MaxValue / 3)
                throw Malformed("A driver triangle count is not valid.");
        }

        DriverFrameStore frames;
        try
        {
            frames = new DriverFrameStore(header.DriverVertexCounts, header.FrameCount, header.LoopMode);
        }
        catch (ArgumentException exception)
        {
            throw Malformed("The driver vertex counts or the frame count are not valid: " + exception.Message);
        }

        return new GaussianCacheRuntimeData(header, frames);
    }

    private static GaussianCacheFormatException Malformed(string message)
        => new(GaussianCacheFormatException.MalformedCode, message);
}
