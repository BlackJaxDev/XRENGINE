using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Materials;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Scene.Importers;

public static partial class UnityMaterialImporter
{
    private static void ApplyPoiyomiEffectsAndIntegrations(
        XRMaterial material,
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        bool outlines = PoiyomiFeatureStateResolver.IsEnabled(
            document,
            HasExternalTexture(document, "_OutlineMask", "_OutlineTexture"),
            ["_EnableOutlines", "_OutlinesEnabled", "_UseOutline"],
            ["POI_OUTLINE"]);
        bool specialEffects = HasSpecialEffects(document);
        bool vertexEffects = HasVertexEffects(document);
        bool audioLink = document.TryGetPositive("_EnableAudioLink") ||
                         document.ValidKeywords.Contains("POI_AUDIOLINK");
        bool environment = HasAnyPositive(document, "_LTCGIEnabled", "_LightVolumeEnabled", "_BlacklightEnabled");
        bool viewContext = HasAnyPositive(document, "_EnableMirrorOptions", "_MirrorTextureEnabled", "_CameraOptionsEnabled");

        material.SetUberFeatureEnabled("outline", outlines);
        material.SetUberFeatureEnabled("extended-effects", specialEffects);
        material.SetUberFeatureEnabled("vertex-effects", vertexEffects);
        material.SetUberFeatureEnabled("audiolink", audioLink && UberMaterialRuntimeAdapters.AudioLink is not null);
        material.SetUberFeatureEnabled("environment-lighting", environment && UberMaterialRuntimeAdapters.Environment is not null);
        material.SetUberFeatureEnabled("view-context", viewContext);
        material.EnsureUberStateInitialized();

        MapOutlineAndDissolve(material, document);
        MapSpecialEffects(material, document);
        MapVertexEffects(material, document);
        BindEffectTextures(material, document, resolver, diagnostics, warnings);

        if (vertexEffects)
        {
            AttachPoiyomiVertexShaders(material);
            UberVertexEffectUniformBinding vertexBinding = new(material);
            material.SettingVertexUniforms += vertexBinding.Apply;
            material.RenderOptions ??= ModelImporter.CreateForwardPlusUberShaderRenderOptions();
            // GPU-indirect substitutes its own vertex program. Until its
            // material-indexed deformation block is available, keep these
            // meshes on the exact CPU-direct/custom-vertex path.
            material.RenderOptions.ExcludeFromGpuIndirect = true;
        }

        bool adaptersAvailable = UberMaterialRuntimeAdapters.ConfigureMaterial(
            material,
            audioLink,
            environment,
            viewContext);
        if (!adaptersAvailable)
        {
            if (audioLink && UberMaterialRuntimeAdapters.AudioLink is null)
                AddMissingAdapterDiagnostic(diagnostics, warnings, "AudioLink", "_EnableAudioLink");
            if (environment && UberMaterialRuntimeAdapters.Environment is null)
                AddMissingAdapterDiagnostic(diagnostics, warnings, "LTCGI/light-volume", "_LTCGIEnabled");
        }
    }

    private static bool HasSpecialEffects(UnityMaterialDocument document)
        => HasAnyPositive(
            document,
            "_EnableUDIMDiscardOptions",
            "_UVTileDiscardEnabled",
            "_PathingEnabled",
            "_EnablePathing",
            "_ProximityColorEnabled",
            "_ProximityEnabled",
            "_DepthFXEnabled",
            "_TouchGlowEnabled",
            "_InternalParallaxEnabled",
            "_ParallaxInternal",
            "_ProceduralMode",
            "_VideoEffectMode");

    private static bool HasVertexEffects(UnityMaterialDocument document)
        => PoiyomiFeatureStateResolver.IsEnabled(
            document,
            authoredEvidence: false,
            ["_VertexManipulationsEnabled"],
            ["AUTO_EXPOSURE"]);

    private static void MapOutlineAndDissolve(XRMaterial material, UnityMaterialDocument document)
    {
        MapInt(document, material, "_OutlineExpansionMode", "_OutlineExpansionMode");
        MapInt(document, material, "_OutlineSpace", "_OutlineSpace");
        MapVector3(document, material, "_OutlinePersonaDirection", "_OutlinePersonaDirection");
        MapVector3(document, material, "_OutlineDropShadowOffset", "_OutlineDropShadowOffset");
        MapFloat(document, material, "_OutlineFixedSize", "_OutlineFixedSize");
        MapFloat(document, material, "_OutlineUseVertexColors", "_OutlineUseVertexColors");
        MapFloat(document, material, "_OutlineZOffset", "_OutlineZOffset", "_OutlineOffset");
        MapFloat(document, material, "_OutlineHueShift", "_OutlineHueShift", "_OutlineHueOffset");
        MapFloat(document, material, "_OutlineHueShiftSpeed", "_OutlineHueOffsetSpeed");
        MapFloat(document, material, "_OutlineShadowStrength", "_OutlineShadowStrength");
        MapInt(document, material, "_OutlineTextureUV", "_OutlineTextureUV");
        MapInt(document, material, "_OutlineMaskUV", "_OutlineMaskUV");
        MapVector2(document, material, "_OutlineTexturePan", "_OutlineTexturePan");
        MapVector2(document, material, "_OutlineMaskPan", "_OutlineMaskPan");

        MapFloat(document, material, "_DissolveContinuous", "_DissolveContinuous", "_DissolveContinuousEnabled");
        MapInt(document, material, "_DissolveCoordinateSpace", "_DissolveCoordinateSpace");
        MapVector2(document, material, "_DissolveTileGrid", "_DissolveTileGrid", "_DissolveUVTileGrid");
        MapFloat(document, material, "_DissolveHueShift", "_DissolveHueShift", "_DissolveEdgeHueShift");
    }

    private static void MapSpecialEffects(XRMaterial material, UnityMaterialDocument document)
    {
        MapFloat(document, material, "_UvTileDiscard", "_EnableUDIMDiscardOptions", "_UVTileDiscardEnabled");
        MapVector2(document, material, "_UvTileDiscardGrid", "_UDIMDiscardGrid", "_UVTileDiscardGrid");
        MapVector2(document, material, "_UvTileDiscardRange", "_UDIMDiscardRange", "_UVTileDiscardRange");
        MapInt(document, material, "_FaceDiscard", "_UDIMDiscardFace", "_FaceDiscard");

        MapFloat(document, material, "_PathingStrength", "_PathingEnabled", "_EnablePathing");
        MapVector4(document, material, "_PathingParams", "_PathingParams");
        MapVector4(document, material, "_PathingColor", "_PathingColor");
        MapFloat(document, material, "_ProximityStrength", "_ProximityColorEnabled", "_ProximityEnabled");
        MapVector4(document, material, "_ProximityParams", "_ProximityParams");
        MapVector4(document, material, "_ProximityColor", "_ProximityColor");
        MapFloat(document, material, "_TouchGlowStrength", "_DepthFXEnabled", "_TouchGlowEnabled");
        MapVector4(document, material, "_TouchGlowParams", "_TouchGlowParams", "_DepthFXParams");
        MapVector4(document, material, "_TouchGlowColor", "_TouchGlowColor", "_DepthFXColor");
        MapFloat(document, material, "_InternalParallaxStrength", "_InternalParallaxEnabled", "_ParallaxInternal");
        MapVector4(document, material, "_InternalParallaxParams", "_InternalParallaxParams");
        MapInt(document, material, "_ProceduralMode", "_ProceduralMode", "_VideoEffectMode");
        MapVector4(document, material, "_ProceduralParams", "_ProceduralParams");
        MapVector4(document, material, "_ProceduralColor", "_ProceduralColor");
        MapFloat(document, material, "_VideoBlend", "_VideoBlend", "_VideoTextureStrength");
    }

    private static void MapVertexEffects(XRMaterial material, UnityMaterialDocument document)
    {
        if (!HasVertexEffects(document))
            return;

        SetFloat(material, "_VertexEffectsEnabled", 1.0f);
        MapVector3(document, material, "_VertexManipulationLocalTranslation", "_VertexManipulationLocalTranslation");
        MapVector3(document, material, "_VertexManipulationLocalRotation", "_VertexManipulationLocalRotation");
        MapVector3(document, material, "_VertexManipulationLocalRotationSpeed", "_VertexManipulationLocalRotationSpeed");
        if (document.TryGetVector("_VertexManipulationLocalScale", out Vector4 scale))
            SetVector3(material, "_VertexManipulationLocalScale", new Vector3(scale.X, scale.Y, scale.Z));
        else
            SetVector3(material, "_VertexManipulationLocalScale", Vector3.One);
        MapVector3(document, material, "_VertexManipulationWorldTranslation", "_VertexManipulationWorldTranslation");
        MapFloat(document, material, "_VertexManipulationHeight", "_VertexManipulationHeight");
        MapFloat(document, material, "_VertexRoundingEnabled", "_VertexRoundingEnabled");
        MapFloat(document, material, "_VertexRoundingDivision", "_VertexRoundingDivision");
        MapFloat(document, material, "_VertexBarrelMode", "_VertexBarrelMode");
        MapFloat(document, material, "_VertexBarrelWidth", "_VertexBarrelWidth");
        MapFloat(document, material, "_VertexBarrelAlpha", "_VertexBarrelAlpha");
        MapFloat(document, material, "_VertexBarrelHeight", "_VertexBarrelHeight");
        MapFloat(document, material, "_VertexLookAtWeight", "_LookAtEnabled", "_LookAtWeight");
        MapVector3(document, material, "_VertexLookAtAxis", "_LookAtAxis", "_LookAtForwardVector");
        MapVector4(document, material, "_VertexGlitch", "_TextureGlitchParams", "_VertexGlitchParams");
        MapVector4(document, material, "_VertexWave", "_UzumoreParams");
        MapVector4(document, material, "_VertexEquation", "_NaturalEquationParams");
        MapVector4(document, material, "_VertexDepthBulge", "_DepthBulgeParams");
        MapVector4(document, material, "_VertexColorPositionOffset", "_VertexColorPosition");
        MapVector4(document, material, "_VertexColorNormalOffset", "_VertexColorNormal");
        MapVector4(document, material, "_VertexConservativeBounds", "_VertexBoundsExpansion", "_CullingBoundsHint");
    }

    private static void BindEffectTextures(
        XRMaterial material,
        UnityMaterialDocument document,
        UnityAssetResolver resolver,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        List<string> warnings)
    {
        (string Destination, string[] Sources)[] bindings =
        [
            ("_OutlineTexture", ["_OutlineTexture", "_OutlineTex"]),
            ("_OutlineMask", ["_OutlineMask", "_OutlineWidthMask"]),
            ("_VideoTexture", ["_VideoTexture", "_VideoTex", "_CRTTexture"]),
        ];

        foreach ((string destination, string[] sources) in bindings)
        {
            foreach (string source in sources)
            {
                XRTexture2D? texture = ResolveUberTexture(document, resolver, warnings, source, destination);
                if (texture is null)
                    continue;

                ReplaceMaterialSampler(material, destination, texture);
                ApplyTextureTransform(material, document, source, destination);
                break;
            }
        }

        if (document.TryGetPositive("_BeatSaberEnabled"))
        {
            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.IntegrationUnavailable,
                MaterialConversionDiagnosticSeverity.Warning,
                "Beat Saber data was authored, but the pinned Poiyomi source exposes no engine-independent runtime provider contract. The state was retained without fabricating input.",
                "_BeatSaberEnabled"));
        }
    }

    private static void AttachPoiyomiVertexShaders(XRMaterial material)
    {
        material.SetShader(
            EShaderType.Vertex,
            ShaderHelper.LoadEngineShader(Path.Combine("Uber", "UberShader.vert"), EShaderType.Vertex),
            coerceShaderType: true);
        material.Shaders.Add(ShaderHelper.LoadEngineShader(Path.Combine("Uber", "UberShader_OVR.vert"), EShaderType.Vertex));
        material.Shaders.Add(ShaderHelper.LoadEngineShader(Path.Combine("Uber", "UberShader_NV.vert"), EShaderType.Vertex));
    }

    private static void AddMissingAdapterDiagnostic(
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ICollection<string> warnings,
        string adapter,
        string sourceProperty)
    {
        AddDiagnostic(
            diagnostics,
            warnings,
            MaterialConversionDiagnosticCodes.IntegrationUnavailable,
            $"{adapter} is enabled, but no provider is registered. The feature remains disabled instead of sampling fabricated data.",
            sourceProperty);
    }

}
