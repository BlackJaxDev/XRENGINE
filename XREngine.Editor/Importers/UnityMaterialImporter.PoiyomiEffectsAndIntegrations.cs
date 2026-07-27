using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Poiyomi;
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
        bool outlines = HasAnyPositive(document, "_EnableOutlines", "_OutlinesEnabled", "_UseOutline") ||
                        document.Textures.ContainsKey("_OutlineMask");
        bool specialEffects = HasSpecialEffects(document);
        bool vertexEffects = HasVertexEffects(document);
        bool audioLink = document.TryGetPositive("_EnableAudioLink") ||
                         document.ValidKeywords.Contains("POI_AUDIOLINK");
        bool environment = HasAnyPositive(document, "_LTCGIEnabled", "_LightVolumeEnabled", "_BlacklightEnabled");
        bool viewContext = HasAnyPositive(document, "_EnableMirrorOptions", "_MirrorTextureEnabled", "_CameraOptionsEnabled");

        material.SetUberFeatureEnabled("outline", outlines);
        material.SetUberFeatureEnabled("poiyomi-special-effects", specialEffects);
        material.SetUberFeatureEnabled("poiyomi-vertex-effects", vertexEffects);
        material.SetUberFeatureEnabled("poiyomi-audiolink", audioLink && PoiyomiRuntimeAdapters.AudioLink is not null);
        material.SetUberFeatureEnabled("poiyomi-environment-adapters", environment && PoiyomiRuntimeAdapters.Environment is not null);
        material.SetUberFeatureEnabled("poiyomi-view-context", viewContext);
        material.EnsureUberStateInitialized();

        MapOutlineAndDissolve(material, document);
        MapSpecialEffects(material, document);
        MapVertexEffects(material, document);
        BindEffectTextures(material, document, resolver, diagnostics, warnings);

        if (vertexEffects)
        {
            AttachPoiyomiVertexShaders(material);
            PoiyomiVertexUniformBinding vertexBinding = new(material);
            material.SettingVertexUniforms += vertexBinding.Apply;
            material.RenderOptions ??= ModelImporter.CreateForwardPlusUberShaderRenderOptions();
            // GPU-indirect substitutes its own vertex program. Until its
            // material-indexed deformation block is available, keep these
            // meshes on the exact CPU-direct/custom-vertex path.
            material.RenderOptions.ExcludeFromGpuIndirect = true;
        }

        bool adaptersAvailable = PoiyomiRuntimeAdapters.ConfigureMaterial(
            material,
            audioLink,
            environment,
            viewContext);
        if (!adaptersAvailable)
        {
            if (audioLink && PoiyomiRuntimeAdapters.AudioLink is null)
                AddMissingAdapterDiagnostic(diagnostics, warnings, "AudioLink", "_EnableAudioLink");
            if (environment && PoiyomiRuntimeAdapters.Environment is null)
                AddMissingAdapterDiagnostic(diagnostics, warnings, "LTCGI/light-volume", "_LTCGIEnabled");
        }
    }

    private static bool HasSpecialEffects(UnityMaterialDocument document)
        => document.GetPropertyNames().Any(static name =>
            name.Contains("Dissolve", StringComparison.Ordinal) ||
            name.Contains("Pathing", StringComparison.Ordinal) ||
            name.Contains("Proximity", StringComparison.Ordinal) ||
            name.Contains("DepthBulge", StringComparison.Ordinal) ||
            name.Contains("TouchGlow", StringComparison.Ordinal) ||
            name.Contains("InternalParallax", StringComparison.Ordinal) ||
            name.Contains("Video", StringComparison.Ordinal) ||
            name.Contains("Voronoi", StringComparison.Ordinal) ||
            name.Contains("Truchet", StringComparison.Ordinal) ||
            name.Contains("UDIM", StringComparison.Ordinal));

    private static bool HasVertexEffects(UnityMaterialDocument document)
        => HasAnyPositive(
            document,
            "_VertexManipulationsEnabled",
            "_VertexRoundingEnabled",
            "_VertexBarrelMode",
            "_LookAtEnabled",
            "_TextureGlitchEnabled",
            "_UzumoreEnabled",
            "_NaturalEquationEnabled",
            "_DepthBulgeEnabled");

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
        MapFloat(document, material, "_PoiUvDiscard", "_EnableUDIMDiscardOptions", "_UVTileDiscardEnabled");
        MapVector2(document, material, "_PoiUvDiscardGrid", "_UDIMDiscardGrid", "_UVTileDiscardGrid");
        MapVector2(document, material, "_PoiUvDiscardRange", "_UDIMDiscardRange", "_UVTileDiscardRange");
        MapInt(document, material, "_PoiFaceDiscard", "_UDIMDiscardFace", "_FaceDiscard");

        MapFloat(document, material, "_PoiPathing", "_PathingEnabled", "_EnablePathing");
        MapVector4(document, material, "_PoiPathingParams", "_PathingParams");
        MapVector4(document, material, "_PoiPathingColor", "_PathingColor");
        MapFloat(document, material, "_PoiProximity", "_ProximityColorEnabled", "_ProximityEnabled");
        MapVector4(document, material, "_PoiProximityParams", "_ProximityParams");
        MapVector4(document, material, "_PoiProximityColor", "_ProximityColor");
        MapFloat(document, material, "_PoiTouchGlow", "_DepthFXEnabled", "_TouchGlowEnabled");
        MapVector4(document, material, "_PoiTouchGlowParams", "_TouchGlowParams", "_DepthFXParams");
        MapVector4(document, material, "_PoiTouchGlowColor", "_TouchGlowColor", "_DepthFXColor");
        MapFloat(document, material, "_PoiInternalParallax", "_InternalParallaxEnabled", "_ParallaxInternal");
        MapVector4(document, material, "_PoiInternalParallaxParams", "_InternalParallaxParams");
        MapInt(document, material, "_PoiProceduralMode", "_ProceduralMode", "_VideoEffectMode");
        MapVector4(document, material, "_PoiProceduralParams", "_ProceduralParams");
        MapVector4(document, material, "_PoiProceduralColor", "_ProceduralColor");
        MapFloat(document, material, "_PoiVideoBlend", "_VideoBlend", "_VideoTextureStrength");
    }

    private static void MapVertexEffects(XRMaterial material, UnityMaterialDocument document)
    {
        if (!HasVertexEffects(document))
            return;

        SetFloat(material, "_PoiVertexEffectsEnabled", 1.0f);
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
        MapFloat(document, material, "_PoiLookAtWeight", "_LookAtEnabled", "_LookAtWeight");
        MapVector3(document, material, "_PoiLookAtAxis", "_LookAtAxis", "_LookAtForwardVector");
        MapVector4(document, material, "_PoiVertexGlitch", "_TextureGlitchParams", "_VertexGlitchParams");
        MapVector4(document, material, "_PoiUzumore", "_UzumoreParams");
        MapVector4(document, material, "_PoiNaturalEquation", "_NaturalEquationParams");
        MapVector4(document, material, "_PoiDepthBulge", "_DepthBulgeParams");
        MapVector4(document, material, "_PoiVertexColorPosition", "_VertexColorPosition");
        MapVector4(document, material, "_PoiVertexColorNormal", "_VertexColorNormal");
        MapVector4(document, material, "_PoiConservativeBounds", "_VertexBoundsExpansion", "_CullingBoundsHint");
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
            ("_PoiVideoTexture", ["_VideoTexture", "_VideoTex", "_CRTTexture"]),
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
