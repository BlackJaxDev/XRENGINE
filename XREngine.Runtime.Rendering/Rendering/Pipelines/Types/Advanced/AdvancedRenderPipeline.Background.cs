using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>Loads native HDR/depth and draws only admitted far-depth background materials.</summary>
    private void AppendAdvancedBackgroundCommands(ViewportRenderCommandContainer commands)
    {
        using (commands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(ForwardPassFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
            commands.Add<VPRC_RenderAdvancedBackground>().Stereo = Stereo;
    }
}
