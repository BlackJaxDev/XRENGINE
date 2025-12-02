using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using XREngine.Components;
using XREngine.Editor;

namespace XREngine.Editor.ComponentEditors;

internal static class ComponentEditorLayout
{
    private sealed class InspectorModeState
    {
        public bool UseCustom = true;
    }

    private sealed class PreviewDialogState
    {
        public bool IsOpen;
        public string Title = string.Empty;
        public nint TextureHandle;
        public Vector2 PixelSize = new(1f, 1f);
        public bool FlipVertically = true;
    }

    private static readonly ConditionalWeakTable<XRComponent, InspectorModeState> s_modes = new();
    private static PreviewDialogState? s_previewDialog;
    private static int s_previewFrameRendered = -1;

    public static bool DrawInspectorModeToggle(XRComponent component, HashSet<object> visited, string? customLabel)
    {
        if (component is null)
            return false;

        string label = string.IsNullOrWhiteSpace(customLabel) ? "Custom Editor" : customLabel!;
        InspectorModeState state = s_modes.GetValue(component, _ => new InspectorModeState());
        bool useCustom = state.UseCustom;

        ImGui.SeparatorText("Inspector View");
        ImGui.PushID(component.GetHashCode());

        bool showDefault = !useCustom;
        if (ImGui.RadioButton("Default Properties", showDefault))
            useCustom = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(label, useCustom))
            useCustom = true;

        ImGui.PopID();

        state.UseCustom = useCustom;

        if (!useCustom)
        {
            UnitTestingWorld.UserInterface.DrawDefaultComponentInspector(component, visited);
            return false;
        }

        return true;
    }

    public static void RequestPreviewDialog(string title, nint textureHandle, Vector2 pixelSize, bool flipVertically)
    {
        if (textureHandle == nint.Zero)
            return;

        s_previewDialog ??= new PreviewDialogState();
        s_previewDialog.IsOpen = true;
        s_previewDialog.Title = string.IsNullOrWhiteSpace(title) ? "Preview" : title;
        s_previewDialog.TextureHandle = textureHandle;
        s_previewDialog.PixelSize = new Vector2(MathF.Max(1f, pixelSize.X), MathF.Max(1f, pixelSize.Y));
        s_previewDialog.FlipVertically = flipVertically;
        s_previewFrameRendered = -1;
    }

    public static void DrawActivePreviewDialog()
    {
        if (s_previewDialog is not { IsOpen: true } dialog)
            return;

        int frame = ImGui.GetFrameCount();
        if (frame == s_previewFrameRendered)
            return;

        s_previewFrameRendered = frame;

        ImGui.SetNextWindowSize(new Vector2(512f, 512f), ImGuiCond.Appearing);
        bool open = dialog.IsOpen;
        if (ImGui.Begin(dialog.Title, ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            Vector2 available = ImGui.GetContentRegionAvail();
            Vector2 displaySize = CalculateDisplaySize(dialog.PixelSize, available);
            Vector2 uv0 = dialog.FlipVertically ? new Vector2(0f, 1f) : Vector2.Zero;
            Vector2 uv1 = dialog.FlipVertically ? new Vector2(1f, 0f) : Vector2.One;
            ImGui.Image(dialog.TextureHandle, displaySize, uv0, uv1);
            ImGui.TextDisabled($"{dialog.PixelSize.X:0} x {dialog.PixelSize.Y:0}");
        }
        ImGui.End();

        dialog.IsOpen = open;
        if (!open)
        {
            s_previewDialog = null;
            s_previewFrameRendered = -1;
        }
    }

    private static Vector2 CalculateDisplaySize(Vector2 pixelSize, Vector2 available)
    {
        float width = MathF.Max(1f, pixelSize.X);
        float height = MathF.Max(1f, pixelSize.Y);

        float scale = 1f;
        if (available.X > 0f && available.Y > 0f)
        {
            float scaleX = available.X / width;
            float scaleY = available.Y / height;
            scale = MathF.Min(scaleX, scaleY);
        }

        if (!float.IsFinite(scale) || scale <= 0f)
            scale = 1f;

        return new Vector2(width * scale, height * scale);
    }
}
