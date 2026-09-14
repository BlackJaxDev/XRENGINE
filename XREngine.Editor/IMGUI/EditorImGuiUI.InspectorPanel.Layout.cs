using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly ConditionalWeakTable<SceneNode, InspectorBreadcrumbState> BreadcrumbStates = new();

    private sealed class InspectorBreadcrumbState
    {
        public SceneNode? Parent;
        public string? Name;
        public string? ParentName;
        public string Text = string.Empty;
    }

    private static string GetInspectorBreadcrumb(SceneNode node)
    {
        var state = BreadcrumbStates.GetValue(node, static _ => new InspectorBreadcrumbState());
        SceneNode? parent = node.Parent;
        string? name = node.Name;
        string? parentName = parent?.Name;
        if (!ReferenceEquals(state.Parent, parent)
            || !string.Equals(state.Name, name, StringComparison.Ordinal)
            || !string.Equals(state.ParentName, parentName, StringComparison.Ordinal))
        {
            state.Parent = parent;
            state.Name = name;
            state.ParentName = parentName;
            state.Text = string.IsNullOrWhiteSpace(parentName) ? "Root" : $"{parentName} / {name}";
        }
        return state.Text;
    }

    private static readonly ConditionalWeakTable<XRComponent, InspectorHeaderSearchState> ComponentHeaderSearchStates = new();

    private sealed class InspectorHeaderSearchState
    {
        public bool LastOpen;
        public bool SearchWasActive;
        public bool RestoreOpen;
    }

    private static void PrepareInspectorComponentSearchExpansion(XRComponent component, bool searching)
    {
        var state = ComponentHeaderSearchStates.GetValue(component, static _ => new InspectorHeaderSearchState());
        if (searching)
        {
            if (!state.SearchWasActive)
            {
                state.RestoreOpen = state.LastOpen;
                state.SearchWasActive = true;
            }
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            return;
        }
        if (!state.SearchWasActive)
            return;
        ImGui.SetNextItemOpen(state.RestoreOpen, ImGuiCond.Always);
        state.SearchWasActive = false;
    }

    private static void RecordInspectorComponentHeaderState(XRComponent component, bool open, bool searching)
    {
        if (!searching)
            ComponentHeaderSearchStates.GetValue(component, static _ => new InspectorHeaderSearchState()).LastOpen = open;
    }
    internal static bool DrawInspectorMixedCheckbox(string id, ref bool value, bool mixed)
    {
        Vector2 checkboxPosition = ImGui.GetCursorScreenPos();
        bool checkboxValue = mixed ? false : value;
        bool changed = ImGui.Checkbox(id, ref checkboxValue);
        if (changed)
            value = checkboxValue;
        if (!mixed)
            return changed;

        float frameHeight = ImGui.GetFrameHeight();
        float inset = frameHeight * 0.30f;
        float centerY = checkboxPosition.Y + frameHeight * 0.50f;
        uint color = ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(checkboxPosition.X + inset, centerY),
            new Vector2(checkboxPosition.X + frameHeight - inset, centerY),
            color,
            1.5f);
        return changed;
    }
}