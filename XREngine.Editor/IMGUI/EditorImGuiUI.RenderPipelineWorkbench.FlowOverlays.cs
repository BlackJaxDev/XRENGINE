using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>
    /// Draws overlays on top of the Flow blocks: the resource rail for the selected resource
    /// (a hollow dot for a read, a solid dot for a write).
    /// </summary>
    private static void DrawWorkbenchFlowOverlays(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, RenderPipelineWorkbenchModel model, RenderPipelineTimelineLayout layout)
    {
        IReadOnlyDictionary<int, ERenderPipelineResourceAccess> access = _workbench.Links.ResourceAccessByNode;
        if (access.Count == 0)
            return;

        RenderPipelineCanvasViewState view = _workbench.FlowView;
        uint readColor = ImGui.GetColorU32(new Vector4(0.45f, 0.8f, 1f, 1f));
        uint writeColor = ImGui.GetColorU32(new Vector4(1f, 0.6f, 0.25f, 1f));
        float radius = MathF.Max(3f, 6f * view.Zoom);
        for (int i = 0; i < layout.Steps.Count; i++)
        {
            RenderPipelineTimelineStep step = layout.Steps[i];
            if (!access.TryGetValue(step.Id, out ERenderPipelineResourceAccess use))
                continue;

            Vector2 anchor = view.ToScreen(canvasPos, GetFlowStepMin(step) + new Vector2(FlowStepWidth - 12f, -8f));
            if (!IsWorkbenchRectVisible(anchor - new Vector2(radius), anchor + new Vector2(radius), canvasPos, canvasSize))
                continue;

            if ((use & ERenderPipelineResourceAccess.Write) != 0)
                drawList.AddCircleFilled(anchor, radius, writeColor);
            if ((use & ERenderPipelineResourceAccess.Read) != 0)
                drawList.AddCircle(anchor - new Vector2(radius * 2.4f, 0f), radius, readColor, 12, 2f);
        }
    }
}
