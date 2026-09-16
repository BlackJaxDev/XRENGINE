using ImGuiNET;
using System.Numerics;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors.PostProcessDrawers;

/// <summary>
/// Custom parameter drawer for Color Grading, providing normalized luminance weight presets (Default, Rec.709, Rec.601).
/// </summary>
public sealed class ColorGradingStageDrawer : IPostProcessStageCustomDrawer
{
    public bool TryDrawParameter(PostProcessParameterCustomDrawerContext context)
    {
        if (context.Parameter.Name != nameof(ColorGradingSettings.AutoExposureLuminanceWeights))
            return false;

        Vector3 fallback = context.Parameter.DefaultValue is Vector3 v3 ? v3 : Vector3.Zero;
        Vector3 value = context.StageState.GetValue(context.Parameter.Name, fallback);

        bool changed = ImGui.DragFloat3(context.Parameter.DisplayName, ref value, context.Parameter.Step ?? 0.001f);
        if (changed)
        {
            value = NormalizeLuminanceWeights(value, RuntimeEngine.Rendering.Settings.DefaultLuminance);
            context.StageState.SetValue(context.Parameter.Name, value);
        }
        ImGuiUndoHelper.TrackDragUndo(context.Parameter.DisplayName, context.UndoTarget);

        ImGui.SameLine();
        if (ImGui.SmallButton("Default"))
        {
            using var _ = Undo.TrackChange("Luminance Weights Default", context.UndoTarget);
            value = NormalizeLuminanceWeights(RuntimeEngine.Rendering.Settings.DefaultLuminance, RuntimeEngine.Rendering.Settings.DefaultLuminance);
            context.StageState.SetValue(context.Parameter.Name, value);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rec.709"))
        {
            using var _ = Undo.TrackChange("Luminance Weights Rec.709", context.UndoTarget);
            value = NormalizeLuminanceWeights(new Vector3(0.2126f, 0.7152f, 0.0722f), RuntimeEngine.Rendering.Settings.DefaultLuminance);
            context.StageState.SetValue(context.Parameter.Name, value);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rec.601"))
        {
            using var _ = Undo.TrackChange("Luminance Weights Rec.601", context.UndoTarget);
            value = NormalizeLuminanceWeights(new Vector3(0.299f, 0.587f, 0.114f), RuntimeEngine.Rendering.Settings.DefaultLuminance);
            context.StageState.SetValue(context.Parameter.Name, value);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Equal"))
        {
            using var _ = Undo.TrackChange("Luminance Weights Equal", context.UndoTarget);
            value = NormalizeLuminanceWeights(new Vector3(1.0f, 1.0f, 1.0f), RuntimeEngine.Rendering.Settings.DefaultLuminance);
            context.StageState.SetValue(context.Parameter.Name, value);
        }

        float sum = value.X + value.Y + value.Z;
        ImGui.TextDisabled($"Normalized (sum={sum:0.###})");
        return true;
    }

    private static Vector3 NormalizeLuminanceWeights(Vector3 w, Vector3 fallback)
    {
        static float Sanitize(float v) => float.IsFinite(v) ? MathF.Max(0.0f, v) : 0.0f;

        w = new Vector3(Sanitize(w.X), Sanitize(w.Y), Sanitize(w.Z));
        float sum = w.X + w.Y + w.Z;
        if (!(sum > 0.0f) || float.IsNaN(sum) || float.IsInfinity(sum))
            return NormalizeLuminanceWeights(fallback, fallback);
        return w / sum;
    }
}
