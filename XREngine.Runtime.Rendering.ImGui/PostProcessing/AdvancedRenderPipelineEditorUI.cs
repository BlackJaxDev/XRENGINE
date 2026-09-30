using ImGuiNET;

namespace XREngine.Rendering.PostProcessing;

/// <summary>Provides the advanced pipeline's camera and shading controls.</summary>
internal sealed class AdvancedRenderPipelineEditorUI(AdvancedRenderPipeline pipeline) : IRenderPipelineEditorUIProvider
{
    private static readonly EAdvancedShadingDebugView[] ShadingDebugViews = Enum.GetValues<EAdvancedShadingDebugView>();

    public void DrawCameraSettings(PipelineEditorContext context) => StandardPipelineEditorControls.DrawCameraSettings(context);

    public void DrawDebug(PipelineEditorContext context)
    {
        if (!ImGui.CollapsingHeader("Shading", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Shared pipeline setting: affects all views using this pipeline.");
        ImGui.Text("Native Shading Debug View");
        EAdvancedShadingDebugView currentView = pipeline.ShadingDebugView;
        ImGui.SetNextItemWidth(-1.0f);
        if (!ImGui.BeginCombo("##ShadingDebugViewCombo", currentView.ToString()))
            return;

        foreach (EAdvancedShadingDebugView view in ShadingDebugViews)
        {
            bool isSelected = view == currentView;
            if (ImGui.Selectable(view.ToString(), isSelected))
                pipeline.ShadingDebugView = view;
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }
}
