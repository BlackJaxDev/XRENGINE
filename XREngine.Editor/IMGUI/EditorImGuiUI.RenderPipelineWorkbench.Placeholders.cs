using ImGuiNET;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static void DrawWorkbenchSettingsView()
        => ImGui.TextDisabled("Settings view.");

    private static void DrawWorkbenchValidationView()
        => ImGui.TextDisabled("Validation view.");

    private static void DrawWorkbenchScriptView()
        => ImGui.TextDisabled("Script view.");

    private static void DrawWorkbenchTargetBarExtras(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
    }

    private static bool IsWorkbenchPipelineEditable(RenderPipeline pipeline)
        => pipeline is CustomRenderPipeline;
}
