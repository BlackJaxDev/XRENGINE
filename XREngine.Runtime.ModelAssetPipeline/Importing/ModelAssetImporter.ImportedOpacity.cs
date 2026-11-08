using System;
using System.Collections.Generic;
using System.Numerics;
using Assimp;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using BlendMode = XREngine.Rendering.Models.Materials.BlendMode;

namespace XREngine
{
    public partial class ModelAssetImporter
    {
        /// <summary>An imported material with an opacity at or above this value is opaque.</summary>
        internal const float ImportedOpaqueThreshold = 0.999f;

        /// <summary>
        /// Uniform that the lit forward texture shader multiplies into its output alpha as
        /// <c>1 - MaterialTransparency</c>. An unset uniform reads 0, so other users of the
        /// shader stay fully opaque.
        /// </summary>
        internal const string MaterialTransparencyParameterName = "MaterialTransparency";

        private const string AI_MATKEY_TRANSPARENCYFACTOR = "$mat.transparencyfactor";

        /// <summary>
        /// Resolves the material-level opacity of an imported material. An explicit opacity
        /// wins. Otherwise the FBX rule applies: transparency is the transparency factor times
        /// the transparent color, and a missing transparent color counts as white. A
        /// transparent color with a zero factor is opaque.
        /// </summary>
        internal static float ResolveImportedOpacity(float? opacity, float? transparencyFactor, Vector3? transparentColor)
        {
            float value = opacity ?? (transparencyFactor is { } factor
                ? 1.0f - factor * MaxComponent(transparentColor ?? Vector3.One)
                : 1.0f);
            return float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 1.0f;
        }

        private static float MaxComponent(Vector3 value)
            => MathF.Max(value.X, MathF.Max(value.Y, value.Z));

        /// <summary>Reads the material-level opacity from Assimp material keys.</summary>
        private static float ResolveImportedOpacity(Dictionary<string, List<MaterialProperty>> properties)
        {
            float? opacity = TryGetImportedFloat(properties, AI_MATKEY_OPACITY);
            float? factor = TryGetImportedFloat(properties, AI_MATKEY_TRANSPARENCYFACTOR);
            Vector3? color = properties.TryGetValue(AI_MATKEY_COLOR_TRANSPARENT, out List<MaterialProperty>? values) && values.Count > 0
                ? values[0].GetVector3Value()
                : null;
            return ResolveImportedOpacity(opacity, factor, color);
        }

        private static float? TryGetImportedFloat(Dictionary<string, List<MaterialProperty>> properties, string key)
        {
            if (!properties.TryGetValue(key, out List<MaterialProperty>? values) || values.Count == 0)
                return null;

            float value = values[0].GetFloatValue();
            return float.IsFinite(value) ? value : null;
        }

        /// <summary>
        /// Applies the material-level opacity of an imported material. A forward transparent
        /// texture material keeps its shader and gets the transparency uniform. An untextured
        /// color material with an opacity below <see cref="ImportedOpaqueThreshold"/> becomes
        /// lit forward glass in the sorted-alpha transparent pass, with the opacity in the
        /// color alpha. Other materials do not change.
        /// </summary>
        internal static void ApplyImportedOpacity(XRMaterial material, float opacity, Vector3 baseColor)
        {
            if (material.Parameter<ShaderFloat>(MaterialTransparencyParameterName) is { } transparency)
            {
                transparency.Value = 1.0f - opacity;
                return;
            }

            if (opacity >= ImportedOpaqueThreshold || material.Textures.Count != 0)
                return;

            bool deferredColor = material.RenderPass == (int)EDefaultRenderPass.OpaqueDeferred;
            bool forwardColor = material.RenderPass == (int)EDefaultRenderPass.OpaqueForward &&
                material.Parameter<ShaderVector4>("MatColor") is not null;
            if (!deferredColor && !forwardColor)
                return;

            material.Shaders.Clear();
            material.Shaders.Add(ShaderHelper.LitColorFragForward());
            material.Parameters =
            [
                new ShaderVector4(new Vector4(baseColor, opacity), "MatColor"),
                new ShaderFloat(1.0f, "MatSpecularIntensity"),
                new ShaderFloat(32.0f, "MatShininess"),
            ];
            material.TransparencyMode = ETransparencyMode.AlphaBlend;
            material.RenderPass = ShaderHelper.ResolveTransparentRenderPass(ETransparencyMode.AlphaBlend);
            material.RenderOptions = CreateImportedTransparentRenderOptions();
        }

        /// <summary>Render state of imported forward transparent materials: blended, depth test without depth write.</summary>
        private static RenderingParameters CreateImportedTransparentRenderOptions()
            => new()
            {
                CullMode = ECullMode.Back,
                DepthTest = new DepthTest()
                {
                    UpdateDepth = false,
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                },
                BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.ViewportDimensions,
            };
    }
}
