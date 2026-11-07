using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds resources that passes read but the resource layout does not declare.</summary>
public sealed class UndeclaredResourceReadRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.undeclared-read";

    /// <inheritdoc />
    public string Description => "A pass reads a resource that the layout does not declare.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        foreach (RenderPipelineResourceEntry entry in context.Resources.Entries)
        {
            if (!entry.IsUsedButUndeclared || entry.Consumers.Count == 0 || entry.Has(ERenderPipelineResourceFlags.External))
                continue;

            problems.Add(RenderPipelineProblem.ForResource(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat("Passes read '", entry.Name, "', but the resource layout does not declare it. Declare it in DescribeResources, or fix the name."),
                entry.Name));
        }
    }
}
