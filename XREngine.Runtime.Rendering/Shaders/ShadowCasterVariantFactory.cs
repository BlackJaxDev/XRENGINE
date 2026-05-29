using XREngine.Data.Rendering;
using XREngine.Rendering;
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
        => CreatePointLightMaterialVariant(
            sourceMaterial,
            useGeometryShader
                ? EPointShadowMaterialKind.GeometryShader
                : EPointShadowMaterialKind.InstancedLayered);

    public static XRMaterial? CreatePointLightMaterialVariant(XRMaterial sourceMaterial, EPointShadowMaterialKind kind)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        List<XRShader> shaders = [];
        if (kind == EPointShadowMaterialKind.GeometryShader)
        {
            shaders.Add(XRShader.EngineShader("PointLightShadowDepth.gs", EShaderType.Geometry));
            shaders.Add(CreatePointLightFragmentVariant(sourceMaterial));
        }
        else if (kind == EPointShadowMaterialKind.AtlasGeometryShader)
        {
            shaders.Add(XRShader.EngineShader("PointLightAtlasShadowDepth.gs", EShaderType.Geometry));
            shaders.Add(CreatePointLightFragmentVariant(sourceMaterial));
        }
        else
        {
            XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
            if (fragmentShader is null)
                return null;

            XRShader? fragmentVariantShader = ShaderHelper.GetPointShadowCasterForwardVariant(fragmentShader);
            if (fragmentVariantShader is null)
                return null;

            shaders.Add(fragmentVariantShader);
        }

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
            PointShadowMaterialKind = kind,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };
        return variant;
    }

    private static XRShader CreatePointLightFragmentVariant(XRMaterial sourceMaterial)
    {
        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        XRShader? fragmentVariantShader = ShaderHelper.GetPointShadowCasterForwardVariant(fragmentShader);
        return fragmentVariantShader ?? XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment);
    }

    public static XRMaterial CreateDirectionalCascadeMaterialVariant(XRMaterial sourceMaterial, bool useGeometryShader)
        => CreateDirectionalCascadeMaterialVariant(
            sourceMaterial,
            useGeometryShader
                ? EDirectionalCascadeShadowMaterialKind.GeometryShader
                : EDirectionalCascadeShadowMaterialKind.InstancedLayered);

    public static XRMaterial CreateDirectionalCascadeMaterialVariant(
        XRMaterial sourceMaterial,
        EDirectionalCascadeShadowMaterialKind kind)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        List<XRShader> shaders = [];
        if (kind == EDirectionalCascadeShadowMaterialKind.GeometryShader)
            shaders.Add(XRShader.EngineShader("DirectionalCascadeShadowDepth.gs", EShaderType.Geometry));
        else if (kind == EDirectionalCascadeShadowMaterialKind.AtlasGeometryShader)
            shaders.Add(XRShader.EngineShader("DirectionalCascadeAtlasShadowDepth.gs", EShaderType.Geometry));

        XRShader fragmentShader = CreateDirectionalCascadeFragmentShader(sourceMaterial);
        shaders.Add(fragmentShader);

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
            DirectionalCascadeShadowMaterialKind = kind,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };
        return variant;
    }

    private static XRShader CreateDirectionalCascadeFragmentShader(XRMaterial sourceMaterial)
    {
        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        if (fragmentShader is null)
            return new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing);

        return ShaderHelper.GetShadowCasterForwardVariant(fragmentShader)
            ?? new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing);
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
            RequiredEngineUniforms = EUniformRequirements.Camera,
        };
}
