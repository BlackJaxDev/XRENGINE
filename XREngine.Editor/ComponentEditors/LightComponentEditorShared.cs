using ImGuiNET;
using Silk.NET.OpenGL;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Lightmapping;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.ComponentEditors;

internal static class LightComponentEditorShared
{
    private const float PreviewMaxEdge = 196.0f;
    private const float PreviewFallbackEdge = 96.0f;
    private const int ArrayPreviewsPerRow = 3;
    private const string ShadowMapEncodingItems = "Depth\0VSM (2 Moment)\0EVSM 2-Channel\0EVSM 4-Channel\0";
    private const string ShadowFilterModeItems =
        "Hard / PCF\0Fixed Soft (Poisson)\0PCSS / Contact Hardening\0Fixed Soft (Vogel)\0";
    private static readonly EShadowMapStorageFormat[] LocalShadowMapStorageFormats =
    [
        EShadowMapStorageFormat.R16Float,
        EShadowMapStorageFormat.R16UNorm,
        EShadowMapStorageFormat.R32Float,
        EShadowMapStorageFormat.R8UNorm,
    ];
    private static readonly EShadowMapStorageFormat[] DirectionalShadowMapStorageFormats =
    [
        EShadowMapStorageFormat.Depth24,
        EShadowMapStorageFormat.Depth16,
        EShadowMapStorageFormat.Depth32Float,
    ];
    private static readonly Vector4 AtlasActiveTextColor = new(0.42f, 0.86f, 0.52f, 1.0f);
    private static readonly Vector4 AtlasDiagnosticTextColor = new(0.92f, 0.72f, 0.36f, 1.0f);

    private static readonly ConditionalWeakTable<XRTexture2DArray, Dictionary<int, XRTexture2DArrayView>> Texture2DArrayPreviewViews = new();
    private static readonly ConditionalWeakTable<XRTextureCube, CubemapPreviewCache> CubemapPreviewCaches = new();
    private static readonly ConditionalWeakTable<LightComponent, ShadowResolutionEditState> ShadowResolutionEditStates = new();
    private static readonly ConditionalWeakTable<IGLTexture, PreviewSwizzleState> TexturePreviewSwizzles = new();

    private static readonly (ECubemapFace Face, string Label)[] CubemapFaces =
    [
        (ECubemapFace.PosX, "+X"),
        (ECubemapFace.NegX, "-X"),
        (ECubemapFace.PosY, "+Y"),
        (ECubemapFace.NegY, "-Y"),
        (ECubemapFace.PosZ, "+Z"),
        (ECubemapFace.NegZ, "-Z"),
    ];

    public static void DrawCommonLightSection(LightComponent light)
    {
        if (!ImGui.CollapsingHeader("Light", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Vector3 color = light.Color;
        if (ImGui.ColorEdit3("Color", ref color))
            light.Color = (ColorF3)color;

        float diffuse = light.DiffuseIntensity;
        if (ImGui.DragFloat("Diffuse Intensity", ref diffuse, 0.01f, 0.0f, 100000.0f, "%.3f"))
            light.DiffuseIntensity = MathF.Max(0.0f, diffuse);

        DrawShadowModeCombo(light);

        ImGui.Separator();
        bool preview = light.PreviewBoundingVolume;
        if (ImGui.Checkbox("Preview Bounding Volume", ref preview))
            light.PreviewBoundingVolume = preview;
    }

    private static void DrawShadowModeCombo(LightComponent light)
    {
        // NOTE: This maps directly to ELightType today.
        // - Realtime => Dynamic
        // - Hybrid   => DynamicCached
        // - Baked    => Static
        var type = light.Type;

        int mode = type switch
        {
            ELightType.Dynamic => 0,
            ELightType.DynamicCached => 1,
            ELightType.Static => 2,
            _ => 0
        };

        if (ImGui.Combo("Shadow Mode", ref mode, "Realtime\0Hybrid\0Baked\0"))
        {
            light.Type = mode switch
            {
                0 => ELightType.Dynamic,
                1 => ELightType.DynamicCached,
                2 => ELightType.Static,
                _ => ELightType.Dynamic
            };
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Currently maps to ELightType (Dynamic / DynamicCached / Static). Hybrid auto-baking is disabled by default, and the lightmap workflow is still experimental.");
    }

    public static void DrawShadowSection(LightComponent light, bool showCascadedOptions)
    {
        if (!ImGui.CollapsingHeader("Shadows", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawShadowAtlasModeBanner(light);
        DrawShadowMapControls(light, showCascadedOptions);
        DrawShadowBiasControls(light);
        DrawShadowFilteringControls(light);
        DrawContactShadowControls(light);
        DrawShadowDebugControls(light);
        DrawShadowAtlasDiagnostics(light);
        DrawLightmapBakeControls(light);
    }

    private static void DrawShadowAtlasModeBanner(LightComponent light)
    {
        ImGui.SeparatorText("Runtime Shadow Path");

        if (light is SpotLightComponent)
        {
            bool useSpotAtlas = Engine.Rendering.Settings.UseSpotShadowAtlas;
            if (ImGui.Checkbox("Use Spot Shadow Atlas", ref useSpotAtlas))
                Engine.Rendering.Settings.UseSpotShadowAtlas = useSpotAtlas;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Global renderer setting. When enabled, dynamic spot lights render and sample through the dynamic shadow atlas.");

            if (useSpotAtlas)
            {
                ImGui.TextColored(AtlasActiveTextColor, "Active: spot shadow atlas");
                ImGui.TextDisabled("Resolution controls request atlas tile size; actual size can be demoted by budget.");
                ImGui.TextDisabled("Storage Format is ignored while atlas sampling is active; spot atlas pages use R16F depth.");
            }
            else
            {
                ImGui.TextColored(AtlasDiagnosticTextColor, "Active: legacy spot shadow map");
                ImGui.TextDisabled("Spot atlas requests are disabled. Per-light shadow map format and resolution are used directly.");
            }

            return;
        }

        if (light is PointLightComponent)
        {
            ImGui.TextColored(AtlasDiagnosticTextColor, "Active: legacy point cubemap shadows");
            ImGui.TextDisabled("Point atlas rendering and sampling are not implemented yet.");
            return;
        }

        if (light is DirectionalLightComponent)
        {
            bool useDirectionalAtlas = Engine.Rendering.Settings.UseDirectionalShadowAtlas;
            if (ImGui.Checkbox("Use Directional Shadow Atlas", ref useDirectionalAtlas))
                Engine.Rendering.Settings.UseDirectionalShadowAtlas = useDirectionalAtlas;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Global renderer setting. When enabled, directional shadow maps and cascades render and sample through the dynamic shadow atlas.");

            if (useDirectionalAtlas)
            {
                ImGui.TextColored(AtlasActiveTextColor, "Active: directional shadow atlas");
                ImGui.TextDisabled("Resolution controls request atlas tile size; actual size can be demoted by budget.");
                ImGui.TextDisabled("Directional direct lighting ignores Storage Format while atlas sampling is active; atlas pages use R16F depth.");
            }
            else
            {
                ImGui.TextColored(AtlasDiagnosticTextColor, "Active: legacy directional/cascade shadows");
                ImGui.TextDisabled("Directional atlas requests are disabled. Shadow map and cascade texture array formats are used directly.");
            }
        }
    }

    private static bool IsSpotShadowAtlasActive(LightComponent light)
        => light is SpotLightComponent && Engine.Rendering.Settings.UseSpotShadowAtlas;

    private static bool IsDirectionalShadowAtlasActive(LightComponent light)
        => light is DirectionalLightComponent && Engine.Rendering.Settings.UseDirectionalShadowAtlas;

    private static void DrawShadowMapControls(LightComponent light, bool showCascadedOptions)
    {
        ImGui.SeparatorText("Shadow Map");

        bool casts = light.CastsShadows;
        if (ImGui.Checkbox("Casts Shadows", ref casts))
            light.CastsShadows = casts;

        if (light is PointLightComponent)
        {
            ShadowResolutionEditState editState = GetShadowResolutionEditState(light);
            if (DrawCommittedIntInput("Cubemap Face Resolution", ref editState.CubemapResolution))
                CommitShadowMapResolution(light, editState, editState.CubemapResolution, editState.CubemapResolution);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Point-light shadows render one square cubemap face per direction.");
        }
        else
        {
            bool spotAtlasActive = IsSpotShadowAtlasActive(light);
            bool directionalAtlasActive = IsDirectionalShadowAtlasActive(light);
            ShadowResolutionEditState editState = GetShadowResolutionEditState(light);
            string widthLabel = spotAtlasActive || directionalAtlasActive ? "Requested Tile Width" : "Map Width";
            string heightLabel = spotAtlasActive || directionalAtlasActive ? "Requested Tile Height" : "Map Height";
            if (DrawCommittedIntInput(widthLabel, ref editState.Width))
                CommitShadowMapResolution(light, editState, editState.Width, editState.Height);
            if (ImGui.IsItemHovered() && (spotAtlasActive || directionalAtlasActive))
                ImGui.SetTooltip("Desired atlas tile width. The allocator can demote this to fit page, memory, and frame budgets.");
            if (DrawCommittedIntInput(heightLabel, ref editState.Height))
                CommitShadowMapResolution(light, editState, editState.Width, editState.Height);
            if (ImGui.IsItemHovered() && (spotAtlasActive || directionalAtlasActive))
                ImGui.SetTooltip("Desired atlas tile height. Atlas allocations are square and use the larger requested dimension.");
        }

        if (IsSpotShadowAtlasActive(light))
        {
            ImGui.BeginDisabled();
            DrawShadowMapStorageFormatCombo(light);
            ImGui.EndDisabled();
            ImGui.TextDisabled("Ignored by the active spot atlas. This value is kept for the legacy fallback path.");
        }
        else
        {
            DrawShadowMapStorageFormatCombo(light);
        }

        if (showCascadedOptions)
        {
            bool cascaded = light.EnableCascadedShadows;
            if (ImGui.Checkbox("Cascaded Shadows", ref cascaded))
                light.EnableCascadedShadows = cascaded;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Use per-camera cascade slices for directional shadows.");
        }
    }

    private static void DrawShadowMapStorageFormatCombo(LightComponent light)
    {
        string preview = FormatShadowMapStorageLabel(light.ShadowMapStorageFormat);
        if (ImGui.BeginCombo("Storage Format", preview))
        {
            EShadowMapStorageFormat[] formats = light is DirectionalLightComponent
                ? DirectionalShadowMapStorageFormats
                : LocalShadowMapStorageFormats;

            foreach (EShadowMapStorageFormat format in formats)
            {
                if (!light.SupportsShadowMapStorageFormat(format))
                    continue;

                bool selected = light.ShadowMapStorageFormat == format;
                if (ImGui.Selectable(FormatShadowMapStorageLabel(format), selected))
                    light.ShadowMapStorageFormat = format;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(DescribeShadowMapStorageFormat(format));

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only framebuffer-renderable formats compatible with the current shadow shaders are listed. Compressed textures and true integer samplers are hidden because this path renders and samples normalized/floating-point shadow depths.");
    }

    private static string FormatShadowMapStorageLabel(EShadowMapStorageFormat format)
        => format switch
        {
            EShadowMapStorageFormat.R8UNorm => "R8 UNorm",
            EShadowMapStorageFormat.R16UNorm => "R16 UNorm",
            EShadowMapStorageFormat.R16Float => "R16 Float",
            EShadowMapStorageFormat.R32Float => "R32 Float",
            EShadowMapStorageFormat.Depth16 => "Depth 16",
            EShadowMapStorageFormat.Depth24 => "Depth 24",
            EShadowMapStorageFormat.Depth32Float => "Depth 32 Float",
            _ => format.ToString(),
        };

    private static string DescribeShadowMapStorageFormat(EShadowMapStorageFormat format)
        => format switch
        {
            EShadowMapStorageFormat.R8UNorm => "8-bit fixed-point red channel. Very compact, useful for debugging or intentionally low precision local shadows.",
            EShadowMapStorageFormat.R16UNorm => "16-bit fixed-point red channel. Integer-backed storage sampled as normalized floats for point and spot shadows.",
            EShadowMapStorageFormat.R16Float => "16-bit floating-point red channel. Current default for point and spot shadows.",
            EShadowMapStorageFormat.R32Float => "32-bit floating-point red channel. Highest precision local-light shadow storage.",
            EShadowMapStorageFormat.Depth16 => "16-bit fixed-point depth texture. Compact directional shadow storage with lower precision.",
            EShadowMapStorageFormat.Depth24 => "24-bit fixed-point depth texture. Current default for directional and cascaded shadows.",
            EShadowMapStorageFormat.Depth32Float => "32-bit floating-point depth texture. Highest precision directional shadow storage.",
            _ => "Unsupported shadow-map storage format.",
        };

    private static void DrawShadowBiasControls(LightComponent light)
    {
        ImGui.SeparatorText("Bias");

        float minBias = light.ShadowMinBias;
        if (ImGui.DragFloat("Min Bias", ref minBias, 0.0001f, 0.0f, 1000.0f, "%.6f"))
            light.ShadowMinBias = MathF.Max(0.0f, minBias);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Minimum compare bias. Raises the floor used on front-facing receivers.");

        float maxBias = light.ShadowMaxBias;
        if (ImGui.DragFloat("Max / Slope Bias", ref maxBias, 0.0001f, 0.0f, 1000.0f, "%.6f"))
            light.ShadowMaxBias = MathF.Max(0.0f, maxBias);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Upper bias used at grazing angles and for receiver normal offset.");

        float expBase = light.ShadowExponentBase;
        if (ImGui.DragFloat("Bias Base", ref expBase, 0.001f, 0.0f, 100.0f, "%.4f"))
            light.ShadowExponentBase = MathF.Max(0.0f, expBase);

        float exp = light.ShadowExponent;
        if (ImGui.DragFloat("Bias Exponent", ref exp, 0.001f, 0.0f, 100.0f, "%.4f"))
            light.ShadowExponent = MathF.Max(0.0f, exp);
    }

    private static void DrawShadowFilteringControls(LightComponent light)
    {
        ImGui.SeparatorText("Shadow Filtering");

        int encoding = (int)light.ShadowMapEncoding;
        if (ImGui.Combo("Shadow Map Encoding", ref encoding, ShadowMapEncodingItems))
            light.ShadowMapEncoding = (EShadowMapEncoding)Math.Clamp(encoding, 0, 3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Depth preserves the existing depth-compare filters. VSM and EVSM store depth moments and use the Moment Filter controls.");

        bool momentEncoding = light.ShadowMapEncoding != EShadowMapEncoding.Depth;

        int softMode = (int)light.SoftShadowMode;
        if (momentEncoding)
            ImGui.BeginDisabled();
        if (ImGui.Combo("Filter Mode", ref softMode, ShadowFilterModeItems))
            light.SoftShadowMode = (ESoftShadowMode)Math.Clamp(softMode, 0, 3);
        if (momentEncoding)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(momentEncoding
                ? "Moment map encodings replace manual depth filtering for map visibility. Contact shadows still multiply on top."
                : "Hard / PCF = crisp compare or small PCF fallback.\nFixed Soft (Poisson) = constant-radius Poisson filter.\nPCSS / Contact Hardening = blocker search plus variable penumbra.\nFixed Soft (Vogel) = constant-radius golden-angle disk taps.");

        if (momentEncoding)
        {
            DrawMomentFilteringControls(light);
            return;
        }

        ImGui.Indent();
        switch (light.SoftShadowMode)
        {
            case ESoftShadowMode.VogelDisk:
                DrawVogelTapControl(light);
                DrawFilterRadiusControl(light, "Vogel Radius");
                break;
            case ESoftShadowMode.ContactHardeningPcss:
                DrawBlockerSampleCountControl(light);
                DrawFilterSampleCountControl(light, "Filter Samples", 32);
                DrawBlockerSearchRadiusControl(light);
                DrawPenumbraClampControls(light);
                DrawLightSourceRadiusControl(light);
                break;
            case ESoftShadowMode.FixedPoisson:
                DrawFilterSampleCountControl(light, "Poisson Samples", light is PointLightComponent ? 32 : 16);
                DrawFilterRadiusControl(light, "Poisson Radius");
                break;
            default:
                DrawFilterSampleCountControl(light, "PCF Samples", light is PointLightComponent ? 32 : 16);
                DrawFilterRadiusControl(light, "PCF Radius");
                break;
        }
        ImGui.Unindent();
    }

    private static void DrawMomentFilteringControls(LightComponent light)
    {
        ShadowMapFormatSelection selection = light.ResolveShadowMapFormat();
        ImGui.TextDisabled($"Resolved Format: {FormatShadowMapStorageLabel(selection.Format.StorageFormat)}");
        if (selection.WasDemoted)
            ImGui.TextDisabled($"Demoted to {selection.Encoding} because the requested moment format is unsupported.");

        ImGui.Indent();
        float minVariance = light.ShadowMomentMinVariance;
        if (ImGui.DragFloat("Min Variance", ref minVariance, 0.000001f, 0.0f, 1.0f, "%.8f"))
            light.ShadowMomentMinVariance = minVariance;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Variance floor used by Chebyshev visibility to reduce acne.");

        float bleed = light.ShadowMomentLightBleedReduction;
        if (ImGui.DragFloat("Bleed Reduction", ref bleed, 0.005f, 0.0f, 0.999f, "%.3f"))
            light.ShadowMomentLightBleedReduction = bleed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Higher values reduce VSM/EVSM light bleeding but can darken soft shadow edges.");

        if (light.ShadowMapEncoding is EShadowMapEncoding.ExponentialVariance2 or EShadowMapEncoding.ExponentialVariance4)
        {
            float positiveExponent = light.ShadowMomentPositiveExponent;
            if (ImGui.DragFloat("Positive Exponent", ref positiveExponent, 0.05f, 0.0f, ShadowMapResourceFactory.FloatEvsmExponentClamp, "%.2f"))
                light.ShadowMomentPositiveExponent = positiveExponent;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Clamped to {selection.PositiveExponent:0.##} for {FormatShadowMapStorageLabel(selection.Format.StorageFormat)}.");
        }

        if (light.ShadowMapEncoding == EShadowMapEncoding.ExponentialVariance4)
        {
            float negativeExponent = light.ShadowMomentNegativeExponent;
            if (ImGui.DragFloat("Negative Exponent", ref negativeExponent, 0.05f, 0.0f, ShadowMapResourceFactory.FloatEvsmExponentClamp, "%.2f"))
                light.ShadowMomentNegativeExponent = negativeExponent;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Clamped to {selection.NegativeExponent:0.##} for {FormatShadowMapStorageLabel(selection.Format.StorageFormat)}.");
        }

        int blurRadius = light.ShadowMomentBlurRadiusTexels;
        if (ImGui.InputInt("Blur Radius Texels", ref blurRadius))
            light.ShadowMomentBlurRadiusTexels = blurRadius;

        int blurPasses = light.ShadowMomentBlurPasses;
        if (ImGui.InputInt("Blur Passes", ref blurPasses))
            light.ShadowMomentBlurPasses = blurPasses;

        bool useMipmaps = light.ShadowMomentUseMipmaps;
        if (ImGui.Checkbox("Use Mipmaps", ref useMipmaps))
            light.ShadowMomentUseMipmaps = useMipmaps;

        float mipBias = light.ShadowMomentMipBias;
        if (!useMipmaps)
            ImGui.BeginDisabled();
        if (ImGui.DragFloat("Mip Bias", ref mipBias, 0.05f, -16.0f, 16.0f, "%.2f") && useMipmaps)
            light.ShadowMomentMipBias = mipBias;
        if (!useMipmaps)
            ImGui.EndDisabled();

        ImGui.Unindent();
    }

    private static void DrawFilterSampleCountControl(LightComponent light, string label, int maxSamples)
    {
        int samples = light.FilterSamples;
        if (ImGui.InputInt(label, ref samples))
            light.FilterSamples = Math.Clamp(samples, 1, maxSamples);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Shader clamps this path to 1-{maxSamples} samples.");
    }

    private static void DrawBlockerSampleCountControl(LightComponent light)
    {
        int samples = light.BlockerSamples;
        if (ImGui.InputInt("Blocker Samples", ref samples))
            light.BlockerSamples = Math.Clamp(samples, 1, 32);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Samples used only by PCSS/contact-hardening blocker search.");
    }

    private static void DrawVogelTapControl(LightComponent light)
    {
        int vogelTapCount = light.VogelTapCount;
        if (ImGui.InputInt("Vogel Taps", ref vogelTapCount))
            light.VogelTapCount = Math.Clamp(vogelTapCount, 1, LightComponent.MaxVogelTapCount);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Number of golden-angle disk taps used by the Vogel filter.");
    }

    private static void DrawFilterRadiusControl(LightComponent light, string label)
    {
        float filter = light.FilterRadius;
        if (ImGui.DragFloat(label, ref filter, 0.0001f, 0.0f, 1.0f, "%.6f"))
            light.FilterRadius = MathF.Max(0.0f, filter);
    }

    private static void DrawBlockerSearchRadiusControl(LightComponent light)
    {
        float search = light.BlockerSearchRadius;
        if (ImGui.DragFloat("Blocker Search Radius", ref search, 0.0001f, 0.0f, 1.0f, "%.6f"))
            light.BlockerSearchRadius = MathF.Max(0.0f, search);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shadow-map search radius used to find average blockers before the PCSS filter pass.");
    }

    private static void DrawPenumbraClampControls(LightComponent light)
    {
        float minPenumbra = light.MinPenumbra;
        if (ImGui.DragFloat("Min Penumbra", ref minPenumbra, 0.0001f, 0.0f, 1.0f, "%.6f"))
            light.MinPenumbra = MathF.Max(0.0f, minPenumbra);

        float maxPenumbra = light.MaxPenumbra;
        if (ImGui.DragFloat("Max Penumbra", ref maxPenumbra, 0.0001f, 0.0f, 1.0f, "%.6f"))
            light.MaxPenumbra = MathF.Max(light.MinPenumbra, maxPenumbra);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Upper clamp for variable PCSS/contact-hardening blur size.");
    }

    private static void DrawLightSourceRadiusControl(LightComponent light)
    {
        bool useLightRadius = light.UseLightRadiusForContactHardening;
        if (light.SupportsLightRadiusContactHardening)
        {
            if (ImGui.Checkbox("Auto From Light Radius", ref useLightRadius))
                light.UseLightRadiusForContactHardening = useLightRadius;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Use a capped source radius derived from the point-light radius or spotlight outer cone radius. The automatic value is clamped to {LightComponent.MaxAutomaticContactHardeningLightRadius:0.##} to preserve hard contact detail.");
        }

        float lightRadius = light.EffectiveLightSourceRadius;
        if (useLightRadius)
            ImGui.BeginDisabled();

        if (ImGui.DragFloat("Light Source Radius", ref lightRadius, 0.001f, 0.0f, 1000000.0f, "%.4f") && !useLightRadius)
            light.LightSourceRadius = MathF.Max(0.0f, lightRadius);

        if (useLightRadius)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Source size used by PCSS/contact-hardening to widen penumbrae with blocker distance.");
    }

    private static void DrawContactShadowControls(LightComponent light)
    {
        ImGui.SeparatorText("Short-Range Contact Shadows");

        bool contact = light.EnableContactShadows;
        if (ImGui.Checkbox("Enable", ref contact))
            light.EnableContactShadows = contact;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Extra short-range shadow test multiplied with the normal shadow-map result. Separate from PCSS/contact-hardening filtering.");

        if (contact)
        {
            ImGui.Indent();
            float dist = light.ContactShadowDistance;
            if (ImGui.DragFloat("Contact Distance", ref dist, 0.001f, 0.0f, 10.0f, "%.4f"))
                light.ContactShadowDistance = MathF.Max(0.0f, dist);

            int cs = light.ContactShadowSamples;
            if (ImGui.InputInt("Contact Samples", ref cs))
                light.ContactShadowSamples = Math.Clamp(cs, 1, 32);

            float thickness = light.ContactShadowThickness;
            if (ImGui.DragFloat("Contact Thickness", ref thickness, 0.001f, 0.0f, 10.0f, "%.4f"))
                light.ContactShadowThickness = MathF.Max(0.0f, thickness);

            float fadeStart = light.ContactShadowFadeStart;
            if (ImGui.DragFloat("Fade Start", ref fadeStart, 0.1f, 0.0f, 10000.0f, "%.2f"))
                light.ContactShadowFadeStart = MathF.Max(0.0f, fadeStart);

            float fadeEnd = light.ContactShadowFadeEnd;
            if (ImGui.DragFloat("Fade End", ref fadeEnd, 0.1f, 0.0f, 10000.0f, "%.2f"))
                light.ContactShadowFadeEnd = MathF.Max(0.0f, fadeEnd);

            float normalOffset = light.ContactShadowNormalOffset;
            if (ImGui.DragFloat("Normal Offset", ref normalOffset, 0.001f, 0.0f, 10.0f, "%.4f"))
                light.ContactShadowNormalOffset = MathF.Max(0.0f, normalOffset);

            float jitter = light.ContactShadowJitterStrength;
            if (ImGui.DragFloat("Jitter Strength", ref jitter, 0.01f, 0.0f, 1.0f, "%.2f"))
                light.ContactShadowJitterStrength = Math.Clamp(jitter, 0.0f, 1.0f);
            ImGui.Unindent();
        }
    }

    private static void DrawShadowDebugControls(LightComponent light)
    {
        if (light is DirectionalLightComponent)
            return;

        ImGui.SeparatorText("Debug");

        int debugMode = light.ShadowDebugMode;
        if (ImGui.Combo("Shadow Debug Mode", ref debugMode, "Off\0Shadow Only\0Margin Heatmap\0"))
            light.ShadowDebugMode = debugMode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: normal lighting.\nShadow Only: white=lit, black=shadow.\nMargin Heatmap: green=lit margin, red=false-shadow margin.");
    }

    private static void DrawShadowAtlasDiagnostics(LightComponent light)
    {
        ShadowRequestDiagnostic diagnostic = light.ShadowAtlasDiagnostic;
        if (diagnostic.RequestCount <= 0)
            return;

        ImGui.SeparatorText("Shadow Atlas");
        ImGui.TextDisabled($"Requests: {diagnostic.RequestCount}");
        ImGui.TextDisabled($"Resident: {diagnostic.ResidentCount}");
        ImGui.TextDisabled($"Max requested: {diagnostic.MaxRequestedResolution} px");
        ImGui.TextDisabled($"Max allocated: {diagnostic.MaxAllocatedResolution} px");
        ImGui.TextDisabled($"Priority: {diagnostic.HighestPriority:F2}");
        if (diagnostic.ShadowRecordIndex >= 0)
            ImGui.TextDisabled($"Record: {diagnostic.ShadowRecordIndex}");
        if (diagnostic.AtlasPageIndex >= 0)
        {
            ImGui.TextDisabled($"Page: {diagnostic.AtlasPageIndex}");
            ImGui.TextDisabled($"Rect: {diagnostic.AtlasPixelRect.X},{diagnostic.AtlasPixelRect.Y} {diagnostic.AtlasPixelRect.Width}x{diagnostic.AtlasPixelRect.Height}");
            ImGui.TextDisabled($"Inner: {diagnostic.AtlasInnerPixelRect.X},{diagnostic.AtlasInnerPixelRect.Y} {diagnostic.AtlasInnerPixelRect.Width}x{diagnostic.AtlasInnerPixelRect.Height}");
        }
        if (diagnostic.LastRenderedFrame != 0u)
            ImGui.TextDisabled($"Last rendered: {diagnostic.LastRenderedFrame}");
        ImGui.TextDisabled($"Fallback: {diagnostic.ActiveFallback}");
        if (diagnostic.LastSkipReason != SkipReason.None)
            ImGui.TextDisabled($"Last skip: {diagnostic.LastSkipReason}");
    }

    private static void DrawLightmapBakeControls(LightComponent light)
    {
        if (light.Type != ELightType.Static)
            return;

        ImGui.SeparatorText("Baking");

        if (ImGui.Button("Bake Lightmaps"))
            light.WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights?.LightmapBaking?.RequestBake(light);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Queues a lightmap bake for Static meshes. Hybrid auto-baking is disabled by default, and lightmap rendering remains experimental.");
    }

    public static void DrawShadowMapPreview(LightComponent light)
    {
        if (!ImGui.CollapsingHeader("Shadow Map Preview", ImGuiTreeNodeFlags.None))
            return;

        if (!light.CastsShadows)
        {
            ImGui.TextDisabled("Shadows disabled for this light.");
            return;
        }

        if (TryDrawSpotAtlasPreview(light))
            return;

        if (TryDrawDirectionalAtlasPreview(light))
            return;

        if (light is global::XREngine.Components.Lights.DirectionalLightComponent dirLight &&
            dirLight.EnableCascadedShadows &&
            dirLight.CascadedShadowMapTexture is XRTexture2DArray cascadeTexture &&
            dirLight.ActiveCascadeCount > 0)
        {
            DrawDirectionalCascadePreview(dirLight, cascadeTexture, dirLight.ActiveCascadeCount);
            return;
        }

        var shadowFbo = light.ShadowMap;
        var mat = shadowFbo?.Material;
        if (mat is null || mat.Textures is null || mat.Textures.Count == 0)
        {
            ImGui.TextDisabled("Shadow map not allocated yet.");
            return;
        }

        XRTexture? texture = SelectBestShadowPreviewTexture(mat.Textures);
        if (texture is null)
        {
            ImGui.TextDisabled("Shadow map texture missing.");
            return;
        }

        switch (texture)
        {
            case XRTexture2D tex2D:
                Draw2DTexturePreview("ShadowMap", tex2D);
                break;
            case XRTexture2DArray tex2DArray:
                Draw2DArrayTexturePreview("ShadowMap", tex2DArray, (int)Math.Max(1u, tex2DArray.Depth));
                break;
            case XRTextureCube cube:
                DrawCubemapPreview("ShadowMap", cube);
                break;
            default:
                ImGui.TextDisabled($"{texture.GetType().Name} preview not supported.");
                break;
        }
    }

    private static bool TryDrawSpotAtlasPreview(LightComponent light)
    {
        if (!IsSpotShadowAtlasActive(light) || light is not SpotLightComponent spotLight)
            return false;

        ImGui.TextDisabled("Previewing active spot atlas tile.");

        XRWorldInstance? world = spotLight.WorldAs<XRWorldInstance>();
        if (world?.Lights is null)
        {
            ImGui.TextDisabled("Spot atlas preview unavailable until the light belongs to a world.");
            return true;
        }

        ShadowRequestKey key = spotLight.CreateShadowRequestKey(EShadowProjectionType.SpotPrimary, 0, EShadowMapEncoding.Depth);
        if (!world.Lights.ShadowAtlas.PublishedFrameData.TryGetAllocationIndex(key, out int recordIndex, out ShadowAtlasAllocation allocation))
        {
            ImGui.TextDisabled("No atlas allocation published for this spot light yet.");
            return true;
        }

        if (!allocation.IsResident)
        {
            ImGui.TextDisabled($"Atlas request is not resident. Fallback: {allocation.ActiveFallback}, skip: {allocation.SkipReason}");
            return true;
        }

        if (allocation.LastRenderedFrame == 0u)
        {
            ImGui.TextDisabled("Atlas tile is allocated but has not rendered yet.");
            return true;
        }

        if (!world.Lights.ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, allocation.PageIndex, out XRTexture2D pageTexture))
        {
            ImGui.TextDisabled($"Atlas page {allocation.PageIndex} texture is unavailable.");
            return true;
        }

        Vector4 uv = allocation.UvScaleBias;
        Vector2 uv0 = new(uv.Z, uv.W + uv.Y);
        Vector2 uv1 = new(uv.Z + uv.X, uv.W);
        Vector2 tilePixelSize = new(
            MathF.Max(1.0f, allocation.InnerPixelRect.Width),
            MathF.Max(1.0f, allocation.InnerPixelRect.Height));
        string details =
            $"Record {recordIndex} | Page {allocation.PageIndex}\n" +
            $"Tile {allocation.InnerPixelRect.X},{allocation.InnerPixelRect.Y} {allocation.InnerPixelRect.Width}x{allocation.InnerPixelRect.Height}\n" +
            $"Page {pageTexture.Width}x{pageTexture.Height} | {pageTexture.SizedInternalFormat}\n" +
            $"Last rendered frame {allocation.LastRenderedFrame}";

        Draw2DTexturePreview("Spot Shadow Atlas Tile", pageTexture, uv0, uv1, tilePixelSize, details);
        return true;
    }

    private static bool TryDrawDirectionalAtlasPreview(LightComponent light)
    {
        if (!IsDirectionalShadowAtlasActive(light) || light is not DirectionalLightComponent dirLight)
            return false;

        XRWorldInstance? world = dirLight.WorldAs<XRWorldInstance>();
        if (world?.Lights is null)
        {
            ImGui.TextDisabled("Directional atlas preview unavailable until the light belongs to a world.");
            return true;
        }

        int activeCascades = dirLight.ActiveCascadeCount;
        string lightLabel = dirLight.SceneNode?.Name ?? dirLight.Name ?? dirLight.GetType().Name;
        if (!dirLight.EnableCascadedShadows || activeCascades <= 0)
        {
            ImGui.TextDisabled("Previewing active directional atlas tile.");
            if (!dirLight.TryGetPrimaryAtlasSlot(out DirectionalLightComponent.DirectionalCascadeAtlasSlot primarySlot) ||
                !primarySlot.HasAllocation)
            {
                ImGui.TextDisabled("No atlas allocation published for this directional light yet.");
                return true;
            }

            DrawDirectionalAtlasSlotPreview(
                world,
                primarySlot,
                "Directional Shadow Atlas Tile",
                lightLabel,
                "Primary",
                null);
            return true;
        }

        ImGui.TextDisabled("Previewing active directional cascade atlas tiles.");
        ImGui.TextDisabled($"{lightLabel}: {activeCascades} active cascade(s)");

        for (int cascadeIndex = 0; cascadeIndex < activeCascades; cascadeIndex++)
        {
            if (!dirLight.TryGetCascadeAtlasSlot(cascadeIndex, out DirectionalLightComponent.DirectionalCascadeAtlasSlot slot) ||
                !slot.HasAllocation)
            {
                ImGui.TextDisabled($"Cascade {cascadeIndex}: no atlas allocation published yet.");
                continue;
            }

            if (!slot.IsResident)
            {
                ImGui.TextDisabled($"Cascade {cascadeIndex}: not resident. Fallback: {slot.Fallback}");
                continue;
            }

            if (slot.LastRenderedFrame == 0u)
            {
                ImGui.TextDisabled($"Cascade {cascadeIndex}: tile allocated but not rendered yet.");
                continue;
            }

            if (!world.Lights.ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, slot.PageIndex, out _))
            {
                ImGui.TextDisabled($"Cascade {cascadeIndex}: atlas page {slot.PageIndex} unavailable.");
                continue;
            }

            if (cascadeIndex > 0 && cascadeIndex % ArrayPreviewsPerRow != 0)
                ImGui.SameLine();

            ImGui.BeginGroup();
            DrawDirectionalAtlasSlotPreview(
                world,
                slot,
                $"Directional Cascade {cascadeIndex}",
                lightLabel,
                $"Cascade {cascadeIndex}",
                $"Split Far: {dirLight.GetCascadeSplit(cascadeIndex):F1}");
            ImGui.TextDisabled($"Cascade {cascadeIndex}");
            ImGui.TextDisabled($"Split {dirLight.GetCascadeSplit(cascadeIndex):F1}");
            ImGui.EndGroup();
        }

        return true;
    }

    private static void DrawDirectionalAtlasSlotPreview(
        XRWorldInstance world,
        DirectionalLightComponent.DirectionalCascadeAtlasSlot slot,
        string previewLabel,
        string lightLabel,
        string slotLabel,
        string? extraDetails)
    {
        if (!slot.IsResident)
        {
            ImGui.TextDisabled($"{slotLabel}: not resident. Fallback: {slot.Fallback}");
            return;
        }

        if (slot.LastRenderedFrame == 0u)
        {
            ImGui.TextDisabled($"{slotLabel}: tile allocated but not rendered yet.");
            return;
        }

        if (!world.Lights.ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, slot.PageIndex, out XRTexture2D pageTexture))
        {
            ImGui.TextDisabled($"{slotLabel}: atlas page {slot.PageIndex} unavailable.");
            return;
        }

        Vector4 uv = slot.UvScaleBias;
        Vector2 uv0 = new(uv.Z, uv.W + uv.Y);
        Vector2 uv1 = new(uv.Z + uv.X, uv.W);
        Vector2 tilePixelSize = new(
            MathF.Max(1.0f, slot.InnerPixelRect.Width),
            MathF.Max(1.0f, slot.InnerPixelRect.Height));
        string details =
            $"{lightLabel} | {slotLabel}\n" +
            $"Record {slot.RecordIndex} | Page {slot.PageIndex}\n" +
            $"Tile {slot.InnerPixelRect.X},{slot.InnerPixelRect.Y} {slot.InnerPixelRect.Width}x{slot.InnerPixelRect.Height}\n" +
            $"Page {pageTexture.Width}x{pageTexture.Height} | {pageTexture.SizedInternalFormat}\n";
        if (!string.IsNullOrEmpty(extraDetails))
            details += $"{extraDetails}\n";
        details += $"Last rendered frame {slot.LastRenderedFrame}";

        Draw2DTexturePreview(previewLabel, pageTexture, uv0, uv1, tilePixelSize, details);
    }

    private static void DrawDirectionalCascadePreview(global::XREngine.Components.Lights.DirectionalLightComponent light, XRTexture2DArray cascadeTexture, int activeCascades)
    {
        string lightLabel = light.SceneNode?.Name ?? light.Name ?? light.GetType().Name;
        ImGui.TextDisabled($"{lightLabel}: {activeCascades} active cascade(s)");

        for (int cascadeIndex = 0; cascadeIndex < activeCascades; cascadeIndex++)
        {
            XRTexture2DArrayView cascadeView = GetOrCreate2DArrayPreviewView(cascadeTexture, cascadeIndex);
            if (!TryGetTexturePreviewData(cascadeView, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
            {
                ImGui.TextDisabled(failureReason ?? $"Cascade {cascadeIndex} preview unavailable.");
                continue;
            }

            if (cascadeIndex > 0 && cascadeIndex % ArrayPreviewsPerRow != 0)
                ImGui.SameLine();

            Vector2 uv0 = new(0.0f, 1.0f);
            Vector2 uv1 = new(1.0f, 0.0f);
            bool openLargeView = false;

            ImGui.BeginGroup();
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{lightLabel} | Cascade {cascadeIndex}\n" +
                    $"{(int)pixelSize.X} x {(int)pixelSize.Y} | {cascadeTexture.SizedInternalFormat}\n" +
                    $"Split Far: {light.GetCascadeSplit(cascadeIndex):F1}");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openLargeView = true;
            }

            if (ImGui.SmallButton($"View Larger##Cascade{cascadeIndex}"))
                openLargeView = true;

            if (openLargeView)
                ComponentEditorLayout.RequestPreviewDialog($"{lightLabel} Cascade {cascadeIndex}", handle, pixelSize, flipVertically: true);

            ImGui.TextDisabled($"Cascade {cascadeIndex}");
            ImGui.TextDisabled($"Split {light.GetCascadeSplit(cascadeIndex):F1}");
            ImGui.EndGroup();
        }
    }

    private static XRTexture? SelectBestShadowPreviewTexture(IReadOnlyList<XRTexture?> textures)
    {
        // Prefer a texture explicitly intended for sampling (typically named "ShadowMap").
        foreach (var t in textures)
        {
            if (t is null)
                continue;

            if (!string.IsNullOrWhiteSpace(t.SamplerName) && t.SamplerName == "ShadowMap")
                return t;
        }

        // Otherwise fall back to first non-null texture.
        foreach (var t in textures)
            if (t is not null)
                return t;

        return null;
    }

    private static void Draw2DTexturePreview(string label, XRTexture2D texture)
        => Draw2DTexturePreview(
            label,
            texture,
            new Vector2(0.0f, 1.0f),
            new Vector2(1.0f, 0.0f),
            null,
            null);

    private static void Draw2DTexturePreview(
        string label,
        XRTexture2D texture,
        Vector2 uv0,
        Vector2 uv1,
        Vector2? sourcePixelSizeOverride,
        string? details)
    {
        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
            return;
        }

        Vector2 sourcePixelSize = sourcePixelSizeOverride ?? pixelSize;
        displaySize = GetPreviewSize(sourcePixelSize);
        bool openLargeView = false;

        ImGui.Image(handle, displaySize, uv0, uv1);
        string info = details ?? FormatTextureInfo(texture, sourcePixelSize);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(info);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                openLargeView = true;
        }

        if (ImGui.SmallButton(sourcePixelSizeOverride.HasValue ? "View Page" : "View Larger"))
            openLargeView = true;

        if (openLargeView)
            ComponentEditorLayout.RequestPreviewDialog($"{label} Preview", handle, pixelSize, flipVertically: true);

        ImGui.TextDisabled(info);
    }

    private static void Draw2DArrayTexturePreview(string label, XRTexture2DArray texture, int layerCount)
    {
        int previewCount = Math.Max(1, layerCount);
        for (int layerIndex = 0; layerIndex < previewCount; ++layerIndex)
        {
            XRTexture2DArrayView previewView = GetOrCreate2DArrayPreviewView(texture, layerIndex);
            if (!TryGetTexturePreviewData(previewView, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
            {
                ImGui.TextDisabled(failureReason ?? $"Layer {layerIndex} preview unavailable.");
                continue;
            }

            if (layerIndex > 0 && layerIndex % ArrayPreviewsPerRow != 0)
                ImGui.SameLine();

            Vector2 uv0 = new(0.0f, 1.0f);
            Vector2 uv1 = new(1.0f, 0.0f);
            bool openLargeView = false;

            ImGui.BeginGroup();
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{label} Layer {layerIndex}\n{FormatTextureInfo(texture, pixelSize)}");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openLargeView = true;
            }

            if (ImGui.SmallButton($"View Larger##{label}Layer{layerIndex}"))
                openLargeView = true;

            if (openLargeView)
                ComponentEditorLayout.RequestPreviewDialog($"{label} Layer {layerIndex}", handle, pixelSize, flipVertically: true);

            ImGui.TextDisabled($"Layer {layerIndex}");
            ImGui.EndGroup();
        }
    }

    private static void DrawCubemapPreview(string label, XRTextureCube cubemap)
    {
        var previewCache = CubemapPreviewCaches.GetValue(cubemap, cube => new CubemapPreviewCache(cube));

        float extent = MathF.Max(1.0f, cubemap.Extent);
        Vector2 pixelSize = new(extent, extent);
        Vector2 displaySize = GetPreviewSize(pixelSize);

        const int facesPerRow = 3;
        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);

        for (int i = 0; i < CubemapFaces.Length; ++i)
        {
            var (face, faceLabel) = CubemapFaces[i];
            var view = previewCache.GetFaceView(face);

            if (!previewCache.TryGetFacePreviewData(face, out nint handle, out _, out _, out string? failureReason))
            {
                ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
                return;
            }

            if (i % facesPerRow != 0)
                ImGui.SameLine();

            ImGui.BeginGroup();
            bool openLarge = false;
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{label} {faceLabel}");
                ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y} | {cubemap.SizedInternalFormat}");
                ImGui.EndTooltip();
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    openLarge = true;
            }

            if (openLarge)
                ComponentEditorLayout.RequestPreviewDialog($"{label} {faceLabel}", handle, pixelSize, flipVertically: true);

            ImGui.TextDisabled(faceLabel);
            ImGui.EndGroup();
        }
    }

    private static bool TryGetTexturePreviewData(
        XRTexture texture,
        out nint handle,
        out Vector2 displaySize,
        out Vector2 pixelSize,
        out string? failureReason)
    {
        pixelSize = GetTexturePixelSize(texture);
        displaySize = GetPreviewSize(pixelSize);
        handle = nint.Zero;
        failureReason = null;

        if (!Engine.IsRenderThread)
        {
            failureReason = "Preview unavailable outside render thread.";
            return false;
        }

        if (TryGetVulkanRenderer() is VulkanRenderer vkRenderer)
        {
            IntPtr textureId = vkRenderer.RegisterImGuiTexture(texture);
            if (textureId == IntPtr.Zero)
            {
                failureReason = "Texture not uploaded to GPU.";
                return false;
            }

            handle = (nint)textureId;
            return true;
        }

        var renderer = TryGetOpenGLRenderer();
        if (renderer is null)
        {
            failureReason = "Preview requires OpenGL or Vulkan renderer.";
            return false;
        }

        switch (texture)
        {
            case XRTexture2D tex2D:
                var glTexture2D = renderer.GenericToAPI<GLTexture2D>(tex2D);
                if (!TryGetTextureHandle(renderer, glTexture2D, out handle, out uint tex2DBinding, out failureReason))
                    return false;

                ApplySingleChannelPreviewSwizzle(renderer, tex2D.SizedInternalFormat, glTexture2D, tex2DBinding);
                return true;
            case XRTexture2DView tex2DView:
                var glTexture2DView = renderer.GenericToAPI<GLTextureView>(tex2DView);
                if (!TryGetTextureHandle(renderer, glTexture2DView, out handle, out uint tex2DViewBinding, out failureReason))
                    return false;

                ApplySingleChannelPreviewSwizzle(renderer, tex2DView.InternalFormat, glTexture2DView, tex2DViewBinding);
                return true;
            case XRTexture2DArrayView tex2DArrayView:
                pixelSize = new Vector2(MathF.Max(1.0f, tex2DArrayView.Width), MathF.Max(1.0f, tex2DArrayView.Height));
                displaySize = GetPreviewSize(pixelSize);
                var glTexture2DArrayView = renderer.GenericToAPI<GLTextureView>(tex2DArrayView);
                if (!TryGetTextureHandle(renderer, glTexture2DArrayView, out handle, out uint tex2DArrayViewBinding, out failureReason))
                    return false;

                ApplySingleChannelPreviewSwizzle(renderer, tex2DArrayView.InternalFormat, glTexture2DArrayView, tex2DArrayViewBinding);
                return true;
            case XRTextureCubeView cubeView when cubeView.View2D:
                float extent = MathF.Max(1.0f, cubeView.ViewedTexture.Extent);
                pixelSize = new Vector2(extent, extent);
                displaySize = GetPreviewSize(pixelSize);
                var glTextureView = renderer.GenericToAPI<GLTextureView>(cubeView);
                if (!TryGetTextureHandle(renderer, glTextureView, out handle, out uint cubeViewBinding, out failureReason))
                    return false;

                ApplySingleChannelPreviewSwizzle(renderer, cubeView.InternalFormat, glTextureView, cubeViewBinding);
                return true;
            default:
                failureReason = $"{texture.GetType().Name} preview not supported.";
                return false;
        }
    }

    private static void ApplySingleChannelPreviewSwizzle(OpenGLRenderer renderer, ESizedInternalFormat format, IGLTexture? glTexture, uint binding)
    {
        if (glTexture is null || !IsSingleChannelFormat(format))
            return;

        if (binding == 0 || binding == OpenGLRenderer.GLObjectBase.InvalidBindingId || !renderer.RawGL.IsTexture(binding))
            return;

        PreviewSwizzleState state = TexturePreviewSwizzles.GetValue(glTexture, _ => new PreviewSwizzleState());
        if (state.BindingId == binding && state.Format == format)
            return;

        var gl = renderer.RawGL;
        int red = (int)GLEnum.Red;
        int one = (int)GLEnum.One;
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleR, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleG, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleB, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleA, in one);

        state.BindingId = binding;
        state.Format = format;
    }

    private static ShadowResolutionEditState GetShadowResolutionEditState(LightComponent light)
    {
        ShadowResolutionEditState state = ShadowResolutionEditStates.GetValue(light, _ => new ShadowResolutionEditState());
        state.SyncFrom(light);
        return state;
    }

    private static bool DrawCommittedIntInput(string label, ref int value)
    {
        bool enterPressed = ImGui.InputInt(label, ref value, 1, 100, ImGuiInputTextFlags.EnterReturnsTrue);
        return enterPressed || ImGui.IsItemDeactivatedAfterEdit();
    }

    private static void CommitShadowMapResolution(LightComponent light, ShadowResolutionEditState state, int width, int height)
    {
        int clampedWidth = Math.Max(1, width);
        int clampedHeight = Math.Max(1, height);

        if (clampedWidth == (int)light.ShadowMapResolutionWidth &&
            clampedHeight == (int)light.ShadowMapResolutionHeight)
        {
            state.SetCommitted(clampedWidth, clampedHeight);
            return;
        }

        light.SetShadowMapResolution(unchecked((uint)clampedWidth), unchecked((uint)clampedHeight));
        state.SetCommitted(clampedWidth, clampedHeight);
    }

    private static XRTexture2DArrayView GetOrCreate2DArrayPreviewView(XRTexture2DArray texture, int layerIndex)
    {
        var views = Texture2DArrayPreviewViews.GetOrCreateValue(texture);
        if (views.TryGetValue(layerIndex, out XRTexture2DArrayView? existing))
        {
            existing.MinLevel = 0u;
            existing.NumLevels = 1u;
            existing.MinLayer = (uint)layerIndex;
            existing.NumLayers = 1u;
            return existing;
        }

        var view = new XRTexture2DArrayView(texture, 0u, 1u, (uint)layerIndex, 1u, texture.SizedInternalFormat, false, texture.MultiSample)
        {
            Name = $"{texture.Name ?? "ShadowMap"}_Layer{layerIndex}_Preview",
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };

        views[layerIndex] = view;
        return view;
    }

    private static bool IsSingleChannelFormat(ESizedInternalFormat format)
        => format switch
        {
            ESizedInternalFormat.R8 or
            ESizedInternalFormat.R8Snorm or
            ESizedInternalFormat.R16 or
            ESizedInternalFormat.R16Snorm or
            ESizedInternalFormat.R16f or
            ESizedInternalFormat.R32f or
            ESizedInternalFormat.R8i or
            ESizedInternalFormat.R8ui or
            ESizedInternalFormat.R16i or
            ESizedInternalFormat.R16ui or
            ESizedInternalFormat.R32i or
            ESizedInternalFormat.R32ui or
            ESizedInternalFormat.DepthComponent16 or
            ESizedInternalFormat.DepthComponent24 or
            ESizedInternalFormat.DepthComponent32f or
            ESizedInternalFormat.Depth24Stencil8 or
            ESizedInternalFormat.Depth32fStencil8 => true,
            _ => false,
        };

    private static bool TryGetTextureHandle(
        OpenGLRenderer renderer,
        IGLTexture? glTexture,
        out nint handle,
        out uint bindingId,
        out string? failureReason)
    {
        handle = nint.Zero;
        bindingId = 0u;
        failureReason = null;
        if (glTexture is null)
        {
            failureReason = "Texture not uploaded to GPU.";
            return false;
        }

        bindingId = glTexture.BindingId;
        if (bindingId == 0 || bindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
        {
            failureReason = "Texture handle invalid.";
            return false;
        }

        if (!renderer.RawGL.IsTexture(bindingId))
        {
            failureReason = "Texture object not ready.";
            return false;
        }

        handle = (nint)bindingId;
        return true;
    }

    private static OpenGLRenderer? TryGetOpenGLRenderer()
    {
        if (AbstractRenderer.Current is OpenGLRenderer current)
            return current;

        foreach (var window in Engine.Windows)
            if (window.Renderer is OpenGLRenderer renderer)
                return renderer;

        return null;
    }

    private static VulkanRenderer? TryGetVulkanRenderer()
    {
        if (AbstractRenderer.Current is VulkanRenderer current)
            return current;

        foreach (var window in Engine.Windows)
            if (window.Renderer is VulkanRenderer renderer)
                return renderer;

        return null;
    }

    private static Vector2 GetPreviewSize(Vector2 pixelSize)
    {
        float width = MathF.Max(1.0f, pixelSize.X);
        float height = MathF.Max(1.0f, pixelSize.Y);

        float maxEdge = MathF.Max(width, height);
        if (maxEdge <= 0.0f)
            return new Vector2(PreviewFallbackEdge, PreviewFallbackEdge);

        if (maxEdge <= PreviewMaxEdge)
            return new Vector2(width, height);

        float scale = PreviewMaxEdge / maxEdge;
        return new Vector2(width * scale, height * scale);
    }

    private static Vector2 GetTexturePixelSize(XRTexture texture)
        => texture switch
        {
            XRTexture2D tex2D => new Vector2(MathF.Max(1.0f, tex2D.Width), MathF.Max(1.0f, tex2D.Height)),
            XRTexture2DView tex2DView => new Vector2(MathF.Max(1.0f, tex2DView.Width), MathF.Max(1.0f, tex2DView.Height)),
            XRTexture2DArrayView tex2DArrayView => new Vector2(MathF.Max(1.0f, tex2DArrayView.Width), MathF.Max(1.0f, tex2DArrayView.Height)),
            XRTextureCubeView cubeView when cubeView.View2D =>
                new Vector2(MathF.Max(1.0f, cubeView.ViewedTexture.Extent), MathF.Max(1.0f, cubeView.ViewedTexture.Extent)),
            _ => new Vector2(MathF.Max(1.0f, texture.WidthHeightDepth.X), MathF.Max(1.0f, texture.WidthHeightDepth.Y)),
        };

    private static string FormatTextureInfo(XRTexture texture, Vector2 pixelSize)
    {
        string resolution = $"{(int)pixelSize.X} x {(int)pixelSize.Y}";
        string? format = texture switch
        {
            XRTexture2D tex2D => tex2D.SizedInternalFormat.ToString(),
            XRTexture2DView tex2DView => tex2DView.ViewedTexture.SizedInternalFormat.ToString(),
            XRTexture2DArray tex2DArray => tex2DArray.SizedInternalFormat.ToString(),
            XRTexture2DArrayView tex2DArrayView => tex2DArrayView.ViewedTexture.SizedInternalFormat.ToString(),
            XRTextureCubeView cubeView when cubeView.View2D => cubeView.ViewedTexture.SizedInternalFormat.ToString(),
            _ => null,
        };

        return format is null ? resolution : $"{resolution} | {format}";
    }

    private sealed class ShadowResolutionEditState
    {
        private uint _sourceWidth = uint.MaxValue;
        private uint _sourceHeight = uint.MaxValue;

        public int Width;
        public int Height;
        public int CubemapResolution;

        public void SyncFrom(LightComponent light)
        {
            uint sourceWidth = light.ShadowMapResolutionWidth;
            uint sourceHeight = light.ShadowMapResolutionHeight;
            if (_sourceWidth == sourceWidth && _sourceHeight == sourceHeight)
                return;

            Width = unchecked((int)Math.Max(1u, sourceWidth));
            Height = unchecked((int)Math.Max(1u, sourceHeight));
            CubemapResolution = Math.Max(Width, Height);
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
        }

        public void SetCommitted(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            CubemapResolution = Math.Max(Width, Height);
            _sourceWidth = unchecked((uint)Width);
            _sourceHeight = unchecked((uint)Height);
        }
    }

    private sealed class PreviewSwizzleState
    {
        public uint BindingId;
        public ESizedInternalFormat Format;
    }

    private sealed class CubemapPreviewCache
    {
        private readonly XRTextureCubeView[] _faceViews = new XRTextureCubeView[6];
        private readonly string?[] _faceFailureReasons = new string?[6];

        public CubemapPreviewCache(XRTextureCube source)
        {
            for (int i = 0; i < _faceViews.Length; ++i)
                _faceViews[i] = CreateFaceView(source, (ECubemapFace)i);
        }

        public XRTextureCubeView GetFaceView(ECubemapFace face)
            => _faceViews[(int)face];

        public bool TryGetFacePreviewData(
            ECubemapFace face,
            out nint handle,
            out Vector2 displaySize,
            out Vector2 pixelSize,
            out string? failureReason)
        {
            int faceIndex = (int)face;

            if (TryGetTexturePreviewData(_faceViews[faceIndex], out handle, out displaySize, out pixelSize, out failureReason))
                return true;

            _faceFailureReasons[faceIndex] = failureReason ?? "Preview unavailable.";

            // Return the last failure reason but do NOT permanently cache the failure.
            // The texture view may not be ready yet (e.g. cubemap not populated on first frame).
            handle = nint.Zero;
            pixelSize = new Vector2(MathF.Max(1.0f, _faceViews[faceIndex].ViewedTexture.Extent));
            displaySize = GetPreviewSize(pixelSize);
            failureReason = _faceFailureReasons[faceIndex];
            return false;
        }

        private static XRTextureCubeView CreateFaceView(XRTextureCube source, ECubemapFace face)
        {
            var view = new XRTextureCubeView(
                source,
                minLevel: 0u,
                numLevels: 1u,
                minLayer: (uint)face,
                numLayers: 1u,
                source.SizedInternalFormat,
                array: false,
                view2D: true)
            {
                Name = $"{source.Name ?? "ShadowMap"}_{face}_Preview",
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            };

            return view;
        }
    }
}
