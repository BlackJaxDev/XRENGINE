using ImGuiNET;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.PostProcessing;

/// <summary>Provides the advanced pipeline's camera and shading controls.</summary>
internal sealed class AdvancedRenderPipelineEditorUI(AdvancedRenderPipeline pipeline) : IRenderPipelineEditorUIProvider
{
    private static readonly EAdvancedShadingDebugView[] ShadingDebugViews = Enum.GetValues<EAdvancedShadingDebugView>();

    public void DrawCameraSettings(PipelineEditorContext context) => StandardPipelineEditorControls.DrawCameraSettings(context);

    public void DrawDebug(PipelineEditorContext context)
    {
        DrawShadingDebug(context);
        DrawCullingDebug(context);
    }

    private void DrawShadingDebug(PipelineEditorContext context)
    {
        if (!ImGui.CollapsingHeader("Shading", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Shared pipeline setting: affects all views using this pipeline.");
        ImGui.Text("Native Shading Debug View");
        EAdvancedShadingDebugView currentView = pipeline.ShadingDebugView;
        ImGui.SetNextItemWidth(-1.0f);
        if (ImGui.BeginCombo("##ShadingDebugViewCombo", currentView.ToString()))
        {
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

        // The camera's "Meshlet Debug Display" post-process toggle selects the
        // meshlet view while the pipeline selects no other view.
        bool meshletView = currentView == EAdvancedShadingDebugView.MeshletId ||
            currentView == EAdvancedShadingDebugView.Disabled &&
            GpuBvhDebugSettings.IsMeshletDebugDisplayEnabled(context.Camera, context.SelectedPipeline);
        if (meshletView)
            ImGui.TextWrapped(DescribeMeshletView(AdvancedRenderPipeline.ResolveMeshletViewSubmissionStrategy(
                pipeline, context.Camera, RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy)));
    }

    private static void DrawCullingDebug(PipelineEditorContext context)
    {
        if (!GpuBvhDebugSettings.IsOcclusionCulledBoundsEnabled(context.Camera, context.SelectedPipeline) ||
            !ImGui.CollapsingHeader("Culling", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped(DescribeOcclusionCulledBoundsSource(RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy));
    }

    /// <summary>
    /// States what the meshlet view shows for the current frame. It needs a meshlet
    /// producer. The view switches a GPU indexed output to a meshlet strategy when the
    /// renderer has a meshlet path; otherwise every opaque pixel is neutral grey.
    /// </summary>
    private static string DescribeMeshletView(EMeshSubmissionStrategy strategy)
        => strategy is EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuMeshletInstrumented
            ? $"Meshlet view, effective strategy {strategy}: each meshlet has its own color. Grey pixels come from indexed ranges, such as parallax ranges."
            : $"Meshlet view, effective strategy {strategy}: this output has no meshlet producer (CPU-direct submission, or no Vulkan meshlet path), so all opaque pixels are grey.";

    /// <summary>
    /// Names the occlusion test whose results the culled-bounds overlay shows for the
    /// current frame's effective strategy, or states that the strategy has none.
    /// </summary>
    private static string DescribeOcclusionCulledBoundsSource(EMeshSubmissionStrategy strategy)
        => strategy switch
        {
            EMeshSubmissionStrategy.GpuMeshletZeroReadback or EMeshSubmissionStrategy.GpuMeshletInstrumented =>
                $"Occlusion-culled bounds, effective strategy {strategy}. Orange: draws removed by the GPU Hi-Z draw test. Cyan: meshlets removed by the late task-stage Hi-Z test.",
            EMeshSubmissionStrategy.GpuIndirectZeroReadback or EMeshSubmissionStrategy.GpuIndirectInstrumented =>
                $"Occlusion-culled bounds, effective strategy {strategy}. Orange: draws removed by the GPU Hi-Z draw test. This strategy has no meshlet occlusion test.",
            _ => $"Occlusion-culled bounds, effective strategy {strategy}: the Advanced visibility pass runs no occlusion test for this strategy, so the overlay has no data.",
        };
}
