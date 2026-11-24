using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One cached row of the Barriers tab of the Execution view.</summary>
/// <param name="Barrier">The barrier.</param>
/// <param name="Kind">The barrier kind as a filter flag.</param>
/// <param name="PassText">The pass name and index.</param>
/// <param name="LayoutText">The layout transition, or an empty string for buffers.</param>
/// <param name="StageText">The stage transition.</param>
/// <param name="AccessText">The access transition.</param>
/// <param name="SearchText">The lowercase text that the search matches.</param>
public readonly record struct RenderPipelinePlanBarrierRow(
    PlanBarrierSnapshot Barrier,
    ERenderPipelineBarrierKindFilter Kind,
    string PassText,
    string LayoutText,
    string StageText,
    string AccessText,
    string SearchText);
