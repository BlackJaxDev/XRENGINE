using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

internal static class RenderCaptureCommandPolicy
{
    public static void AddConditional(
        ViewportRenderCommandContainer target,
        RenderPipeline pipeline,
        ERenderCapturePass pass,
        Action<ViewportRenderCommandContainer> buildCommands)
    {
        VPRC_IfElse conditional = target.Add<VPRC_IfElse>();
        conditional.Label = $"CapturePolicy.{pass}";
        conditional.ConditionEvaluator = () => AllowsCurrent(pass);

        ViewportRenderCommandContainer commands = new(pipeline);
        buildCommands(commands);
        conditional.TrueCommands = commands;
    }

    private static bool AllowsCurrent(ERenderCapturePass pass)
    {
        RenderCapturePolicy policy = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.CapturePolicy
            ?? RenderCapturePolicy.None;
        return !policy.IsCapture || policy.Allows(pass);
    }
}
