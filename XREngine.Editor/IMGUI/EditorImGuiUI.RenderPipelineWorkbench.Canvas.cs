using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>
    /// Opens a pan-and-zoom canvas child window. Pan with the right or middle mouse button, or Space with the
    /// left button; zoom with the wheel around the mouse. Returns false when the canvas has no area; the caller
    /// must still call <see cref="EndWorkbenchCanvas"/>.
    /// </summary>
    private static bool BeginWorkbenchCanvas(
        string id,
        RenderPipelineCanvasViewState view,
        out ImDrawListPtr drawList,
        out Vector2 canvasPos,
        out Vector2 canvasSize,
        out bool hovered)
    {
        ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        drawList = ImGui.GetWindowDrawList();
        canvasPos = ImGui.GetCursorScreenPos();
        canvasSize = ImGui.GetContentRegionAvail();
        hovered = false;
        if (canvasSize.X < 1f || canvasSize.Y < 1f)
            return false;

        ImGui.InvisibleButton("##CanvasSurface", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        hovered = ImGui.IsItemHovered();
        ImGuiIOPtr io = ImGui.GetIO();

        bool panning = ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0f) ||
            ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0f) ||
            (ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f));
        if (hovered && panning)
            view.Pan += io.MouseDelta / MathF.Max(1e-6f, view.Zoom);

        if (hovered && MathF.Abs(io.MouseWheel) > 1e-6f)
        {
            float previous = view.Zoom;
            float step = io.KeyCtrl ? 1.15f : 1.07f;
            float next = Math.Clamp(previous * MathF.Pow(step, io.MouseWheel), 0.25f, 2.5f);
            if (MathF.Abs(next - previous) > 1e-6f)
            {
                Vector2 mouseLocal = io.MousePos - canvasPos;
                Vector2 worldBefore = (mouseLocal / previous) - view.Pan;
                view.Zoom = next;
                Vector2 worldAfter = (mouseLocal / next) - view.Pan;
                view.Pan += worldAfter - worldBefore;
            }
        }

        if (view.PendingFocus is { } focus)
        {
            // Center the requested canvas point.
            view.Pan = (canvasSize * 0.5f / view.Zoom) - focus;
            view.PendingFocus = null;
        }

        drawList.PushClipRect(canvasPos, canvasPos + canvasSize, true);
        return true;
    }

    private static void EndWorkbenchCanvas(bool began)
    {
        if (began)
            ImGui.GetWindowDrawList().PopClipRect();
        ImGui.EndChild();
    }

    /// <summary>Draws a light background grid for a Workbench canvas.</summary>
    private static void DrawWorkbenchCanvasGrid(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelineCanvasViewState view)
        => DrawEditorCanvasGrid(drawList, canvasPos, canvasSize, view.Pan, view.Zoom);

    /// <summary>
    /// Draws a background grid and the canvas axes for a pan-and-zoom canvas. Node editors such as the
    /// shader graph and the Workbench share it.
    /// </summary>
    private static void DrawEditorCanvasGrid(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, Vector2 pan, float zoom)
    {
        float step = MathF.Max(10f, 64f * zoom);
        uint color = ImGui.GetColorU32(ImGuiCol.Separator, 0.35f);
        uint axisColor = ImGui.GetColorU32(ImGuiCol.PlotLines, 0.5f);
        Vector2 origin = canvasPos + (pan * zoom);
        float startX = WorkbenchMod(origin.X - canvasPos.X, step);
        float startY = WorkbenchMod(origin.Y - canvasPos.Y, step);
        for (float x = startX; x < canvasSize.X; x += step)
            drawList.AddLine(new Vector2(canvasPos.X + x, canvasPos.Y), new Vector2(canvasPos.X + x, canvasPos.Y + canvasSize.Y), color);
        for (float y = startY; y < canvasSize.Y; y += step)
            drawList.AddLine(new Vector2(canvasPos.X, canvasPos.Y + y), new Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + y), color);

        drawList.AddLine(new Vector2(origin.X, canvasPos.Y), new Vector2(origin.X, canvasPos.Y + canvasSize.Y), axisColor, 2f);
        drawList.AddLine(new Vector2(canvasPos.X, origin.Y), new Vector2(canvasPos.X + canvasSize.X, origin.Y), axisColor, 2f);
    }

    /// <summary>Returns whether a screen rectangle overlaps the canvas, so draw code can skip hidden items.</summary>
    private static bool IsWorkbenchRectVisible(Vector2 min, Vector2 max, Vector2 canvasPos, Vector2 canvasSize)
        => max.X >= canvasPos.X && max.Y >= canvasPos.Y && min.X <= canvasPos.X + canvasSize.X && min.Y <= canvasPos.Y + canvasSize.Y;

    /// <summary>Draws a cubic curve as dashes or dots without allocating.</summary>
    private static void DrawWorkbenchPatternedBezier(ImDrawListPtr drawList, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint color, float thickness, bool dotted)
    {
        const int Segments = 32;
        Vector2 previous = p0;
        for (int i = 1; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            float u = 1f - t;
            Vector2 point = (u * u * u * p0) + (3f * u * u * t * p1) + (3f * u * t * t * p2) + (t * t * t * p3);
            if (dotted)
            {
                if ((i & 1) == 0)
                    drawList.AddCircleFilled(point, thickness, color);
            }
            else if ((i & 1) == 1)
            {
                drawList.AddLine(previous, point, color, thickness);
            }

            previous = point;
        }
    }

    private static float WorkbenchMod(float x, float m)
    {
        float r = x % m;
        return r < 0f ? r + m : r;
    }
}
