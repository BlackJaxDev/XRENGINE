using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Frame lighting and sky in linear color. Light direction points from the surface toward the light.</summary>
public readonly record struct BrowserPipelineEnvironment(
    Matrix4x4 ViewProjection, Matrix4x4 ShadowViewProjection, Vector3 LightDirection,
    float LightIntensity, Vector3 Ambient, float Exposure, Vector4 SkyTop, Vector4 SkyBottom)
{
    public static BrowserPipelineEnvironment Default => new(Matrix4x4.Identity, Matrix4x4.Identity,
        Vector3.Normalize(new Vector3(0.5f, 1, 0.5f)), 1, new Vector3(0.15f), 1,
        new Vector4(0.04f, 0.09f, 0.18f, 1), new Vector4(0.35f, 0.45f, 0.6f, 1));
}
