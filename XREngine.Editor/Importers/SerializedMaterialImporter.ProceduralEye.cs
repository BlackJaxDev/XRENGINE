using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>The asset GUID of the source procedural eye shader.</summary>
    private const string ProceduralEyeShaderGuid = "34fac1513b5a3564d99b890f16cc60b1";

    /// <summary>The shader name that the first code line of the source procedural eye shader declares.</summary>
    private const string ProceduralEyeShaderDeclaration = "Shader \"!Neon/Eye Shaders/Lost\"";

    /// <summary>
    /// True when the material uses the source procedural eye shader. The check accepts the known
    /// asset GUID, or a shader file whose first code line declares the known shader name.
    /// </summary>
    private static bool IsProceduralEyeMaterial(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        out string? shaderPath)
    {
        shaderPath = resolver.Resolve(document.Shader.Guid);
        if (string.Equals(document.Shader.Guid, ProceduralEyeShaderGuid, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(shaderPath) || !File.Exists(shaderPath))
            return false;

        try
        {
            foreach (string line in File.ReadLines(shaderPath))
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                return trimmed.StartsWith(ProceduralEyeShaderDeclaration, StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>
    /// Converts a source procedural eye material to a native Uber material with procedural mode 6.
    /// The forward Uber, native Uber, and splat paths then compute the same layered parallax iris,
    /// rings, glare, and dithered vignette.
    /// </summary>
    /// <remarks>
    /// The eye output is unlit, so the base color is zero and scene lights add nothing. A nonzero
    /// vignette transparency makes the material masked, because the vignette dithers coverage.
    /// </remarks>
    private static XRMaterial ConvertProceduralEyeToUberMaterial(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        List<string> warnings)
    {
        XRTexture2D main = ResolveUberTexture(document, resolver, warnings, "_MainTex", "_MainTex")
            ?? GetDefaultUberSamplerTexture("_MainTex", ColorF4.White);
        XRTexture2D bump = GetDefaultUberSamplerTexture("_BumpMap", new ColorF4(0.5f, 0.5f, 1.0f, 1.0f));
        XRTexture2D outlineMask = GetDefaultUberSamplerTexture("_OutlineMask", ColorF4.White);

        float vignetteTransparency = GetFloat(document, "_VignetteTransparency");
        bool isMasked = vignetteTransparency > 0.0001f;

        RenderingParameters renderOptions = ModelAssetImporter.CreateForwardPlusUberShaderRenderOptions();
        renderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        renderOptions.DepthTest.UpdateDepth = true;
        renderOptions.DepthTest.Function = EComparison.Lequal;
        renderOptions.BlendModeAllDrawBuffers = BlendMode.Disabled();

        int defaultQueue = isMasked ? 2450 : 2000;
        XRMaterial material = new()
        {
            Name = document.Name,
            Textures = [main, bump, outlineMask],
            Parameters = ModelAssetImporter.CreateDefaultForwardPlusUberShaderParameters(0.0f),
            RenderOptions = renderOptions,
            RenderPass = isMasked
                ? (int)EDefaultRenderPass.MaskedForward
                : (int)EDefaultRenderPass.OpaqueForward,
            TransparencyMode = isMasked
                ? ETransparencyMode.Masked
                : ETransparencyMode.Opaque,
            AlphaCutoff = 0.5f,
            TransparentSortPriority = document.CustomRenderQueue >= 0 ? document.CustomRenderQueue - defaultQueue : 0,
        };

        material.SetShader(EShaderType.Fragment, ShaderHelper.UberFragForward(), coerceShaderType: true);
        material.SetUberFeatureEnabled("extended-effects", true);
        material.SetUberFeatureEnabled("render-time", true);
        material.EnsureUberStateInitialized();

        ApplyTextureTransform(material, document, "_MainTex", "_MainTex");
        if (material.Parameter<ShaderVector4>("_MainTex_ST") is null)
            SetVector4(material, "_MainTex_ST", new Vector4(1.0f, 1.0f, 0.0f, 0.0f));

        SetVector4(material, "_Color", new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        SetInt(material, "_Mode", isMasked ? 1 : 0);
        SetFloat(material, "_Cutoff", 0.5f);
        SetInt(material, "_ProceduralMode", 6);

        // The source declares the eye color as HDR, so the material file stores linear values.
        Vector4 mainColor = document.TryGetVector("_MainColor", out Vector4 authoredColor)
            ? authoredColor
            : Vector4.One;
        SetVector4(material, "_ProceduralColor", new Vector4(mainColor.X, mainColor.Y, mainColor.Z, 1.0f));

        SetVector4(material, "_ProceduralEyeParams0", new Vector4(
            GetFloat(document, "_DistanceBasedParallaxScaling", 1.0f),
            GetFloat(document, "_MainParallax", 1.0f),
            GetFloat(document, "_SnakePupil", 1.0f),
            GetFloat(document, "_PupilSmokeSize", 0.5f)));
        SetVector4(material, "_ProceduralEyeParams1", new Vector4(
            GetFloat(document, "_PupilSize", 0.5f),
            GetFloat(document, "_PupilThickness", 0.5f),
            GetFloat(document, "_MainHueShift"),
            GetFloat(document, "_MainHueSpeed")));
        float ringCount = document.TryGetFloat("_RingCount", out float rawRings)
            ? MathF.Round(rawRings)
            : document.TryGetInt("_RingCount", out int rawRingInt) ? rawRingInt : 10.0f;
        SetVector4(material, "_ProceduralEyeParams2", new Vector4(
            Math.Clamp(ringCount, 0.0f, 20.0f),
            GetFloat(document, "_RingThickness", 0.1f),
            GetFloat(document, "_RingSize", 1.0f),
            0.0f));
        SetVector4(material, "_ProceduralEyeParams3", new Vector4(
            GetFloat(document, "_VignetteThickness", 0.4f),
            GetFloat(document, "_VignetteFalloff", 0.15f),
            vignetteTransparency,
            GetFloat(document, "_VignetteDitherScale")));

        material.EngineSemantic = EngineMaterialSemanticIdentity.NativeUberV1;
        return material;
    }
}
