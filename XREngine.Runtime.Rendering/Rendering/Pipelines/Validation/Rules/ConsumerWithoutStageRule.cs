using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds commands that read a stage key that the schema does not define.</summary>
public sealed class ConsumerWithoutStageRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "stage.unknown";

    /// <inheritdoc />
    public string Description => "A command reads a stage that the schema does not define.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        foreach (KeyValuePair<string, List<ViewportRenderCommand>> pair in context.GetStageConsumers())
        {
            if (context.Schema.StagesByKey.ContainsKey(pair.Key))
                continue;

            for (int i = 0; i < pair.Value.Count; i++)
            {
                problems.Add(RenderPipelineProblem.ForCommand(
                    Id,
                    ERenderPipelineProblemSeverity.Warning,
                    string.Concat("This command reads stage '", pair.Key, "', but the post-process schema has no stage with that key. Camera settings for it do not exist."),
                    pair.Value[i]));
            }
        }
    }
}
