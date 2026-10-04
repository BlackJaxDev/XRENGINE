using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>Exact forward diffuse, normal/height, specular and optional opacity inputs.</summary>
public readonly record struct AuthoredTexturedSurface(
    StandardLitColorSurface Values,
    MaterialSurfaceTextureBinding BaseColor,
    MaterialSurfaceTextureBinding? Normal,
    MaterialSurfaceTextureBinding? Specular,
    MaterialSurfaceTextureBinding? Opacity,
    float MatShininess,
    int NormalMapMode,
    float HeightMapScale)
{
    public int TextureFlags => (Normal is null ? 0 : 1) | (Specular is null ? 0 : 2) | (Opacity is null ? 0 : 4);

    public string VertexProfile => "position-normal-optional-tangent-uv-v1";

    /// <summary>Canonical source identity; opacity-only surfaces retain their separate existing family.</summary>
    public string DesktopFragmentPath => TextureFlags switch
    {
        1 => "Common/LitTexturedNormalForward.fs",
        2 => "Common/LitTexturedSpecForward.fs",
        3 => "Common/LitTexturedNormalSpecForward.fs",
        5 => "Common/LitTexturedNormalAlphaForward.fs",
        6 => "Common/LitTexturedSpecAlphaForward.fs",
        7 => "Common/LitTexturedNormalSpecAlphaForward.fs",
        _ => throw new InvalidOperationException("Authored textured surfaces require a normal or specular image."),
    };
}
