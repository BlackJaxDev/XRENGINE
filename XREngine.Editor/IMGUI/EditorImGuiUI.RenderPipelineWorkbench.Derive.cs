using ImGuiNET;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>Draws the Derive action of the authoring row.</summary>
    private static void DrawWorkbenchDeriveButton(RenderPipeline pipeline)
    {
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button("Derive");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Derived pipelines are not available yet.");
    }
}
