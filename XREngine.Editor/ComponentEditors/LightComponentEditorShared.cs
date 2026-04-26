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
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor.ComponentEditors;

internal static class LightComponentEditorShared
{
    private const float PreviewMaxEdge = 196.0f;
    private const float PreviewFallbackEdge = 96.0f;
    private const int ArrayPreviewsPerRow = 3;
    private const string ShadowFilterModeItems =
        "Hard / PCF\0Fixed Soft (Poisson)\0PCSS / Contact Hardening\0Fixed Soft (Vogel)\0";

    private static readonly ConditionalWeakTable<XRTexture2D, XRTexture2DView> Texture2DPreviewViews = new();
    private static readonly ConditionalWeakTable<XRTexture2DArray, Dictionary<int, XRTexture2DArrayView>> Texture2DArrayPreviewViews = new();
    private static readonly ConditionalWeakTable<XRTextureCube, CubemapPreviewCache> CubemapPreviewCaches = new();

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

        DrawShadowMapControls(light, showCascadedOptions);
        DrawShadowBiasControls(light);
        DrawShadowFilteringControls(light);
        DrawContactShadowControls(light);
        DrawShadowDebugControls(light);
        DrawLightmapBakeControls(light);
    }

    private static void DrawShadowMapControls(LightComponent light, bool showCascadedOptions)
    {
        ImGui.SeparatorText("Shadow Map");

        bool casts = light.CastsShadows;
        if (ImGui.Checkbox("Casts Shadows", ref casts))
            light.CastsShadows = casts;

        if (light is PointLightComponent)
        {
            int resolution = unchecked((int)Math.Max(light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight));
            if (ImGui.InputInt("Cubemap Face Resolution", ref resolution))
            {
                resolution = Math.Max(1, resolution);
                light.SetShadowMapResolution(unchecked((uint)resolution), unchecked((uint)resolution));
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Point-light shadows render one square cubemap face per direction.");
        }
        else
        {
            int w = unchecked((int)light.ShadowMapResolutionWidth);
            int h = unchecked((int)light.ShadowMapResolutionHeight);

            bool changed = false;
            if (ImGui.InputInt("Map Width", ref w))
            {
                w = Math.Max(1, w);
                changed = true;
            }
            if (ImGui.InputInt("Map Height", ref h))
            {
                h = Math.Max(1, h);
                changed = true;
            }

            if (changed)
                light.SetShadowMapResolution(unchecked((uint)w), unchecked((uint)h));
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
        ImGui.SeparatorText("Filtering");

        int softMode = (int)light.SoftShadowMode;
        if (ImGui.Combo("Filter Mode", ref softMode, ShadowFilterModeItems))
            light.SoftShadowMode = (ESoftShadowMode)Math.Clamp(softMode, 0, 3);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hard / PCF = crisp compare or small PCF fallback.\nFixed Soft (Poisson) = constant-radius Poisson filter.\nPCSS / Contact Hardening = blocker search plus variable penumbra.\nFixed Soft (Vogel) = constant-radius golden-angle disk taps.");

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
        float lightRadius = light.LightSourceRadius;
        if (ImGui.DragFloat("Light Source Radius", ref lightRadius, 0.001f, 0.0f, 10.0f, "%.4f"))
            light.LightSourceRadius = MathF.Max(0.0f, lightRadius);

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
        if (!ImGui.CollapsingHeader("Shadow Map Preview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!light.CastsShadows)
        {
            ImGui.TextDisabled("Shadows disabled for this light.");
            return;
        }

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
    {
        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
            return;
        }

        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);
        bool openLargeView = false;

        ImGui.Image(handle, displaySize, uv0, uv1);
        string info = FormatTextureInfo(texture, pixelSize);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(info);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                openLargeView = true;
        }

        if (ImGui.SmallButton("View Larger"))
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
        texture = ResolvePreviewTexture(texture);
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
                return TryGetTextureHandle(renderer.GenericToAPI<GLTexture2D>(tex2D), out handle, out failureReason);
            case XRTexture2DView tex2DView:
                var glTexture2DView = renderer.GenericToAPI<GLTextureView>(tex2DView);
                ApplySingleChannelPreviewSwizzle(renderer, tex2DView.ViewedTexture.SizedInternalFormat, glTexture2DView);
                return TryGetTextureHandle(glTexture2DView, out handle, out failureReason);
            case XRTexture2DArrayView tex2DArrayView:
                pixelSize = new Vector2(MathF.Max(1.0f, tex2DArrayView.Width), MathF.Max(1.0f, tex2DArrayView.Height));
                displaySize = GetPreviewSize(pixelSize);
                var glTexture2DArrayView = renderer.GenericToAPI<GLTextureView>(tex2DArrayView);
                ApplySingleChannelPreviewSwizzle(renderer, tex2DArrayView.ViewedTexture.SizedInternalFormat, glTexture2DArrayView);
                return TryGetTextureHandle(glTexture2DArrayView, out handle, out failureReason);
            case XRTextureCubeView cubeView when cubeView.View2D:
                float extent = MathF.Max(1.0f, cubeView.ViewedTexture.Extent);
                pixelSize = new Vector2(extent, extent);
                displaySize = GetPreviewSize(pixelSize);
                var glTextureView = renderer.GenericToAPI<GLTextureView>(cubeView);
                ApplySingleChannelPreviewSwizzle(renderer, cubeView.ViewedTexture.SizedInternalFormat, glTextureView);
                return TryGetTextureHandle(glTextureView, out handle, out failureReason);
            default:
                failureReason = $"{texture.GetType().Name} preview not supported.";
                return false;
        }
    }

    private static XRTexture ResolvePreviewTexture(XRTexture texture)
        => texture is XRTexture2D tex2D && IsSingleChannelFormat(tex2D.SizedInternalFormat)
            ? Texture2DPreviewViews.GetValue(tex2D, CreateTexture2DPreviewView)
            : texture;

    private static void ApplySingleChannelPreviewSwizzle(OpenGLRenderer renderer, ESizedInternalFormat format, GLTextureView? glTextureView)
    {
        if (glTextureView is null || !IsSingleChannelFormat(format))
            return;

        uint binding = glTextureView.BindingId;
        if (binding == 0 || binding == OpenGLRenderer.GLObjectBase.InvalidBindingId)
            return;

        var gl = renderer.RawGL;
        int red = (int)GLEnum.Red;
        int one = (int)GLEnum.One;
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleR, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleG, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleB, in red);
        gl.TextureParameterI(binding, GLEnum.TextureSwizzleA, in one);
    }

    private static XRTexture2DView CreateTexture2DPreviewView(XRTexture2D source)
        => new(source, 0u, 1u, source.SizedInternalFormat, false, source.MultiSample)
        {
            Name = $"{source.Name ?? "ShadowMap"}_Preview",
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };

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
            ESizedInternalFormat.R32ui => true,
            _ => false,
        };

    private static bool TryGetTextureHandle(IGLTexture? glTexture, out nint handle, out string? failureReason)
    {
        handle = nint.Zero;
        failureReason = null;
        if (glTexture is null)
        {
            failureReason = "Texture not uploaded to GPU.";
            return false;
        }

        uint bindingId = glTexture.BindingId;
        if (bindingId == 0 || bindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
        {
            failureReason = "Texture handle invalid.";
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
