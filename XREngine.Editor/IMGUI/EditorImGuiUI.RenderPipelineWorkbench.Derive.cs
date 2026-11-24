using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>
    /// Draws the Derive action of the authoring row. It opens the new pipeline wizard on the derive page, with the
    /// Workbench pipeline as the base when it is a registered script base.
    /// </summary>
    private static void DrawWorkbenchDeriveButton(RenderPipeline pipeline)
    {
        ImGui.SameLine();
        if (ImGui.Button("Derive"))
        {
            OpenNewRenderPipelineWizard(pipeline);
            if (_newPipelineWizard is { } wizard)
                wizard.Source = ENewRenderPipelineSource.DeriveFromBase;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Makes a new .xrs script that derives from this pipeline. Add patches to the script to change the base commands, resources, and schema.");
    }
}
