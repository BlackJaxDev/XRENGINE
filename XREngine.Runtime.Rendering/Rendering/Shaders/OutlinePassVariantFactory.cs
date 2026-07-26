using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Creates the material used by an inverse-hull outline companion draw.
/// Authored parameters and textures are shared with the source material while
/// the shader set and fixed-function state remain pass-specific.
/// </summary>
public static class OutlinePassVariantFactory
{
    private const string OutlinePassDefine = "XRENGINE_OUTLINE_PASS";

    public static XRMaterial? CreateMaterialVariant(XRMaterial sourceMaterial)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        if (!sourceMaterial.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition pass) ||
            !pass.Enabled)
        {
            return null;
        }

        XRShader? sourceFragment = sourceMaterial.GetShader(EShaderType.Fragment);
        XRShader? fragment = ShaderHelper.CreateDefinedShaderVariant(sourceFragment, OutlinePassDefine);
        if (fragment is null)
            return null;

        XRShader monoVertex = CreateVertexVariant("UberShader.vert");
        XRShader ovrVertex = CreateVertexVariant("UberShader_OVR.vert");
        XRShader nvVertex = CreateVertexVariant("UberShader_NV.vert");

        XRMaterial variant = new(monoVertex, ovrVertex, nvVertex, fragment)
        {
            Name = string.Concat(sourceMaterial.Name, " [Outline]"),
            Parameters = sourceMaterial.Parameters,
            Textures = sourceMaterial.Textures,
            RenderPass = pass.RenderPass,
            BillboardMode = sourceMaterial.BillboardMode,
            AlphaCutoff = sourceMaterial.AlphaCutoff,
            TransparencyMode = sourceMaterial.TransparencyMode,
            TransparentTechniqueOverride = sourceMaterial.TransparentTechniqueOverride,
            TransparentSortPriority = sourceMaterial.TransparentSortPriority,
            UberAuthoredState = sourceMaterial.UberAuthoredState,
            RenderOptions = pass.RenderOptions,
        };

        variant.SettingUniforms += (_, program) => sourceMaterial.OnSettingUniforms(program);
        return variant;
    }

    private static XRShader CreateVertexVariant(string fileName)
    {
        XRShader source = ShaderHelper.LoadEngineShader(Path.Combine("Uber", fileName), EShaderType.Vertex);
        return ShaderHelper.CreateDefinedShaderVariant(source, OutlinePassDefine) ?? source;
    }
}
