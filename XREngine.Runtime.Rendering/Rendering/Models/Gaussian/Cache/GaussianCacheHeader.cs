using XREngine.Data.Geometry;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Describes a cache: its counts, timing, bounds, camera hints, and chunks.</summary>
public sealed partial class GaussianCacheHeader
{
    public const ushort CurrentFormatVersion = 1;

    public ushort FormatVersion { get; set; } = CurrentFormatVersion;
    public GaussianCacheRepresentation Representation { get; set; } = GaussianCacheRepresentation.BakedDriver;
    public ushort MotionCodecVersion { get; set; }

    /// <summary>The splat count of each splat level.</summary>
    public int[] SplatLevelCounts { get; set; } = [];

    /// <summary>The count of splats that do not move, for each splat level.</summary>
    public int[] StaticSplatCounts { get; set; } = [];

    /// <summary>The cumulative driver vertex count of each driver level.</summary>
    public int[] DriverVertexCounts { get; set; } = [];

    /// <summary>The triangle count of each driver level. Each level has its own full triangle list.</summary>
    public int[] DriverTriangleCounts { get; set; } = [];

    public int FrameCount { get; set; }
    public float FrameRate { get; set; }
    public float DurationSeconds { get; set; }
    public GaussianCacheLoopMode LoopMode { get; set; }
    public AABB Bounds { get; set; }
    public byte GridBits { get; set; }
    public float ColorScale { get; set; } = 1.0f;
    public GaussianCacheCameraHints? CameraHints { get; set; }
    public GaussianCacheChunkInfo[] Chunks { get; set; } = [];
}
