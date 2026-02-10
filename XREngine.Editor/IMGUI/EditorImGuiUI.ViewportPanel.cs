using ImGuiNET;
using Silk.NET.Maths;
using System;
using System.Numerics;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Picking;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _scenePanelInteracting;

    private static BoundingRectangle? _scenePanelRenderRegion;

    private static void DrawScenePanel()
    {
        if (Engine.EditorPreferences.ViewportPresentationMode != EditorPreferences.EViewportPresentationMode.UseViewportPanel)
        {
            _scenePanelInteracting = false;
            _scenePanelRenderRegion = null;
            return;
        }

        EnsureScenePanelRenderRegionProvider();

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse;

        if (!ImGui.Begin("Scene", flags))
        {
            _scenePanelInteracting = false;
            _scenePanelRenderRegion = null;
            ImGui.End();
            return;
        }

        // Important: do not treat "focused" alone as scene interaction.
        // Focus can linger on the Scene window for one frame while clicking another panel,
        // which causes click-through into world picking.
        _scenePanelInteracting =
            ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        UpdateScenePanelRenderRegion();

        // Display the viewport FBO texture as an ImGui image
        DisplayScenePanelImage();

        ImGui.End();
    }

    /// <summary>
    /// Displays the viewport panel FBO texture as an ImGui image.
    /// This allows the scene render to respect ImGui's Z ordering.
    /// </summary>
    private static void DisplayScenePanelImage()
    {
        XRWindow? window = Engine.Windows.Count > 0 ? Engine.Windows[0] : null;
        if (window is null)
            return;

        var texture = window.ScenePanelTexture;
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

        // Handle asset drop on the scene image - must be right after ImGui.Image()
        XRWorldInstance? world = TryGetActiveWorldInstance();
        if (world is not null)
            HandleScenePanelModelAssetDrop(world);
    }

    private static void EnsureScenePanelRenderRegionProvider()
    {
        if (Engine.Rendering.ScenePanelRenderRegionProvider is not null)
            return;

        Engine.Rendering.ScenePanelRenderRegionProvider = window =>
        {
            if (!Engine.IsEditor)
                return null;

            if (Engine.EditorPreferences.ViewportPresentationMode != EditorPreferences.EViewportPresentationMode.UseViewportPanel)
                return null;

            if (_scenePanelHookedWindow is not null && !ReferenceEquals(window, _scenePanelHookedWindow))
                return null;

            return _scenePanelRenderRegion;
        };
    }

    private static void UpdateScenePanelRenderRegion()
    {
        XRWindow? window = Engine.Windows.Count > 0 ? Engine.Windows[0] : null;
        if (window is null)
        {
            _scenePanelRenderRegion = null;
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
            _scenePanelRenderRegion = null;
            return;
        }

        var hostWindow = window.Window;
        Vector2D<int> fb = hostWindow.FramebufferSize;

        if (fb.X <= 0 || fb.Y <= 0)
        {
            _scenePanelRenderRegion = null;
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
            _scenePanelRenderRegion = null;
            return;
        }

        _scenePanelRenderRegion = new BoundingRectangle(x, y, w, h);
    }

    private static void HandleScenePanelModelAssetDrop(XRWorldInstance world)
    {
        if (!ImGui.BeginDragDropTarget())
            return;

        var payload = ImGui.AcceptDragDropPayload(ImGuiAssetUtilities.AssetPayloadType);
        unsafe
        {
            if ((nint)payload.NativePtr != IntPtr.Zero && payload.Data != IntPtr.Zero && payload.DataSize > 0)
            {
                string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (TryLoadMaterialAsset(path, out var material))
                        EnqueueSceneEdit(() => TryApplyMaterialDropToHoveredSubmesh(world, material!));
                    else if (TryLoadPrefabAsset(path, out var prefab))
                        EnqueueSceneEdit(() => SpawnPrefabNode(world, parent: null, prefab!));
                    else if (TryLoadModelAsset(path, out var model))
                        EnqueueSceneEdit(() => SpawnModelNode(world, parent: null, model!, path));
                }
            }
        }

        ImGui.EndDragDropTarget();
    }

    private static bool TryApplyMaterialDropToHoveredSubmesh(XRWorldInstance world, XRMaterial material)
    {
        _ = world;

        var player = Engine.State.MainPlayer ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
        if (player?.ControlledPawn is not EditorFlyingCameraPawnComponent pawn)
        {
            Debug.LogWarning("No editor camera pawn available to apply dropped material.");
            return false;
        }

        if (!pawn.TryGetLastMeshHit(out MeshPickResult meshHit))
        {
            Debug.LogWarning("No mesh under the cursor to apply the dropped material.");
            return false;
        }

        if (meshHit.Component is ModelComponent modelComponent)
        {
            if (modelComponent.TryGetSourceSubMesh(meshHit.Mesh, out var subMesh))
            {
                foreach (var lod in subMesh.LODs)
                    lod.Material = material;
                return true;
            }

            Debug.LogWarning("Unable to resolve submesh for the hovered model.");
            return false;
        }

        var renderer = meshHit.Mesh.CurrentLODRenderer ?? meshHit.Mesh.LODs.First?.Value.Renderer;
        if (renderer is not null)
        {
            renderer.Material = material;
            return true;
        }

        Debug.LogWarning("Hovered mesh has no renderer material to replace.");
        return false;
    }
}
