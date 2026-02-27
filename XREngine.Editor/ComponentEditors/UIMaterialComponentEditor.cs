using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.UI;

namespace XREngine.Editor.ComponentEditors;

public sealed class UIMaterialComponentEditor : IXRComponentEditor
{
    private const float PreviewMaxEdge = 192.0f;
    private const float PreviewFallbackEdge = 64.0f;

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not UIMaterialComponent uiMat)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(uiMat, visited, "UI Material"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawMaterialSection(uiMat);
        DrawPreviewSection(uiMat);
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawMaterialSection(UIMaterialComponent uiMat)
    {
        if (!ImGui.CollapsingHeader("Component", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg;
        if (ImGui.BeginTable("UIMaterialComponentProps", 2, tableFlags))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Material");
            ImGui.TableSetColumnIndex(1);
            ImGuiAssetUtilities.DrawAssetField("UIMaterialComponent.Material", uiMat.Material, asset =>
            {
                if (!ReferenceEquals(uiMat.Material, asset))
                {
                    using var _ = Undo.TrackChange("Set UI Material", uiMat);
                    uiMat.Material = asset;
                }
            });

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Flip Vertical UV");
            ImGui.TableSetColumnIndex(1);
            bool flip = uiMat.FlipVerticalUVCoord;
            if (ImGui.Checkbox("##FlipVerticalUV", ref flip))
                uiMat.FlipVerticalUVCoord = flip;

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted("Clip To Bounds");
            ImGui.TableSetColumnIndex(1);
            bool clip = uiMat.ClipToBounds;
            if (ImGui.Checkbox("##ClipToBounds", ref clip))
                uiMat.ClipToBounds = clip;

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    private static void DrawPreviewSection(UIMaterialComponent uiMat)
    {
        if (!ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        XRTexture? texture = TrySelectPreviewTexture(uiMat.Material);
        if (texture is null)
        {
            ImGui.TextDisabled("No material texture available to preview.");
            return;
        }

        if (!TryGetTexturePreviewData(texture, out nint handle, out Vector2 displaySize, out Vector2 pixelSize, out string? failureReason))
        {
            ImGui.TextDisabled(failureReason ?? "Preview unavailable.");
            return;
        }

        // If the UI quad flips UV vertically, invert the ImGui flip so the preview matches what the quad outputs.
        bool flipVertically = !uiMat.FlipVerticalUVCoord;
        Vector2 uv0 = flipVertically ? new Vector2(0.0f, 1.0f) : Vector2.Zero;
        Vector2 uv1 = flipVertically ? new Vector2(1.0f, 0.0f) : Vector2.One;

        bool openLarge = false;
        ImGui.Image(handle, displaySize, uv0, uv1);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{(int)pixelSize.X} x {(int)pixelSize.Y}");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                openLarge = true;
        }

        if (ImGui.SmallButton("View Larger"))
            openLarge = true;

        if (openLarge)
            ComponentEditorLayout.RequestPreviewDialog("UI Material Output", handle, pixelSize, flipVertically);

        ImGui.TextDisabled($"Texture: {texture.GetType().Name} | {(int)pixelSize.X} x {(int)pixelSize.Y}");
    }

    private static XRTexture? TrySelectPreviewTexture(XRMaterial? material)
    {
        var textures = material?.Textures;
        if (textures is null || textures.Count == 0)
            return null;

        // Prefer a named UI/main texture if present.
        foreach (var tex in textures)
        {
            if (tex is null)
                continue;
            if (!string.IsNullOrWhiteSpace(tex.SamplerName) &&
                (tex.SamplerName.Equals("MainTex", StringComparison.OrdinalIgnoreCase) ||
                 tex.SamplerName.Equals("MainTexture", StringComparison.OrdinalIgnoreCase) ||
                 tex.SamplerName.Equals("Albedo", StringComparison.OrdinalIgnoreCase)))
                return tex;
        }

        // Otherwise, first non-null texture.
        foreach (var tex in textures)
            if (tex is not null)
                return tex;

        return null;
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
}
