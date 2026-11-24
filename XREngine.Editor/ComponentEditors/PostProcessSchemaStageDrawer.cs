using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.ComponentEditors.PostProcessDrawers;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Draws post-process schema stages and their parameters. The camera editor and the Render Pipeline Workbench share
/// this drawer, so a stage looks the same in both places.
/// </summary>
public static class PostProcessSchemaStageDrawer
{
    /// <summary>
    /// Draws the parameters of <paramref name="stage"/> that belong to <paramref name="section"/>, and the custom
    /// drawer of the stage when a camera is given. Edits write to <paramref name="stageState"/> and record undo against
    /// <paramref name="undoTarget"/> or the backing instance of the state.
    /// </summary>
    public static void DrawStage(
        PostProcessStageDescriptor stage,
        PostProcessStageState stageState,
        XRBase? undoTarget,
        CameraComponent? component,
        IPostProcessStageCustomDrawer? customDrawer,
        bool drawStageHeader = true,
        XRCamera? camera = null,
        PipelineEditorSection section = PipelineEditorSection.PostProcessing)
    {
        if (stage.Parameters.Count == 0 && customDrawer is null)
            return;

        ImGui.PushID(stage.Key);

        if (drawStageHeader)
            ImGui.SeparatorText(stage.DisplayName);

        undoTarget = (stageState.BackingInstance as XRBase) ?? undoTarget;

        PostProcessStageCustomDrawerContext? stageContext = null;
        if (stage.EditorSection == section && customDrawer is not null && camera is not null)
        {
            stageContext = new PostProcessStageCustomDrawerContext(camera, component, stage, stageState, undoTarget);
            customDrawer.DrawStageHeader(stageContext);
        }

        foreach (var param in stage.Parameters)
            if ((param.EditorSection ?? stage.EditorSection) == section)
                DrawParameter(param, stage, stageState, undoTarget, camera, component, customDrawer);

        if (stageContext is not null)
        {
            customDrawer?.DrawStageFooter(stageContext);
        }

        ImGui.PopID();
    }

    private static void DrawParameter(
        PostProcessParameterDescriptor param,
        PostProcessStageDescriptor stage,
        PostProcessStageState stageState,
        XRBase? undoTarget,
        XRCamera? camera,
        CameraComponent? component,
        IPostProcessStageCustomDrawer? customDrawer)
    {
        if (param.VisibilityCondition != null && stageState.BackingInstance != null)
        {
            if (!param.VisibilityCondition(stageState.BackingInstance))
                return;
        }

        ImGui.PushID(param.Name);

        if (customDrawer is not null && camera is not null)
        {
            var paramContext = new PostProcessParameterCustomDrawerContext(
                camera,
                component,
                stage,
                param,
                stageState,
                undoTarget);

            if (customDrawer.TryDrawParameter(paramContext))
            {
                ImGui.PopID();
                return;
            }
        }

        switch (param.Kind)
        {
            case PostProcessParameterKind.Bool:
                DrawBoolParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Int:
                DrawIntParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Float:
                DrawFloatParameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector2:
                DrawVector2Parameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector3:
                DrawVector3Parameter(param, stageState, undoTarget);
                break;
            case PostProcessParameterKind.Vector4:
                DrawVector4Parameter(param, stageState, undoTarget);
                break;
            default:
                ImGui.TextDisabled($"{param.DisplayName}: (unsupported type)");
                break;
        }

        ImGui.PopID();
    }

    private static void DrawBoolParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        bool fallback = ExtractDefault(param, false);
        bool value = state.GetValue(param.Name, fallback);
        if (ImGui.Checkbox(param.DisplayName, ref value))
        {
            using var _ = Undo.TrackChange(param.DisplayName, undoTarget);
            state.SetValue(param.Name, value);
        }
    }

    private static void DrawIntParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        if (param.EnumOptions.Count > 0)
        {
            DrawEnumParameter(param, state, undoTarget);
            return;
        }

        int fallback = ExtractDefault(param, 0);
        int value = state.GetValue(param.Name, fallback);

        float min = param.Min ?? int.MinValue;
        float max = param.Max ?? int.MaxValue;

        if (ImGui.SliderInt(param.DisplayName, ref value, (int)min, (int)max))
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawEnumParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        int fallback = ExtractDefault(param, 0);
        int value = state.GetValue(param.Name, fallback);
        string currentLabel = param.EnumOptions.FirstOrDefault(o => o.Value == value)?.Label ?? value.ToString();

        if (ImGui.BeginCombo(param.DisplayName, currentLabel))
        {
            foreach (var option in param.EnumOptions)
            {
                bool selected = option.Value == value;
                if (ImGui.Selectable(option.Label, selected) && !selected)
                {
                    using var _ = Undo.TrackChange(param.DisplayName, undoTarget);
                    state.SetValue(param.Name, option.Value);
                    value = option.Value;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private static void DrawFloatParameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        float fallback = ExtractDefault(param, 0.0f);
        float value = state.GetValue(param.Name, fallback);
        float min = param.Min ?? 0.0f;
        float max = param.Max ?? 1.0f;
        float step = param.Step ?? 0.01f;

        string format = step < 0.01f ? "%.4f" : step < 0.1f ? "%.3f" : "%.2f";
        bool useSlider = (max - min) <= 1000.0f && max < float.MaxValue / 2;

        if (useSlider)
        {
            if (ImGui.SliderFloat(param.DisplayName, ref value, min, max, format))
                state.SetValue(param.Name, value);
        }
        else
        {
            if (ImGui.DragFloat(param.DisplayName, ref value, step, min, max, format))
                state.SetValue(param.Name, value);
        }
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawVector2Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector2 fallback = ExtractDefault(param, Vector2.Zero);
        Vector2 value = state.GetValue(param.Name, fallback);

        if (ImGui.DragFloat2(param.DisplayName, ref value, param.Step ?? 0.01f))
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawVector3Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector3 fallback = ExtractDefault(param, Vector3.Zero);
        Vector3 value = state.GetValue(param.Name, fallback);

        bool changed = param.IsColor
            ? ImGui.ColorEdit3(param.DisplayName, ref value)
            : ImGui.DragFloat3(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    private static void DrawVector4Parameter(PostProcessParameterDescriptor param, PostProcessStageState state, XRBase? undoTarget)
    {
        Vector4 fallback = ExtractDefault(param, Vector4.Zero);
        Vector4 value = state.GetValue(param.Name, fallback);

        bool changed = param.IsColor
            ? ImGui.ColorEdit4(param.DisplayName, ref value)
            : ImGui.DragFloat4(param.DisplayName, ref value, param.Step ?? 0.01f);

        if (changed)
            state.SetValue(param.Name, value);
        ImGuiUndoHelper.TrackDragUndo(param.DisplayName, undoTarget);
    }

    /// <summary>Returns the default value of <paramref name="descriptor"/> as <typeparamref name="T"/>, or <paramref name="fallback"/>.</summary>
    public static T ExtractDefault<T>(PostProcessParameterDescriptor descriptor, T fallback)
    {
        if (descriptor.DefaultValue is T typed)
            return typed;

        if (descriptor.DefaultValue is null)
            return fallback;

        try
        {
            if (typeof(T) == typeof(Vector2))
            {
                if (descriptor.DefaultValue is Vector3 v3)
                    return (T)(object)new Vector2(v3.X, v3.Y);
                if (descriptor.DefaultValue is Vector4 v4)
                    return (T)(object)new Vector2(v4.X, v4.Y);
            }

            if (typeof(T) == typeof(Vector3) && descriptor.DefaultValue is Vector4 v4Value)
                return (T)(object)new Vector3(v4Value.X, v4Value.Y, v4Value.Z);

            if (descriptor.DefaultValue is IConvertible convertible)
                return (T)Convert.ChangeType(convertible, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        return fallback;
    }
}
