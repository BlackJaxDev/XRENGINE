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
        if (ImGui.DragFloat("Shadow Exponent", ref exp, 0.001f, 0.0f, 100.0f, "%.4f"))
            light.ShadowExponent = MathF.Max(0.0f, exp);

        bool pcss = light.EnablePCSS;
        if (ImGui.Checkbox("Enable PCSS", ref pcss))
            light.EnablePCSS = pcss;

        if (light is not PointLightComponent || pcss)
        {
            ImGui.Indent();

            int samples = light.Samples;
            if (ImGui.InputInt("Samples", ref samples))
                light.Samples = Math.Max(1, samples);

            float filter = light.FilterRadius;
            if (ImGui.DragFloat("Filter Radius", ref filter, 0.0001f, 0.0f, 1.0f, "%.6f"))
                light.FilterRadius = MathF.Max(0.0f, filter);

            ImGui.Unindent();
        }

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
                light.WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights?.LightmapBaking?.RequestBake(light);

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
