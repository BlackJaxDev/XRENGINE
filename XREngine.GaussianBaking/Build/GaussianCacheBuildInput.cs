using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Lod;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

/// <summary>Holds the baked parts that <see cref="GaussianCacheBuilder"/> writes into one cache.</summary>
/// <remarks>
/// Splat level <c>k</c> binds to driver level <c>k</c>. When the content has no driver level, every splat must be
/// static, and the cache has no driver data and no motion.
/// </remarks>
public sealed class GaussianCacheBuildInput
{
    public required GaussianBakeKind Kind { get; init; }

    /// <summary>The splat levels. In each level the static splats are first.</summary>
    public required SplatLodTree Tree { get; init; }

    /// <summary>The driver levels. There are none, or one for each splat level.</summary>
    public required DriverHierarchy Drivers { get; init; }

    /// <summary>
    /// For each splat level, the bindings of the animated splats of that level, in their order in the level.
    /// Static splats have no binding. The array is empty when there is no driver level.
    /// </summary>
    public required SplatBinding[][] BindingsPerLevel { get; init; }

    /// <summary>
    /// The rest position of each driver vertex, in coarse-first order. These are the positions that the bindings were
    /// made with. The builder uses them to keep each rotation correct when the index codec turns the corners of a triangle.
    /// </summary>
    public required Vector3[] DriverRestPositions { get; init; }

    /// <summary>The quantized driver vertex positions of every frame, on the grid of <see cref="Bounds"/> and <see cref="GridBits"/>.</summary>
    public required DriverFrameStore Frames { get; init; }

    public required BakeMotionChoice Motion { get; init; }

    /// <summary>The loop mode. It must be the loop mode of <see cref="Frames"/>.</summary>
    public required GaussianCacheLoopMode LoopMode { get; init; }

    /// <summary>The box that holds every driver vertex in every frame and every static splat.</summary>
    public required AABB Bounds { get; init; }

    /// <summary>The number of grid bits on each axis, from 1 to 16.</summary>
    public required byte GridBits { get; init; }

    /// <summary>The value that the splat colors were divided by. It is at least 1.</summary>
    public float ColorScale { get; init; } = 1.0f;

    public GaussianCacheCameraHints? CameraHints { get; init; }
}
