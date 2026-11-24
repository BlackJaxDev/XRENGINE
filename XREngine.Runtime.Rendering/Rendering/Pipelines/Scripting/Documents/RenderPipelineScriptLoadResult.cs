namespace XREngine.Rendering;

/// <summary>The result of <see cref="RenderPipelineScriptLoader.Load"/>.</summary>
/// <param name="Pipeline">The loaded pipeline, or null when a blocking error stopped the load.</param>
/// <param name="Document">The parsed document.</param>
/// <param name="Problems">
/// The problems of the document, of each parent script, and of the load. A loaded pipeline can still have errors for
/// unconverted items; validation then blocks Apply until the user fixes them.
/// </param>
public sealed record RenderPipelineScriptLoadResult(
    RenderPipeline? Pipeline,
    RenderPipelineScriptDocument Document,
    IReadOnlyList<RenderPipelineScriptProblem> Problems)
{
    /// <summary>Gets true when the load made a pipeline.</summary>
    public bool Succeeded => Pipeline is not null;
}
