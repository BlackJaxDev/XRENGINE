using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Editor;
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
        DrawCapturePreview(lightProbe, "Environment (Octa)", lightProbe.EnvironmentTextureOctahedral, LightProbeComponent.ERenderPreview.Environment);
        DrawCapturePreview(lightProbe, "Irradiance", lightProbe.IrradianceTexture, LightProbeComponent.ERenderPreview.Irradiance);
        DrawCapturePreview(lightProbe, "Prefilter", lightProbe.PrefilterTexture, LightProbeComponent.ERenderPreview.Prefilter);
    }

    private static void DrawCapturePreview(
        LightProbeComponent lightProbe,
        string label,
        XRTexture2D? texture,
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

        string info = $"{(int)pixelSize.X} x {(int)pixelSize.Y} | {texture.SizedInternalFormat}";
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(info);
        ImGui.TextDisabled(info);

        ImGui.PopID();
    }

    private static bool TryGetTexturePreviewData(
        XRTexture2D texture,
        out nint handle,
        out Vector2 displaySize,
        out Vector2 pixelSize,
        out string? failureReason)
    {
        float width = MathF.Max(1.0f, texture.Width);
        float height = MathF.Max(1.0f, texture.Height);
        pixelSize = new Vector2(width, height);
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

        var apiTexture = renderer.GenericToAPI<GLTexture2D>(texture);
        if (apiTexture is null)
        {
            failureReason = "Texture not uploaded to GPU.";
            return false;
        }

        uint bindingId = apiTexture.BindingId;
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
}
