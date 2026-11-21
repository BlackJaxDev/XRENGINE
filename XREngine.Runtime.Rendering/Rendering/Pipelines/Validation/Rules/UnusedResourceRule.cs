using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds declared resources that no pass uses.</summary>
public sealed class UnusedResourceRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.unused";

    /// <inheritdoc />
    public string Description => "The layout declares a resource that no pass uses.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        foreach (RenderPipelineResourceEntry entry in context.Resources.Entries)
        {
            if (!entry.IsDeclaredButUnused ||
                entry.Has(ERenderPipelineResourceFlags.Disabled) ||
                entry.Has(ERenderPipelineResourceFlags.External))
            {
                continue;
            }

            problems.Add(RenderPipelineProblem.ForResource(
                Id,
                ERenderPipelineProblemSeverity.Info,
                string.Concat("No pass uses '", entry.Name, "'. Remove it, or declare the pass that uses it."),
                entry.Name));
        }
    }
}
