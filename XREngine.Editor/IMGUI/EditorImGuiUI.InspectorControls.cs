using System;
using ImGuiNET;
using XREngine.Data.Core;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static class EnumCache<TEnum> where TEnum : struct, Enum
    {
        public static readonly string[] Names = Enum.GetNames<TEnum>();
        public static readonly TEnum[] Values = Enum.GetValues<TEnum>();
    }

    internal static bool DrawCheckbox(string label, bool value, Action<bool> setValue, string undoLabel, XRBase target)
    {
        bool edited = ImGui.Checkbox(label, ref value);
        ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
        if (!edited)
            return false;

        setValue(value);
        EnqueueSceneEdit(() => setValue(value));
        return true;
    }

    internal static bool DrawFloat(string label, float value, float speed, Action<float> setValue, string undoLabel, XRBase target)
    {
        bool edited = ImGui.DragFloat(label, ref value, speed);
        ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
        if (!edited)
            return false;

        setValue(value);
        EnqueueSceneEdit(() => setValue(value));
        return true;
    }

    internal static bool DrawInt(string label, int value, float speed, Action<int> setValue, string undoLabel, XRBase target)
    {
        bool edited = ImGui.DragInt(label, ref value, speed);
        ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
        if (!edited)
            return false;

        setValue(value);
        EnqueueSceneEdit(() => setValue(value));
        return true;
    }

    internal static bool DrawEnumCombo<TEnum>(string label, TEnum value, Action<TEnum> setValue, string undoLabel, XRBase target)
        where TEnum : struct, Enum
    {
        TEnum[] values = EnumCache<TEnum>.Values;
        int index = Array.IndexOf(values, value);
        if (index < 0)
            index = 0;

        bool edited = ImGui.Combo(label, ref index, EnumCache<TEnum>.Names, EnumCache<TEnum>.Names.Length);
        ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
        if (!edited || index < 0 || index >= values.Length)
            return false;

        TEnum next = values[index];
        setValue(next);
        EnqueueSceneEdit(() => setValue(next));
        return true;
    }

    internal static void DrawOptionalFloat(string label, float? value, Action<float?> setValue, string undoLabel, XRBase target, float speed = 0.25f)
    {
        ImGui.PushID(label);

        bool enabled = value.HasValue;
        if (ImGui.Checkbox("##Enabled", ref enabled))
        {
            ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
            float? next = enabled ? 0f : null;
            setValue(next);
            EnqueueSceneEdit(() => setValue(next));
        }
        else
        {
            ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        float current = value ?? 0f;
        using (new ImGuiDisabledScope(!enabled))
        {
            ImGui.SetNextItemWidth(-1f);
            bool edited = ImGui.DragFloat("##Value", ref current, speed);
            ImGuiUndoHelper.TrackDragUndo(undoLabel, target);
            if (edited)
            {
                float? next = enabled ? current : null;
                setValue(next);
                EnqueueSceneEdit(() => setValue(next));
            }
        }

        ImGui.PopID();
    }

    internal static void DrawReadOnlyField(string label, string value)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        ImGui.TextDisabled(value);
    }
}