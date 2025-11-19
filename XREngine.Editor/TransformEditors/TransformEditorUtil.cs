using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.TransformEditors;

internal static class TransformEditorUtil
{
    public static string GetTransformDisplayName(TransformBase target)
    {
        string? name = target.SceneNode?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = target.Name;
        return string.IsNullOrWhiteSpace(name) ? target.GetType().Name : name;
    }

    public static void DrawReadOnlyVector3(string label, Vector3 value, string format = "F3")
    {
        var inv = CultureInfo.InvariantCulture;
        ImGui.TextDisabled($"{label}: {value.X.ToString(format, inv)}, {value.Y.ToString(format, inv)}, {value.Z.ToString(format, inv)}");
    }
}
