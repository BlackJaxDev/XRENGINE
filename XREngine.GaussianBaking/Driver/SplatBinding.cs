using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>
/// Attaches one splat to one driver triangle. <c>TriangleFrame.Reconstruct(a, b, c, U, V, H)</c> gives the splat
/// center from the triangle corners, and <c>frame × LocalRotation</c> gives the splat rotation, where
/// <c>frame</c> comes from <see cref="TriangleFrame.TryCompute"/>.
/// </summary>
public struct SplatBinding
{
    /// <summary>The index of the triangle in the triangle list of the driver level.</summary>
    public uint Triangle;

    /// <summary>The plane coordinate along b − a. It is not clamped, so it can be outside the triangle.</summary>
    public float U;

    /// <summary>The plane coordinate along c − a. It is not clamped, so it can be outside the triangle.</summary>
    public float V;

    /// <summary>The signed distance from the triangle plane along the triangle normal, in meters.</summary>
    public float H;

    /// <summary>The splat rotation relative to the triangle frame.</summary>
    public Quaternion LocalRotation;
}
