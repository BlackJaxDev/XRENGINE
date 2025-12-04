using System;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

public static class UIRenderResources
{
    private static readonly Lazy<XRMaterial> _uiUberMaterial = new(CreateUIUberMaterial);

    public static XRMaterial UIUberMaterial => _uiUberMaterial.Value;

    private static XRMaterial CreateUIUberMaterial()
    {
        RenderingParameters renderParameters = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new()
            {
                Enabled = ERenderParamUsage.Disabled,
                Function = EComparison.Always
            },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };

        ShaderVar[] parameters =
        [
            new ShaderVector4(ColorF4.White, "BaseColor"),
            new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 0.0f), "GradientColor"),
            new ShaderVector4(new Vector4(0.0f, 0.0f, 1.0f, 1.0f), "GradientRange"),
            new ShaderVector4(ColorF4.Transparent, "InnerStroke"),
            new ShaderVector4(ColorF4.Transparent, "MiddleStroke"),
            new ShaderVector4(ColorF4.Transparent, "OuterStroke"),
            new ShaderVector4(ColorF4.Transparent, "InnerGlowColor"),
            new ShaderVector4(ColorF4.Transparent, "OuterGlowColor"),
            new ShaderVector4(Vector4.Zero, "GlowParams"),
            new ShaderVector4(ColorF4.Transparent, "InnerShadowColor"),
            new ShaderVector4(ColorF4.Transparent, "OuterShadowColor"),
            new ShaderVector4(Vector4.Zero, "ShadowAnglesDist"),
            new ShaderVector4(Vector4.Zero, "ShadowRadii"),
            new ShaderVector4(Vector4.Zero, "GradientFeather"),
            new ShaderFloat(0.0f, "CornerRadius"),
            new ShaderFloat(1.0f, "TextureOpacity"),
            new ShaderFloat(0.0f, "UseTexture"),
            new ShaderFloat(0.0f, "AlphaCutoff"),
            new ShaderFloat(0.0f, "UseInstanceData"),
            new ShaderInt(0, "InstanceDataOffset"),
            new ShaderFloat(2.0f, "PrimitivesPerInstance"),
        ];

        XRMaterial material = new(parameters, ShaderHelper.UIUberFragment()!)
        {
            RenderPass = (int)EDefaultRenderPass.OnTopForward,
            Name = "UI Uber Material",
            RenderOptions = renderParameters
        };

        material.SettingUniforms += (XRMaterialBase mat, XRRenderProgram program) =>
        {
            if (mat is XRMaterial typed)
            {
                bool hasTexture = typed.Textures?.Count > 0 && typed.Textures[0] is not null;
                program.Uniform("UseTexture", hasTexture ? 1.0f : 0.0f);
            }
        };

        return material;
    }
}
