using ImGuiNET;
using System.Collections;
using System.Globalization;
using XREngine.Data.Core;
using XREngine.Editor.UI;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>Primitive lists use one uniform table row per item, allowing true visible-item evaluation.</summary>
    private static unsafe void DrawClippedInspectorCollection(ImGuiEditorUtilities.CollectionEditorAdapter adapter, Type elementType, object? owner)
    {
        if (!ImGui.BeginTable("Elements", 3, ImGuiTableFlags.SizingStretchProp))
            return;
        int removeIndex = -1;
        try
        {
            ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 3f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            bool drawAll = ImGui.IsAnyItemActive()
                || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
            if (drawAll)
            {
                for (int i = 0; i < adapter.Count; i++)
                    if (DrawInspectorCollectionRow(adapter, elementType, owner, i))
                        removeIndex = i;
            }
            else
            {
                var clipper = new ImGuiListClipper();
                ImGuiNative.ImGuiListClipper_Begin(&clipper, adapter.Count, -1f);
                try
                {
                    while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                        for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                            if (DrawInspectorCollectionRow(adapter, elementType, owner, i))
                                removeIndex = i;
                }
                finally
                {
                    ImGuiNative.ImGuiListClipper_End(&clipper);
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        // Apply structural edits after clipping finishes so its item count remains consistent.
        if (removeIndex < 0 || removeIndex >= adapter.Count)
            return;
        object? removed = adapter.Items[removeIndex];
        if (!adapter.TryRemoveAt(removeIndex))
            return;
        int index = removeIndex;
        Undo.RecordStructuralChange("Remove Collection Element",
            undoAction: () => { adapter.TryInsert(index, removed); NotifyInspectorValueEdited(owner); },
            redoAction: () => { adapter.TryRemoveAt(index); NotifyInspectorValueEdited(owner); });
        NotifyInspectorValueEdited(owner);
    }

    private static bool DrawInspectorCollectionRow(ImGuiEditorUtilities.CollectionEditorAdapter adapter, Type elementType, object? owner, int index)
    {
        BeginInspectorPropertyRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(index.ToString(CultureInfo.InvariantCulture));
        ImGui.TableSetColumnIndex(1);
        ImGui.PushID(index);
        try
        {
            IList list = adapter.Items;
            object? value;
            try
            {
                value = list[index];
            }
            catch
            {
                ImGui.TextDisabled("<error>");
                return false;
            }
            DrawCollectionSimpleElement(list, elementType, index, ref value, list is Array || !list.IsReadOnly, owner);
            ImGui.TableSetColumnIndex(2);
            using var disabled = new ImGuiDisabledScope(!adapter.CanAddRemove);
            bool remove = ImGui.SmallButton("-");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove element");
            return remove && adapter.CanAddRemove;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static bool IsUniformInspectorCollectionType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(string)
            || type == typeof(System.Numerics.Vector2) || type == typeof(System.Numerics.Vector3)
            || type == typeof(System.Numerics.Vector4);
    }
}
