using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

public static class ShadowCasterVariantFactory
{
    public static XRMaterial? CreateMaterialVariant(XRMaterial sourceMaterial)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        if (fragmentShader is null)
            return null;

        XRShader? fragmentVariantShader = ShaderHelper.GetShadowCasterForwardVariant(fragmentShader);
        if (fragmentVariantShader is null)
            return null;

        List<XRShader> shaders =
        [
            .. sourceMaterial.Shaders.Where(shader => shader is not null && shader.Type != EShaderType.Fragment),
            fragmentVariantShader,
        ];

        XRMaterial variant = new(shaders)
        {
            Parameters = [],
            Textures = [],
            RenderPass = sourceMaterial.RenderPass,
            BillboardMode = sourceMaterial.BillboardMode,
            AlphaCutoff = sourceMaterial.AlphaCutoff,
            TransparencyMode = sourceMaterial.TransparencyMode,
            TransparentTechniqueOverride = sourceMaterial.TransparentTechniqueOverride,
            TransparentSortPriority = sourceMaterial.TransparentSortPriority,
            ShadowBindingSourceMaterial = sourceMaterial,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };
        return variant;
    }

    public static XRMaterial? CreatePointLightMaterialVariant(XRMaterial sourceMaterial, bool useGeometryShader)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        if (fragmentShader is null)
            return null;

        XRShader? fragmentVariantShader = ShaderHelper.GetPointShadowCasterForwardVariant(fragmentShader);
        if (fragmentVariantShader is null)
            return null;

        List<XRShader> shaders = [];
        if (useGeometryShader)
            shaders.Add(XRShader.EngineShader("PointLightShadowDepth.gs", EShaderType.Geometry));

        shaders.Add(fragmentVariantShader);

        XRMaterial variant = new(shaders)
        {
            Parameters = [],
            Textures = [],
            RenderPass = sourceMaterial.RenderPass,
            BillboardMode = sourceMaterial.BillboardMode,
            AlphaCutoff = sourceMaterial.AlphaCutoff,
            TransparencyMode = sourceMaterial.TransparencyMode,
            TransparentTechniqueOverride = sourceMaterial.TransparentTechniqueOverride,
            TransparentSortPriority = sourceMaterial.TransparentSortPriority,
            ShadowBindingSourceMaterial = sourceMaterial,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };
        return variant;
    }

    private static RenderingParameters CreateRenderOptions(RenderingParameters? source)
        => new()
        {
            CullMode = ECullMode.None,
            AlphaToCoverage = ERenderParamUsage.Disabled,
            BlendModeAllDrawBuffers = BlendMode.Disabled(),
            BlendModesPerDrawBuffer = null,
            DepthTest = new DepthTest()
            {
                Enabled = source?.DepthTest?.Enabled ?? ERenderParamUsage.Enabled,
                Function = source?.DepthTest?.Function ?? EComparison.Lequal,
                UpdateDepth = true,
            },
            RequiredEngineUniforms = EUniformRequirements.None,
        };
}
