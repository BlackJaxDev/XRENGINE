using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>
    /// Gets the record of which child container each branch command executed for this instance.
    /// Tools read it to show the branches that ran in the last frames.
    /// </summary>
    public RenderPipelineBranchTrace BranchTrace { get; } = new();

    /// <summary>
    /// Gets the order in which the command chain of this instance entered render graph passes in the newest frame.
    /// Tools compare it with the declared pass order and with the backend plan.
    /// </summary>
    public RenderPipelineExecutionTrace ExecutionTrace { get; } = new();
}
