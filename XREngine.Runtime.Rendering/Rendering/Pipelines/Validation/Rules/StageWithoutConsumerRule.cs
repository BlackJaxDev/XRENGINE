using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds schema stages that no command declares as consumed.</summary>
public sealed class StageWithoutConsumerRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "stage.no-consumer";

    /// <inheritdoc />
    public string Description => "A schema stage has no command that declares it reads the stage.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        IReadOnlyDictionary<string, List<Commands.ViewportRenderCommand>> consumers = context.GetStageConsumers();
        foreach (PostProcessStageDescriptor stage in context.Schema.StagesByKey.Values)
        {
            if (consumers.ContainsKey(stage.Key))
                continue;

            problems.Add(RenderPipelineProblem.ForStage(
                Id,
                ERenderPipelineProblemSeverity.Info,
                string.Concat("No command declares that it reads stage '", stage.Key, "'. Add ConsumesStages to the command type, or ConsumedStageKeys to the quad command, so tools can link them."),
                stage.Key));
        }
    }
}
