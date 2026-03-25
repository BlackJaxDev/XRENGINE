using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Data.Colors;

namespace XREngine.Editor.ComponentEditors;

public sealed class CustomUIComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not CustomUIComponent customUi)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(customUi, visited, "Custom UI"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(customUi.GetHashCode());

        if (!ImGui.CollapsingHeader("Controls", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.PopID();
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (customUi.Fields.Count == 0)
        {
            ImGui.TextDisabled("No programmable controls were registered on this component.");
        }
        else
        {
            foreach (CustomUIField field in customUi.Fields)
                DrawField(field);
        }

        if (ImGui.CollapsingHeader("Advanced"))
            EditorImGuiUI.DrawDefaultComponentInspector(customUi, visited);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawField(CustomUIField field)
    {
        switch (field)
        {
            case CustomUIFloatField floatField:
                DrawFloatField(floatField);
                break;
            case CustomUIBoolField boolField:
                DrawBoolField(boolField);
                break;
            case CustomUIVector3Field vector3Field:
                DrawVector3Field(vector3Field);
                break;
            case CustomUIColorField colorField:
                DrawColorField(colorField);
                break;
            case CustomUITextField textField:
                DrawTextField(textField);
                break;
            case CustomUIButtonField buttonField:
                DrawButtonField(buttonField);
                break;
            default:
                ImGui.TextDisabled($"Unsupported control type: {field.GetType().Name}");
                break;
        }
    }

    private static void DrawFloatField(CustomUIFloatField field)
    {
        float value = field.GetValue();
        if (ImGui.DragFloat(field.Label, ref value, field.Step, field.Min, field.Max, field.Format))
            field.SetValue(Math.Clamp(value, field.Min, field.Max));

        DrawTooltip(field);
    }

    private static void DrawBoolField(CustomUIBoolField field)
    {
        bool value = field.GetValue();
        if (ImGui.Checkbox(field.Label, ref value))
            field.SetValue(value);

        DrawTooltip(field);
    }

    private static void DrawVector3Field(CustomUIVector3Field field)
    {
        var value = field.GetValue();
        if (ImGui.DragFloat3(field.Label, ref value, field.Step, 0.0f, 0.0f, field.Format))
            field.SetValue(value);

        DrawTooltip(field);
    }

    private static void DrawColorField(CustomUIColorField field)
    {
        Vector4 value = field.GetValue();
        bool changed;
        if (field.Alpha)
        {
            changed = ImGui.ColorEdit4(field.Label, ref value);
        }
        else
        {
            Vector3 rgb = new(value.X, value.Y, value.Z);
            changed = ImGui.ColorEdit3(field.Label, ref rgb);
            if (changed)
                value = new Vector4(rgb, value.W);
        }

        if (changed)
            field.SetValue((ColorF4)value);

        DrawTooltip(field);
    }

    private static void DrawTextField(CustomUITextField field)
    {
        ImGui.TextUnformatted($"{field.Label}: {field.GetValue()}");
        DrawTooltip(field);
    }

    private static void DrawButtonField(CustomUIButtonField field)
    {
        if (ImGui.Button(field.Label))
            field.Invoke();

        DrawTooltip(field);
    }

    private static void DrawTooltip(CustomUIField field)
    {
        if (!string.IsNullOrWhiteSpace(field.HelpText) && ImGui.IsItemHovered())
            ImGui.SetTooltip(field.HelpText);
    }
}