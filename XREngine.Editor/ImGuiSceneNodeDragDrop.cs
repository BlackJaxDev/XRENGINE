using System;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

internal static unsafe class ImGuiSceneNodeDragDrop
{
    public const string PayloadType = "XR_SCENE_NODE";

    public static void SetPayload(SceneNode node)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));

        string idText = node.ID.ToString("N") + '\0';
        byte[] bytes = Encoding.UTF8.GetBytes(idText);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            ImGui.SetDragDropPayload(PayloadType, handle.AddrOfPinnedObject(), (uint)bytes.Length);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    public static SceneNode? Accept(bool peekOnly = false)
    {
        var payload = ImGui.AcceptDragDropPayload(PayloadType, peekOnly ? ImGuiDragDropFlags.AcceptPeekOnly : ImGuiDragDropFlags.None);
        if (payload.NativePtr == null)
            return null;

        return ExtractSceneNode(payload);
    }

    private static SceneNode? ExtractSceneNode(ImGuiPayloadPtr payload)
    {
        if (payload.NativePtr == null || payload.Data == IntPtr.Zero || payload.DataSize == 0)
            return null;

        try
        {
            string? guidText = Marshal.PtrToStringUTF8(payload.Data, (int)payload.DataSize);
            if (string.IsNullOrWhiteSpace(guidText))
                return null;

            guidText = guidText.TrimEnd('\0');
            if (!Guid.TryParse(guidText, out Guid id))
                return null;

            return XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) ? obj as SceneNode : null;
        }
        catch
        {
            return null;
        }
    }
}
