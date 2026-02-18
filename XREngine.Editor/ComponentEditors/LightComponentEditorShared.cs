using ImGuiNET;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
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
            ImGui.SetTooltip("Currently maps to ELightType (Dynamic / DynamicCached / Static). Shadow baking workflow is not fully implemented yet.");
    }

    public static void DrawShadowSection(LightComponent light, bool showCascadedOptions)
    {
        if (!ImGui.CollapsingHeader("Shadows", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool casts = light.CastsShadows;
        if (ImGui.Checkbox("Casts Shadows", ref casts))
            light.CastsShadows = casts;

        uint width = light.ShadowMapResolutionWidth;
        uint height = light.ShadowMapResolutionHeight;
        int w = unchecked((int)width);
        int h = unchecked((int)height);

        bool changed = false;
        if (ImGui.InputInt("Shadow Map Width", ref w))
        {
            w = Math.Max(1, w);
            changed = true;
        }
        if (ImGui.InputInt("Shadow Map Height", ref h))
        {
            h = Math.Max(1, h);
            changed = true;
        }

        if (changed)
            light.SetShadowMapResolution(unchecked((uint)w), unchecked((uint)h));

        float minBias = light.ShadowMinBias;
        if (ImGui.DragFloat("Min Bias", ref minBias, 0.0001f, 0.0f, 1000.0f, "%.6f"))
            light.ShadowMinBias = MathF.Max(0.0f, minBias);

        float maxBias = light.ShadowMaxBias;
        if (ImGui.DragFloat("Max Bias", ref maxBias, 0.0001f, 0.0f, 1000.0f, "%.6f"))
            light.ShadowMaxBias = MathF.Max(0.0f, maxBias);

        float expBase = light.ShadowExponentBase;
        if (ImGui.DragFloat("Exponent Base", ref expBase, 0.001f, 0.0f, 100.0f, "%.4f"))
            light.ShadowExponentBase = MathF.Max(0.0f, expBase);

        float exp = light.ShadowExponent;
        if (ImGui.DragFloat("Exponent", ref exp, 0.001f, 0.0f, 100.0f, "%.4f"))
            light.ShadowExponent = MathF.Max(0.0f, exp);

        ImGui.Separator();

        int samples = light.Samples;
        if (ImGui.InputInt("Samples", ref samples))
            light.Samples = Math.Max(1, samples);

        float filter = light.FilterRadius;
        if (ImGui.DragFloat("Filter Radius", ref filter, 0.0001f, 0.0f, 1.0f, "%.6f"))
            light.FilterRadius = MathF.Max(0.0f, filter);

        bool pcss = light.EnablePCSS;
        if (ImGui.Checkbox("Enable PCSS", ref pcss))
            light.EnablePCSS = pcss;

        if (showCascadedOptions)
        {
            bool cascaded = light.EnableCascadedShadows;
            if (ImGui.Checkbox("Enable Cascaded Shadows", ref cascaded))
                light.EnableCascadedShadows = cascaded;
        }

        bool contact = light.EnableContactShadows;
        if (ImGui.Checkbox("Enable Contact Shadows", ref contact))
            light.EnableContactShadows = contact;

        if (contact)
        {
            ImGui.Indent();
            float dist = light.ContactShadowDistance;
            if (ImGui.DragFloat("Contact Distance", ref dist, 0.001f, 0.0f, 10.0f, "%.4f"))
                light.ContactShadowDistance = MathF.Max(0.0f, dist);

            int cs = light.ContactShadowSamples;
            if (ImGui.InputInt("Contact Samples", ref cs))
                light.ContactShadowSamples = Math.Max(1, cs);
            ImGui.Unindent();
        }

        if (light.Type == ELightType.Static)
        {
            ImGui.Separator();

            if (ImGui.Button("Bake Lightmaps"))
                light.World?.Lights?.LightmapBaking?.RequestBake(light);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Queues a lightmap bake for Static meshes. (Lightmap rendering is scaffolded but not implemented yet.)");
        }
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
            case XRTextureCube cube:
                DrawCubemapPreview("ShadowMap", cube);
                break;
            default:
                ImGui.TextDisabled($"{texture.GetType().Name} preview not supported.");
                break;
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

            if (!TryGetTexturePreviewData(view, out nint handle, out _, out _, out string? failureReason))
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
                return TryGetTextureHandle(renderer.GenericToAPI<GLTexture2D>(tex2D), out handle, out failureReason);
            case XRTextureCubeView cubeView when cubeView.View2D:
                float extent = MathF.Max(1.0f, cubeView.ViewedTexture.Extent);
                pixelSize = new Vector2(extent, extent);
                displaySize = GetPreviewSize(pixelSize);
                return TryGetTextureHandle(renderer.GenericToAPI<GLTextureView>(cubeView), out handle, out failureReason);
            default:
                failureReason = $"{texture.GetType().Name} preview not supported.";
                return false;
        }
    }

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
            XRTextureCubeView cubeView when cubeView.View2D => cubeView.ViewedTexture.SizedInternalFormat.ToString(),
            _ => null,
        };

        return format is null ? resolution : $"{resolution} | {format}";
    }

    private sealed class CubemapPreviewCache
    {
        private readonly XRTextureCubeView[] _faceViews = new XRTextureCubeView[6];

        public CubemapPreviewCache(XRTextureCube source)
        {
            for (int i = 0; i < _faceViews.Length; ++i)
                _faceViews[i] = CreateFaceView(source, (ECubemapFace)i);
        }

        public XRTextureCubeView GetFaceView(ECubemapFace face)
            => _faceViews[(int)face];

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
