namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>One pass of a plan snapshot.</summary>
/// <param name="PassIndex">The pass index from the metadata.</param>
/// <param name="Name">The pass name.</param>
/// <param name="Stage">The pass stage.</param>
/// <param name="ExecutionOrder">The position of the pass in the plan order.</param>
/// <param name="AuthoredOrder">The declaration order of the pass in the command chain.</param>
/// <param name="BatchIndex">The batch that holds the pass, or -1.</param>
/// <param name="Executed">Whether the pass executed in the last recorded frame. Sources that do not track execution report true.</param>
/// <param name="Metadata">The source metadata of the pass.</param>
public sealed record PlanPassSnapshot(
    int PassIndex,
    string Name,
    ERenderGraphPassStage Stage,
    int ExecutionOrder,
    int AuthoredOrder,
    int BatchIndex,
    bool Executed,
    RenderPassMetadata Metadata);
