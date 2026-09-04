using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        IReadOnlyList<AdvancedRenderStageDescriptor> stages =
            AdvancedRenderPipelineFrameContract.OrderedStages;

        // Jitter must be active while the native visibility/opaque stages render
        // their depth and velocity inputs. Accumulation and PopJitter remain in
        // the late post stage, after those inputs have been produced.
        AppendAdvancedTemporalBegin(commands);

        for (int i = 0; i < stages.Count; i++)
            AppendStage(commands, stages[i]);

        return commands;
    }

    private void AppendStage(
        ViewportRenderCommandContainer commands,
        in AdvancedRenderStageDescriptor descriptor)
    {
        commands.Add<VPRC_Annotation>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_GPUTimerBegin>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_AdvancedRenderStage>().SetStage(descriptor.Stage);

        // The stage command retains the stable backend-facing frame-contract identity.
        // Commands which consume the native HDR/depth outputs are appended immediately
        // after their corresponding marker so they share the same ordered contract.
        switch (descriptor.Stage)
        {
            case EAdvancedRenderStage.LatePasses:
                AppendAdvancedLatePassCommands(commands);
                break;
            case EAdvancedRenderStage.TemporalAndPostProcessing:
                AppendAdvancedPostProcessCommands(commands);
                break;
            case EAdvancedRenderStage.Output:
                AppendAdvancedOutputCommands(commands);
                break;
            case EAdvancedRenderStage.UserInterface:
                AppendAdvancedScreenSpaceUi(commands);
                break;
        }
        commands.Add<VPRC_GPUTimerEnd>().Label = descriptor.GpuLabel;
    }

    private void AppendAdvancedScreenSpaceUi(ViewportRenderCommandContainer commands)
    {
        var ui = commands.Add<VPRC_IfElse>();
        ui.Label = "AdvancedScreenSpaceUiAllowed";
        ui.ConditionEvaluator = () => AllowsScreenSpaceUi;
        var uiCommands = new ViewportRenderCommandContainer(this);
        uiCommands.Add<VPRC_RenderScreenSpaceUI>();
        ui.TrueCommands = uiCommands;
    }
}
