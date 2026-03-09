using System;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Editor;

internal static unsafe class ImGuiComponentDragDrop
{
    public const string PayloadType = "XR_COMPONENT";

    public static void SetPayload(XRComponent component)
    {
        if (component is null)
            throw new ArgumentNullException(nameof(component));

        string idText = component.ID.ToString("N") + '\0';
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

    public static XRComponent? Accept(bool peekOnly = false)
    {
        var payload = ImGui.AcceptDragDropPayload(PayloadType, peekOnly ? ImGuiDragDropFlags.AcceptPeekOnly : ImGuiDragDropFlags.None);
        if (payload.NativePtr == null)
            return null;

        return ExtractComponent(payload);
    }

    private static XRComponent? ExtractComponent(ImGuiPayloadPtr payload)
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

            return XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) ? obj as XRComponent : null;
        }
        catch
        {
            return null;
        }
    }
}