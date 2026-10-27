using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Baking.Sampling;

/// <summary>One gaussian splat of a bake, in world space at the rest pose.</summary>
public struct BakedSplat
{
    /// <summary>The center of the splat.</summary>
    public Vector3 Position;

    /// <summary>The rotation of the splat axes.</summary>
    public Quaternion Rotation;

    /// <summary>The standard deviation along each splat axis, in meters.</summary>
    public Vector3 Scale;

    /// <summary>The color in linear RGB.</summary>
    public Vector3 LinearColor;

    /// <summary>The opacity, from 0 to 1.</summary>
    public float Opacity;

    /// <summary>The unit normal of the source triangle.</summary>
    public Vector3 Normal;

    /// <summary>The index of the source triangle in the surface. The value is -1 for a merged parent splat.</summary>
    public int SourceTriangle;

    /// <summary>True when the splat does not move.</summary>
    public bool IsStatic;
}
