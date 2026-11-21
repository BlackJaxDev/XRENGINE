using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds declared resources that passes read but no pass writes in the frame.</summary>
public sealed class UnwrittenResourceRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.never-written";

    /// <inheritdoc />
    public string Description => "A pass reads a resource that no pass writes.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        foreach (RenderPipelineResourceEntry entry in context.Resources.Entries)
        {
            if (entry.Spec is null ||
                entry.Consumers.Count == 0 ||
                entry.Producers.Count > 0 ||
                entry.Has(ERenderPipelineResourceFlags.History) ||
                entry.Has(ERenderPipelineResourceFlags.External) ||
                entry.Has(ERenderPipelineResourceFlags.Disabled))
            {
                continue;
            }

            // A texture with a factory gets its content from the factory, such as a lookup table or a noise texture.
            if (entry.Spec is TextureSpec { Factory: not null } || entry.Spec is not (TextureSpec or BufferSpec))
                continue;

            problems.Add(RenderPipelineProblem.ForResource(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat("Passes read '", entry.Name, "', but no pass writes it. The content is undefined unless a command writes it without declaring the write."),
                entry.Name));
        }
    }
}
