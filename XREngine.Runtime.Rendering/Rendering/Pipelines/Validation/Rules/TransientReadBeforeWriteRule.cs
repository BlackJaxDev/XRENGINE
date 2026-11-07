using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds transient resources that a pass reads before the first pass that writes them. A transient resource has no
/// content from the previous frame, so the read sees undefined data.
/// </summary>
public sealed class TransientReadBeforeWriteRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.transient-read-before-write";

    /// <inheritdoc />
    public string Description => "A pass reads a transient resource before any pass writes it in the frame.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        IReadOnlyList<RenderPassMetadata> order = context.Resources.PassOrder;
        Dictionary<int, int> position = new(order.Count);
        for (int i = 0; i < order.Count; i++)
            position[order[i].PassIndex] = i;

        foreach (RenderPipelineResourceEntry entry in context.Resources.Entries)
        {
            if (entry.Spec?.Lifetime != RenderResourceLifetime.Transient || entry.Producers.Count == 0 || entry.Consumers.Count == 0)
                continue;

            int firstWrite = MinPosition(entry.Producers, position);
            for (int c = 0; c < entry.Consumers.Count; c++)
            {
                int consumer = entry.Consumers[c];
                if (!position.TryGetValue(consumer, out int readAt) || readAt >= firstWrite || Contains(entry.Producers, consumer))
                    continue;

                problems.Add(RenderPipelineProblem.ForPass(
                    Id,
                    ERenderPipelineProblemSeverity.Error,
                    string.Concat("This pass reads transient resource '", entry.Name, "' before any pass writes it. Make it a history resource, or order the writer first."),
                    consumer));
            }
        }
    }

    private static int MinPosition(IReadOnlyList<int> passes, Dictionary<int, int> position)
    {
        int min = int.MaxValue;
        for (int i = 0; i < passes.Count; i++)
            if (position.TryGetValue(passes[i], out int at) && at < min)
                min = at;

        return min;
    }

    private static bool Contains(IReadOnlyList<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i] == value)
                return true;

        return false;
    }
}
