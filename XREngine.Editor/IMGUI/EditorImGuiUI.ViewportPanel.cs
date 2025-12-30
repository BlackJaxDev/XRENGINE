using ImGuiNET;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static void DrawViewportPanel()
    {
        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin("Viewport", flags))
        {
            ImGui.End();
            return;
        }

        var world = TryGetActiveWorldInstance();
        if (world is not null)
            HandleViewportModelAssetDrop(world);

        // Do not draw ImGui content here.
        // The underlying engine render should remain visible.
        ImGui.End();
    }

    private static void HandleViewportModelAssetDrop(XRWorldInstance world)
    {
        if (!ImGui.BeginDragDropTarget())
            return;

        var payload = ImGui.AcceptDragDropPayload(ImGuiAssetUtilities.AssetPayloadType);
        if (payload.Data != IntPtr.Zero && payload.DataSize > 0)
        {
            string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
            if (!string.IsNullOrWhiteSpace(path) && TryLoadModelAsset(path, out var model))
            {
                EnqueueSceneEdit(() => SpawnModelNode(world, parent: null, model!, path));
            }
        }

        ImGui.EndDragDropTarget();
    }
}
