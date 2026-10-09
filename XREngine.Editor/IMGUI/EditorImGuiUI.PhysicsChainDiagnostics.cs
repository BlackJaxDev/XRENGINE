using ImGuiNET;
using XREngine.Components;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static void DrawPhysicsChainRuntimeDiagnostics(PhysicsChainComponent chain)
    {
        if (!ImGui.CollapsingHeader("Physics Chain Runtime"))
            return;

        PhysicsChainRuntimeDiagnostics diagnostics = chain.GetRuntimeDiagnostics();
        PhysicsChainRenderingDiagnostics rendering = diagnostics.Rendering;
        ImGui.TextUnformatted($"Handle: {diagnostics.Handle.Slot}:{diagnostics.Handle.Generation}");
        ImGui.TextUnformatted($"Template: {diagnostics.TemplateId}");
        ImGui.TextUnformatted($"Kernel bucket: {diagnostics.GpuKernelFamilies}");
        ImGui.TextUnformatted($"State slice: {diagnostics.StateSlice.Offset}, {diagnostics.StateSlice.Count}, generation {diagnostics.StateSlice.Generation}");
        ImGui.TextUnformatted($"Palette slice: {diagnostics.PaletteSlice.Offset}, {diagnostics.PaletteSlice.Count}, generation {diagnostics.PaletteSlice.Generation}");
        ImGui.TextUnformatted($"Previous palette: {diagnostics.PreviousPaletteSlice.Offset}, {diagnostics.PreviousPaletteSlice.Count}, generation {diagnostics.PreviousPaletteSlice.Generation}");
        ImGui.TextUnformatted($"Bounds slot: {diagnostics.BoundsSlot.Slot}:{diagnostics.BoundsSlot.Generation}");
        ImGui.TextUnformatted($"Output: generation {diagnostics.OutputGeneration}, simulation frame {diagnostics.OutputSimulationFrame}");
        ImGui.TextUnformatted($"Quality: {diagnostics.RequestedQualityTier} requested, {diagnostics.EffectiveQualityTier} effective");
        ImGui.TextUnformatted($"Sleeping: {diagnostics.IsSleeping}");
        ImGui.TextUnformatted($"Backend: {diagnostics.Backend}, {diagnostics.RuntimeStatus}, {diagnostics.GpuBackendState}");
        ImGui.TextUnformatted($"Bound renderers: {rendering.BoundRendererCount}");
        ImGui.TextUnformatted($"Conservative bounds: {rendering.ConservativeBoundsStatus ?? "Unavailable"}");
        ImGui.TextWrapped($"Last recorded global GPU error: {rendering.LastGlobalGpuError ?? "None"}");

        ImGui.SeparatorText("Captured scene frame");
        ImGui.TextUnformatted($"Visible renderers: {rendering.FrameVisibleRendererCount}");
        ImGui.TextUnformatted($"Aggregate deformation dispatches: {rendering.FrameAggregateDeformationDispatchCount}");
        ImGui.TextUnformatted($"Legacy skinning dispatches: {rendering.FrameLegacySkinningDispatchCount}");
        ImGui.TextUnformatted($"Prepared indirect command slots: {rendering.FramePreparedIndirectCommandCount}");
        ImGui.TextUnformatted($"Indirect draw calls: {rendering.FrameIndirectDrawCallCount}");
        if (rendering.HasSubmittedIndirectDrawCount)
            ImGui.TextUnformatted($"Vulkan submitted indirect draws: {rendering.FrameSubmittedIndirectDrawCount}");
        ImGui.TextUnformatted($"Draw calls: {rendering.FrameDrawCallCount}");
        ImGui.TextUnformatted($"CPU visible triangles: {rendering.FrameCpuVisibleTriangleCount}");
    }
}
