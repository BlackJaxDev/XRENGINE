using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>Draws queued debug shapes after scene color and depth are complete.</summary>
    private void AppendAdvancedDebugShapeCommands(ViewportRenderCommandContainer commands)
    {
        RenderCaptureCommandPolicy.AddConditional(
            commands, this, ERenderCapturePass.DebugOverlays, debugCommands =>
            {
                using (debugCommands.AddUsing<VPRC_PushViewportRenderArea>(x => x.UseInternalResolution = true))
                using (debugCommands.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(
                    ForwardPassFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
                {
                    debugCommands.Add<VPRC_ColorMask>().Set(true, true, true, true);
                    debugCommands.Add<VPRC_DepthTest>().Enable = true;
                    debugCommands.Add<VPRC_DepthWrite>().Allow = false;
                    debugCommands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                    var worldShapes = debugCommands.Add<VPRC_RenderDebugShapes>();
                    worldShapes.RenderGraphPassName = "Advanced.DebugShapesDepthTested";
                    worldShapes.DepthTested = true;
                    debugCommands.Add<VPRC_DepthTest>().Enable = false;
                    var overlayShapes = debugCommands.Add<VPRC_RenderDebugShapes>();
                    overlayShapes.RenderGraphPassName = "Advanced.DebugShapesOverlay";
                    debugCommands.Add<VPRC_DepthTest>().Enable = true;
                    debugCommands.Add<VPRC_DepthWrite>().Allow = true;
                }
            });
    }
}
