using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.State)]
    public class VPRC_PushProgramBindings : ViewportStateRenderCommand<VPRC_PopProgramBindings>
    {
        public Action<XRRenderProgram>? ApplyUniforms { get; set; }

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PushProgramBindingsState(
                new XRRenderPipelineInstance.RenderingState.ScopedProgramBindings(ApplyUniforms));
        }
    }
}
