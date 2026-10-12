using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    /// <summary>
    /// Creates the existing opaque deferred PBR texture family with explicit surface metadata.
    /// Texture alpha does not affect coverage. Normal maps use the engine's flipped-Y RGB
    /// convention; metallic and roughness maps multiply their factors from the red channel.
    /// </summary>
    public static XRMaterial CreateLitPbrTextureMaterial(XRTexture2D baseColor, XRTexture2D? normal = null,
        XRTexture2D? metallic = null, XRTexture2D? roughness = null, ColorF4? tint = null,
        float specular = 1, float roughnessFactor = 1, float metallicFactor = 0, float emission = 0)
    {
        ArgumentNullException.ThrowIfNull(baseColor);
        ColorF4 color = tint ?? ColorF4.White;
        if (color.A != 1) throw new ArgumentException("Opaque textured materials require opacity exactly one.", nameof(tint));
        ShaderVar[] parameters = CreateDeferredLitDefaults(color, specular, roughnessFactor, metallicFactor, emission);
        if (normal is not null)
            parameters = [.. parameters, new ShaderInt(0, "NormalMapMode"), new ShaderFloat(0, "HeightMapScale")];
        List<MaterialSurfaceTextureBinding> bindings = [CreateStandardSurfaceBinding(EMaterialTextureSemantic.BaseColor, baseColor)];
        if (normal is not null) bindings.Add(CreateStandardSurfaceBinding(EMaterialTextureSemantic.Normal, normal));
        if (metallic is not null) bindings.Add(CreateStandardSurfaceBinding(EMaterialTextureSemantic.Metallic, metallic));
        if (roughness is not null) bindings.Add(CreateStandardSurfaceBinding(EMaterialTextureSemantic.Roughness, roughness));
        XRTexture?[] textures = roughness is not null
            ? normal is not null || metallic is not null ? [baseColor, normal, metallic, roughness] : [baseColor, null, roughness]
            : metallic is not null ? [baseColor, normal, metallic]
            : normal is not null ? [baseColor, normal] : [baseColor];
        return CreateStandardLitTextureMaterial(parameters, textures, [.. bindings]);
    }

    private static MaterialSurfaceTextureBinding CreateStandardSurfaceBinding(EMaterialTextureSemantic semantic, XRTexture2D texture)
        => new(semantic, texture, IsSrgb: semantic == EMaterialTextureSemantic.BaseColor,
            WrapU: texture.UWrap, WrapV: texture.VWrap);

    private static XRMaterial CreateStandardLitTextureMaterial(ShaderVar[] parameters, XRTexture?[] textures,
        MaterialSurfaceTextureBinding[] bindings)
    {
        XRMaterial material;
        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.DesktopGlsl)
        {
            bool normal = bindings.Any(binding => binding.Semantic == EMaterialTextureSemantic.Normal);
            bool metallic = bindings.Any(binding => binding.Semantic == EMaterialTextureSemantic.Metallic);
            bool roughness = bindings.Any(binding => binding.Semantic == EMaterialTextureSemantic.Roughness);
            XRShader shader = normal
                ? roughness ? ShaderHelper.LitTextureNormalRoughnessMetallicDeferred()
                    : metallic ? ShaderHelper.LitTextureNormalMetallicFragDeferred() : ShaderHelper.LitTextureNormalFragDeferred()
                : roughness && metallic ? ShaderHelper.LitTextureMetallicRoughnessDeferred()
                    : metallic ? ShaderHelper.LitTextureMetallicFragDeferred()
                    : roughness ? ShaderHelper.LitTextureRoughnessFragDeferred() : ShaderHelper.LitTextureFragDeferred()!;
            material = new(parameters, textures, shader);
        }
        else if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked)
            material = new(parameters, textures);
        else
            throw new InvalidOperationException("Unsupported built-in material construction target.");
        material.Parameters = parameters;
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        if (bindings.Length != 0)
            material.SurfaceTextureBindings = bindings;
        material.EngineSemantic = EngineMaterialSemanticIdentity.StandardLitTextureV1;
        return material;
    }
}
