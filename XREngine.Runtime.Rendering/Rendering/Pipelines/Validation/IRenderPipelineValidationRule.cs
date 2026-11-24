namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>
/// One lint rule of the render pipeline validator. A rule reads the context and adds problems. It must not change the
/// pipeline, its instance, or its resource generation.
/// </summary>
public interface IRenderPipelineValidationRule
{
    /// <summary>Gets the stable rule identifier, such as <c>resource.unused</c>.</summary>
    string Id { get; }

    /// <summary>Gets a one-line description of what the rule checks.</summary>
    string Description { get; }

    /// <summary>Adds the problems that the rule finds in <paramref name="context"/> to <paramref name="problems"/>.</summary>
    void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems);
}
