using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Editor;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor.ComponentEditors;

public sealed class LightProbeComponentEditor : IXRComponentEditor
{
    private const float PreviewMaxEdge = 196.0f;
    private const float PreviewFallbackEdge = 96.0f;
    private static readonly Vector4 ActivePreviewColor = new(0.45f, 0.85f, 0.55f, 1.00f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not LightProbeComponent lightProbe)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return;
        }

        ImGui.SeparatorText("Component");
        UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(lightProbe, visited);

        ImGui.SeparatorText("Capture Previews");
        DrawCubemapPreview(lightProbe);
        DrawCapturePreview(lightProbe, "Environment (Octa)", lightProbe.EnvironmentTextureOctahedral, LightProbeComponent.ERenderPreview.Environment);
        DrawCapturePreview(lightProbe, "Irradiance", lightProbe.IrradianceTexture, LightProbeComponent.ERenderPreview.Irradiance);
        DrawCapturePreview(lightProbe, "Prefilter", lightProbe.PrefilterTexture, LightProbeComponent.ERenderPreview.Prefilter);
    }

    private static readonly (ECubemapFace Face, string Label)[] CubemapFaces =
    [
        (ECubemapFace.PosX, "+X"),
        (ECubemapFace.NegX, "-X"),
        (ECubemapFace.PosY, "+Y"),
        (ECubemapFace.NegY, "-Y"),
        (ECubemapFace.PosZ, "+Z"),
        (ECubemapFace.NegZ, "-Z"),
    ];

    private static readonly ConditionalWeakTable<XRTextureCube, CubemapPreviewCache> CubemapPreviewCaches = new();

    private static void DrawCubemapPreview(LightProbeComponent lightProbe)
    {
        ImGui.PushID("EnvironmentCubemap");
        ImGui.TextUnformatted("Environment (Cubemap)");

        var cubemap = lightProbe.EnvironmentTextureCubemap;
        if (cubemap is null)
        {
            ImGui.TextDisabled("Capture not generated yet.");
            ImGui.PopID();
            return;
        }

        var previewCache = CubemapPreviewCaches.GetValue(cubemap, cube => new CubemapPreviewCache(cube));
        float faceExtent = MathF.Max(1.0f, cubemap.Extent);
        Vector2 pixelSize = new(faceExtent, faceExtent);
        Vector2 displaySize = GetPreviewSize(pixelSize);

        const int facesPerRow = 3;
        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);

        for (int i = 0; i < CubemapFaces.Length; ++i)
        {
            var (face, label) = CubemapFaces[i];
            bool hasPreview = TryGetTexturePreviewData(
                previewCache.GetFaceView(face),
                out nint handle,
                out _,
                out _,
                out string? failureReason);

            if (!hasPreview)
            {
                ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
                ImGui.PopID();
                return;
            }

            if (i % facesPerRow != 0)
                ImGui.SameLine();

            ImGui.BeginGroup();
            ImGui.Image(handle, displaySize, uv0, uv1);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{label} face");
                ImGui.TextDisabled($"{(int)pixelSize.X} x {(int)pixelSize.Y} | {cubemap.SizedInternalFormat}");
                ImGui.EndTooltip();
            }
            ImGui.TextDisabled(label);
            ImGui.EndGroup();
        }

        ImGui.PopID();
    }

    private static void DrawCapturePreview(
        LightProbeComponent lightProbe,
        string label,
        XRTexture? texture,
        LightProbeComponent.ERenderPreview previewMode)
    {
        ImGui.PushID(label);
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        bool isActivePreview = lightProbe.PreviewDisplay == previewMode;
        ImGui.BeginDisabled(isActivePreview);
        if (ImGui.SmallButton("Use Scene Preview"))
            lightProbe.PreviewDisplay = previewMode;
        ImGui.EndDisabled();
        if (isActivePreview)
        {
            ImGui.SameLine();
            ImGui.TextColored(ActivePreviewColor, "(Active)");
        }

        if (texture is null)
        {
            ImGui.TextDisabled("Capture not generated yet.");
            ImGui.PopID();
            return;
        }

        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
            ImGui.PopID();
            return;
        }

        Vector2 uv0 = new(0.0f, 1.0f);
        Vector2 uv1 = new(1.0f, 0.0f);
        ImGui.Image(handle, displaySize, uv0, uv1);

        string info = FormatTextureInfo(texture, pixelSize);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(info);
        ImGui.TextDisabled(info);

        ImGui.PopID();
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

        var renderer = TryGetOpenGLRenderer();
        if (renderer is null)
        {
            failureReason = "Preview requires OpenGL renderer.";
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
                Name = $"{source.Name ?? "SceneCaptureEnvColor"}_{face}_Preview",
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            };

            return view;
        }
    }
}
