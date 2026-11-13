using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    private const string LostEyeShaderGuid = "34fac1513b5a3564d99b890f16cc60b1";

    private static bool IsLostEyeMaterial(
        SerializedMaterialDocument document,
        SourceAssetResolver resolver,
        out string? shaderPath)
    {
        shaderPath = resolver.Resolve(document.Shader.Guid);
        if (string.Equals(document.Shader.Guid, LostEyeShaderGuid, StringComparison.OrdinalIgnoreCase))
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

                return trimmed.StartsWith("Shader \"!Neon/Eye Shaders/Lost\"", StringComparison.Ordinal);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>
    /// Converts a <c>!Neon/Eye Shaders/Lost</c> material into a canonical <c>NativeUberV1</c>
    /// procedural eye material so both triangle Uber and Gaussian Splat NativeUber paths
    /// evaluate identical multi-layer parallax iris, ring, glare, and dithered vignette math.
    /// </summary>
    private static XRMaterial ConvertLostEyeToUberMaterial(
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

        // Output is unlit procedural emission; zero base color so scene lights add nothing.
        SetVector4(material, "_Color", new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
        SetInt(material, "_Mode", isMasked ? 1 : 0);
        SetFloat(material, "_Cutoff", 0.5f);
        SetInt(material, "_ProceduralMode", 6);

        // _MainColor is marked [hdr] in Lost.shader, so Unity YAML stores linear values directly.
        Vector4 mainColor = document.TryGetVector("_MainColor", out Vector4 authoredColor)
            ? authoredColor
            : Vector4.One;
        SetVector4(material, "_ProceduralColor", new Vector4(mainColor.X, mainColor.Y, mainColor.Z, 1.0f));

        SetVector4(
            material,
            "_LostEyeParams0",
            new Vector4(
                GetFloat(document, "_DistanceBasedParallaxScaling", 1.0f),
                GetFloat(document, "_MainParallax", 1.0f),
                GetFloat(document, "_SnakePupil", 1.0f),
                GetFloat(document, "_PupilSmokeSize", 0.5f)));
        SetVector4(
            material,
            "_LostEyeParams1",
            new Vector4(
                GetFloat(document, "_PupilSize", 0.5f),
                GetFloat(document, "_PupilThickness", 0.5f),
                GetFloat(document, "_MainHueShift"),
                GetFloat(document, "_MainHueSpeed")));
        float ringCount = document.TryGetFloat("_RingCount", out float rawRings)
            ? MathF.Round(rawRings)
            : document.TryGetInt("_RingCount", out int rawRingInt) ? rawRingInt : 10.0f;
        SetVector4(
            material,
            "_LostEyeParams2",
            new Vector4(
                Math.Clamp(ringCount, 0.0f, 20.0f),
                GetFloat(document, "_RingThickness", 0.1f),
                GetFloat(document, "_RingSize", 1.0f),
                0.0f));
        SetVector4(
            material,
            "_LostEyeParams3",
            new Vector4(
                GetFloat(document, "_VignetteThickness", 0.4f),
                GetFloat(document, "_VignetteFalloff", 0.15f),
                vignetteTransparency,
                GetFloat(document, "_VignetteDitherScale")));

        material.EngineSemantic = EngineMaterialSemanticIdentity.NativeUberV1;
        return material;
    }
}
