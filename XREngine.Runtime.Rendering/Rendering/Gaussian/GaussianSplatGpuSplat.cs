using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Gaussian;

/// <summary>
/// One gaussian splat in the splat buffer of <see cref="GaussianSplatRenderer"/>. The layout is std430
/// and 64 bytes, and it matches the <c>GaussianSplat</c> struct in the gaussian splat shaders.
/// </summary>
/// <remarks>
/// The rotation maps the local frame of the splat (+X and +Y tangent axes, +Z normal axis) to model space.
/// The scale is the standard deviation (sigma) along each local axis, so the covariance is
/// R * diag(sigma^2) * transpose(R).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct GaussianSplatGpuSplat
{
    /// <summary>Center in model space (XYZ) and opacity from 0 to 1 (W).</summary>
    public Vector4 CenterOpacity;

    /// <summary>Unit quaternion (X, Y, Z, W) from the local frame of the splat to model space.</summary>
    public Vector4 Rotation;

    /// <summary>Standard deviation along each local axis (XYZ). The renderer does not use W.</summary>
    public Vector4 ScalePad;

    /// <summary>Linear RGB color (XYZ). The renderer does not use W.</summary>
    public Vector4 LinearColor;

    /// <summary>Creates a splat from its parts.</summary>
    public GaussianSplatGpuSplat(Vector3 center, float opacity, Quaternion rotation, Vector3 scale, Vector3 linearColor)
    {
        CenterOpacity = new Vector4(center, opacity);
        Rotation = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W);
        ScalePad = new Vector4(scale, 0f);
        LinearColor = new Vector4(linearColor, 1f);
    }
}
