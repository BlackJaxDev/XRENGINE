using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushProgramBindings : ViewportStateRenderCommand<VPRC_PopProgramBindings>
    {
        public Action<XRRenderProgram>? ApplyUniforms { get; set; }

        protected override void Execute()
        {
            ActivePipelineInstance.RenderState.PushProgramBindings(new XRRenderPipelineInstance.RenderingState.ScopedProgramBindings
            {
                ApplyUniforms = ApplyUniforms
            });
        }
    }
}
