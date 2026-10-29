using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>
/// The pose of one chosen node of a gaussian cache at one time, in the space of the cache.
/// <see cref="GaussianCachePoseReference"/> makes it.
/// </summary>
/// <param name="Level">The splat level of the node. Level 0 is the coarsest.</param>
/// <param name="Node">The index of the node in its level.</param>
/// <param name="Position">The center of the splat.</param>
/// <param name="Rotation">The unit quaternion from the local frame of the splat to the space of the cache.</param>
/// <param name="Scale">The standard deviation (sigma) along each local axis.</param>
/// <param name="LinearColor">The linear RGB color multiplied by the color scale of the cache (XYZ), and the opacity (W).</param>
public readonly record struct GaussianSplatPose(int Level, int Node, Vector3 Position, Quaternion Rotation, Vector3 Scale, Vector4 LinearColor);
