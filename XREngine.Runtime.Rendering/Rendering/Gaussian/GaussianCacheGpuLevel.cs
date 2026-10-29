using System;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// One entry of the level table that the gaussian cache compute passes read. The layout is std430 and 32 bytes, and it
/// matches the <c>CacheLevel</c> struct (two <c>ivec4</c>) in <c>GaussianCacheDriverPose.comp</c> and
/// <c>GaussianCacheSplats.comp</c>. This type holds plain data. The fields are public by design.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GaussianCacheGpuLevel
{
    /// <summary>The index of the first record of the splat level in the concatenated record buffer.</summary>
    public int FirstRecord;

    /// <summary>The number of decoded nodes of the splat level. The decoded nodes are the first nodes of the level.</summary>
    public int LoadedNodeCount;

    /// <summary>The index of the first triangle index of the driver level in the concatenated index buffer.</summary>
    public int FirstDriverIndex;

    /// <summary>1 when the splat level can be drawn (see <see cref="GaussianCachePoseReference.IsLevelReady"/>), otherwise 0.</summary>
    public int Ready;

    /// <summary>The first driver vertex that the driver level adds.</summary>
    public int FirstDriverVertex;

    /// <summary>The number of driver vertices that the driver level adds.</summary>
    public int DriverVertexCount;

    /// <summary>The frame step of the driver level (see <see cref="GaussianCachePoseReference.DriverStep"/>). 0 means not decoded.</summary>
    public int DriverStep;

    /// <summary>Not used. It pads the entry to 32 bytes.</summary>
    public int Reserved;

    /// <summary>Fills the level table of a cache from its decoded data. The table has one entry for each splat level.</summary>
    /// <exception cref="ArgumentException"><paramref name="levels"/> does not have one entry for each splat level.</exception>
    public static void Fill(GaussianCache cache, Span<GaussianCacheGpuLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(cache);
        GaussianCacheRuntimeData runtime = cache.Runtime;
        DriverFrameStore frames = runtime.Frames;
        int levelCount = runtime.LevelRecords.Length;
        if (levels.Length != levelCount)
            throw new ArgumentException($"The table has {levels.Length} entries for {levelCount} splat levels.", nameof(levels));

        int firstRecord = 0;
        int firstIndex = 0;
        for (int level = 0; level < levelCount; level++)
        {
            bool hasDriver = level < frames.DriverLevelCount;
            levels[level] = new GaussianCacheGpuLevel
            {
                FirstRecord = firstRecord,
                LoadedNodeCount = runtime.GetLoadedNodeCount(level),
                FirstDriverIndex = firstIndex,
                Ready = GaussianCachePoseReference.IsLevelReady(runtime, level) ? 1 : 0,
                FirstDriverVertex = hasDriver ? frames.GetLevelFirstVertex(level) : 0,
                DriverVertexCount = hasDriver ? frames.GetLevelVertexCount(level) : 0,
                DriverStep = hasDriver ? GaussianCachePoseReference.DriverStep(frames, level) : 0,
            };
            firstRecord += runtime.LevelRecords[level].Length;
            if (level < runtime.DriverLevelIndices.Length)
                firstIndex += runtime.DriverLevelIndices[level].Length;
        }
    }
}
