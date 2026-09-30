using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.UI;

namespace XREngine.Rendering;

/// <summary>Installs Dear ImGui context, platform-window, component and pipeline editor capabilities.</summary>
public static class ImGuiBackend
{
    public static void Register()
    {
        ImGuiRuntimeServices.Current = new NativeImGuiRuntimeServices();
        ImGuiPlatformWindowBehavior.Services = new NativeImGuiPlatformWindowBehavior();
        PipelineEditorUiServices.Register<DefaultRenderPipeline>(static _ => new DefaultPipelineEditorUI());
        PipelineEditorUiServices.Register<AdvancedRenderPipeline>(static pipeline => new AdvancedRenderPipelineEditorUI(pipeline));
        CookedBinarySerializer.RegisterRuntimeFactory(static () => new DearImGuiComponent());
    }
}
