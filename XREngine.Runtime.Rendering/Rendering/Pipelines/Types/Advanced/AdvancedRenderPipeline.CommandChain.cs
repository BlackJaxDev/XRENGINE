using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        IReadOnlyList<AdvancedRenderStageDescriptor> stages =
            AdvancedRenderPipelineFrameContract.OrderedStages;

        for (int i = 0; i < stages.Count; i++)
            AppendStage(commands, stages[i]);

        return commands;
    }

    private static void AppendStage(
        ViewportRenderCommandContainer commands,
        in AdvancedRenderStageDescriptor descriptor)
    {
        commands.Add<VPRC_Annotation>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_GPUTimerBegin>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_AdvancedRenderStage>().SetStage(descriptor.Stage);
        commands.Add<VPRC_GPUTimerEnd>().Label = descriptor.GpuLabel;
    }
}
