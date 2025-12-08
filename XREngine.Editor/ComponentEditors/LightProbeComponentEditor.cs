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
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(lightProbe, visited, "Light Probe Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawProbeStatus(lightProbe);
        DrawProxyAndInfluence(lightProbe);
        DrawHdrAndStreaming(lightProbe);
        DrawCapturePreviews(lightProbe);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawProbeStatus(LightProbeComponent lightProbe)
    {
        ImGui.SeparatorText("Probe Status");

        bool realtime = lightProbe.RealtimeCapture;
        if (ImGui.Checkbox("Realtime Capture", ref realtime))
            lightProbe.RealtimeCapture = realtime;

        bool gizmoVisible = lightProbe.PreviewEnabled;
        if (ImGui.Checkbox("Show Probe Gizmo", ref gizmoVisible))
            lightProbe.PreviewEnabled = gizmoVisible;

        bool drawInfluence = lightProbe.RenderInfluenceOnSelection;
        if (ImGui.Checkbox("Draw Influence/Proxy Gizmos", ref drawInfluence))
            lightProbe.RenderInfluenceOnSelection = drawInfluence;

        ImGui.TextDisabled($"Preview Mode: {lightProbe.PreviewDisplay}");

        XRTexture? previewTexture = lightProbe.GetPreviewTexture();
        string displayName = previewTexture?.Name ?? "<pending capture>";
        ImGui.TextDisabled($"Active Texture: {displayName}");
    }

    private static void DrawCapturePreviews(LightProbeComponent lightProbe)
    {
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

    private static void DrawProxyAndInfluence(LightProbeComponent lightProbe)
    {
        if (!ImGui.CollapsingHeader("Proxy & Influence", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool parallax = lightProbe.ParallaxCorrectionEnabled;
        if (ImGui.Checkbox("Enable Parallax Correction", ref parallax))
            lightProbe.ParallaxCorrectionEnabled = parallax;

        Vector3 proxyCenter = lightProbe.ProxyBoxCenterOffset;
        if (ImGui.DragFloat3("Proxy Center Offset", ref proxyCenter, 0.01f))
            lightProbe.ProxyBoxCenterOffset = proxyCenter;

        Vector3 proxyExtents = lightProbe.ProxyBoxHalfExtents;
        if (ImGui.DragFloat3("Proxy Half Extents", ref proxyExtents, 0.01f, 0.001f))
            lightProbe.ProxyBoxHalfExtents = new Vector3(
                MathF.Max(0.0001f, proxyExtents.X),
                MathF.Max(0.0001f, proxyExtents.Y),
                MathF.Max(0.0001f, proxyExtents.Z));

        Vector4 proxyRotation = new(
            lightProbe.ProxyBoxRotation.X,
            lightProbe.ProxyBoxRotation.Y,
            lightProbe.ProxyBoxRotation.Z,
            lightProbe.ProxyBoxRotation.W);
        if (ImGui.DragFloat4("Proxy Rotation (xyzw)", ref proxyRotation, 0.01f))
        {
            var q = new Quaternion(proxyRotation.X, proxyRotation.Y, proxyRotation.Z, proxyRotation.W);
            if (q.LengthSquared() > float.Epsilon)
                q = Quaternion.Normalize(q);
            lightProbe.ProxyBoxRotation = q;
        }

        ImGui.Separator();
        int influenceShape = (int)lightProbe.InfluenceShape;
        if (ImGui.Combo("Influence Shape", ref influenceShape, "Sphere\0Box\0"))
            lightProbe.InfluenceShape = (LightProbeComponent.EInfluenceShape)influenceShape;

        Vector3 influenceOffset = lightProbe.InfluenceOffset;
        if (ImGui.DragFloat3("Influence Offset", ref influenceOffset, 0.01f))
            lightProbe.InfluenceOffset = influenceOffset;

        if (lightProbe.InfluenceShape == LightProbeComponent.EInfluenceShape.Sphere)
        {
            float inner = lightProbe.InfluenceSphereInnerRadius;
            float outer = lightProbe.InfluenceSphereOuterRadius;

            if (ImGui.DragFloat("Inner Radius", ref inner, 0.01f, 0.0f, outer, "%.3f"))
                lightProbe.InfluenceSphereInnerRadius = inner;
            if (ImGui.DragFloat("Outer Radius", ref outer, 0.01f, 0.001f, float.MaxValue, "%.3f"))
                lightProbe.InfluenceSphereOuterRadius = outer;
        }
        else
        {
            Vector3 inner = lightProbe.InfluenceBoxInnerExtents;
            Vector3 outer = lightProbe.InfluenceBoxOuterExtents;

            if (ImGui.DragFloat3("Inner Extents", ref inner, 0.01f, 0.0f))
                lightProbe.InfluenceBoxInnerExtents = inner;

            if (ImGui.DragFloat3("Outer Extents", ref outer, 0.01f, 0.001f))
                lightProbe.InfluenceBoxOuterExtents = outer;
        }
    }

    private static void DrawHdrAndStreaming(LightProbeComponent lightProbe)
    {
        if (!ImGui.CollapsingHeader("HDR & Streaming", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        int hdrEncoding = (int)lightProbe.HdrEncoding;
        if (ImGui.Combo("HDR Encoding", ref hdrEncoding, "Rgb16f\0RGBM\0RGBE\0YCoCg\0"))
            lightProbe.HdrEncoding = (LightProbeComponent.EHdrEncoding)hdrEncoding;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("HDR encoding for baked probes. Rgb16f is highest quality, others reduce memory.");

        bool normalized = lightProbe.NormalizedCubemap;
        if (ImGui.Checkbox("Normalized Cubemap", ref normalized))
            lightProbe.NormalizedCubemap = normalized;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Normalize intensity during bake; allows probe reuse at different brightness levels.");

        if (normalized)
        {
            ImGui.Indent();
            float normScale = lightProbe.NormalizationScale;
            if (ImGui.DragFloat("Normalization Scale", ref normScale, 0.01f, 0.0001f, 100.0f, "%.4f"))
                lightProbe.NormalizationScale = normScale;
            ImGui.Unindent();
        }

        ImGui.Separator();
        bool streaming = lightProbe.StreamHighMipsOnDemand;
        if (ImGui.Checkbox("Stream High Mips on Demand", ref streaming))
            lightProbe.StreamHighMipsOnDemand = streaming;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Load only low-res mips initially; stream high-res when probe becomes visible/dominant.");

        if (streaming)
        {
            ImGui.Indent();
            ImGui.TextDisabled($"Streamed Mip Level: {lightProbe.StreamedMipLevel}");
            ImGui.TextDisabled($"Target Mip Level: {lightProbe.TargetMipLevel}");
            ImGui.Unindent();
        }
    }

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
