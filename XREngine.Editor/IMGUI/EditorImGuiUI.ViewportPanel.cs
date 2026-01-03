using ImGuiNET;
using Silk.NET.Maths;
using System;
using System.Numerics;
using XREngine;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _viewportPanelInteracting;

    private static BoundingRectangle? _viewportPanelRenderRegion;

    private static void DrawViewportPanel()
    {
        if (Engine.Rendering.Settings.ViewportPresentationMode != Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel)
        {
            _viewportPanelInteracting = false;
            _viewportPanelRenderRegion = null;
            return;
        }

        EnsureViewportPanelRenderRegionProvider();

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse;

        if (!ImGui.Begin("Viewport", flags))
        {
            _viewportPanelInteracting = false;
            _viewportPanelRenderRegion = null;
            ImGui.End();
            return;
        }

        _viewportPanelInteracting =
            ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) ||
            ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        UpdateViewportPanelRenderRegion();

        // Display the viewport FBO texture as an ImGui image
        DisplayViewportPanelImage();

        ImGui.End();
    }

    /// <summary>
    /// Displays the viewport panel FBO texture as an ImGui image.
    /// This allows the scene render to respect ImGui's Z ordering.
    /// </summary>
    private static void DisplayViewportPanelImage()
    {
        XRWindow? window = Engine.Windows.Count > 0 ? Engine.Windows[0] : null;
        if (window is null)
            return;

        var texture = window.ViewportPanelTexture;
        if (texture is null)
            return;

        // Get the OpenGL texture handle
        if (AbstractRenderer.Current is not OpenGLRenderer renderer)
            return;

        var glTexture = renderer.GenericToAPI<GLTexture2D>(texture);
        if (glTexture is null || glTexture.BindingId == 0 || glTexture.BindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
            return;

        nint handle = (nint)glTexture.BindingId;

        // Get the content region size for the image
        Vector2 contentSize = ImGui.GetContentRegionAvail();
        if (contentSize.X <= 1 || contentSize.Y <= 1)
            return;

        // Display the image with flipped UVs (OpenGL textures are bottom-up, ImGui expects top-down)
        Vector2 uv0 = new(0.0f, 1.0f); // bottom-left
        Vector2 uv1 = new(1.0f, 0.0f); // top-right
        ImGui.Image(handle, contentSize, uv0, uv1);

        // Handle asset drop on the viewport image - must be right after ImGui.Image()
        XRWorldInstance? world = TryGetActiveWorldInstance();
        if (world is not null)
            HandleViewportModelAssetDrop(world);
    }

    private static void EnsureViewportPanelRenderRegionProvider()
    {
        if (Engine.Rendering.ViewportPanelRenderRegionProvider is not null)
            return;

        Engine.Rendering.ViewportPanelRenderRegionProvider = window =>
        {
            if (!Engine.IsEditor)
                return null;

            if (Engine.Rendering.Settings.ViewportPresentationMode != Engine.Rendering.EngineSettings.EViewportPresentationMode.UseViewportPanel)
                return null;

            if (_viewportPanelHookedWindow is not null && !ReferenceEquals(window, _viewportPanelHookedWindow))
                return null;

            return _viewportPanelRenderRegion;
        };
    }

    private static void UpdateViewportPanelRenderRegion()
    {
        XRWindow? window = Engine.Windows.Count > 0 ? Engine.Windows[0] : null;
        if (window is null)
        {
            _viewportPanelRenderRegion = null;
            return;
        }

        // Use cursor position + available content size to avoid double-counting window padding
        // (notably for docked windows). Convert using ImGui's configured display scale.
        var io = ImGui.GetIO();
        // Older ImGui.NET versions don't expose IO.DisplayPos; use the main viewport position instead.
        var mainViewport = ImGui.GetMainViewport();
        Vector2 contentPos = ImGui.GetCursorScreenPos() - mainViewport.Pos;
        Vector2 contentSize = ImGui.GetContentRegionAvail();

        if (contentSize.X <= 1 || contentSize.Y <= 1)
        {
            _viewportPanelRenderRegion = null;
            return;
        }

        var hostWindow = window.Window;
        Vector2D<int> fb = hostWindow.FramebufferSize;

        if (fb.X <= 0 || fb.Y <= 0)
        {
            _viewportPanelRenderRegion = null;
            return;
        }

        float scaleX = MathF.Max(io.DisplayFramebufferScale.X, 1e-6f);
        float scaleY = MathF.Max(io.DisplayFramebufferScale.Y, 1e-6f);

        int x = (int)(contentPos.X * scaleX);
        int w = (int)(contentSize.X * scaleX);

        int yTop = (int)(contentPos.Y * scaleY);
        int h = (int)(contentSize.Y * scaleY);

        // Convert from top-left origin to bottom-left origin in framebuffer space.
        int y = fb.Y - (yTop + h);

        // Clamp to framebuffer.
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > fb.X) w = fb.X - x;
        if (y + h > fb.Y) h = fb.Y - y;

        if (w <= 0 || h <= 0)
        {
            _viewportPanelRenderRegion = null;
            return;
        }

        _viewportPanelRenderRegion = new BoundingRectangle(x, y, w, h);
    }

    private static void HandleViewportModelAssetDrop(XRWorldInstance world)
    {
        if (!ImGui.BeginDragDropTarget())
            return;

        var payload = ImGui.AcceptDragDropPayload(ImGuiAssetUtilities.AssetPayloadType);
        if (payload.Data != IntPtr.Zero && payload.DataSize > 0)
        {
            string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (TryLoadPrefabAsset(path, out var prefab))
                    EnqueueSceneEdit(() => SpawnPrefabNode(world, parent: null, prefab!));
                else if (TryLoadModelAsset(path, out var model))
                    EnqueueSceneEdit(() => SpawnModelNode(world, parent: null, model!, path));
            }
        }

        ImGui.EndDragDropTarget();
    }
}
