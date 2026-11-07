using XREngine.Rendering.Pipelines.Validation.Rules;

namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>
/// Runs validation rules on a render pipeline and combines their problems with the problems that the pipeline
/// sources reported, such as requirement diagnostics and resource generation failures.
/// </summary>
public static class RenderPipelineValidator
{
    /// <summary>Gets the rules that run when the caller does not give a rule list.</summary>
    public static IReadOnlyList<IRenderPipelineValidationRule> DefaultRules { get; } =
    [
        new GpuWorkWithoutPassRule(),
        new ScenePassNotRegisteredRule(),
        new TransientReadBeforeWriteRule(),
        new FrameBufferAttachmentMismatchRule(),
        new FullscreenSamplerWithoutInputRule(),
        new UndeclaredResourceReadRule(),
        new UnwrittenResourceRule(),
        new UnpairedStatePushRule(),
        new ConsumerWithoutStageRule(),
        new BackendUnsupportedFormatRule(),
        new StereoIncompatibleResourceRule(),
        new UnusedResourceRule(),
        new StageWithoutConsumerRule(),
        new BranchWithoutConditionRule(),
        new UnresolvedBranchKeyRule(),
        new CodeBranchWithoutLabelRule(),
        new CodeCommandInDataPipelineRule(),
    ];

    /// <summary>Runs the rules on a context and returns the report.</summary>
    /// <param name="context">The pipeline data to check.</param>
    /// <param name="rules">The rules to run, or <see langword="null"/> to run <see cref="DefaultRules"/>.</param>
    public static RenderPipelineValidationReport Validate(RenderPipelineValidationContext context, IReadOnlyList<IRenderPipelineValidationRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        rules ??= DefaultRules;

        List<RenderPipelineProblem> problems = new(context.SourceProblems.Count + 16);
        problems.AddRange(context.SourceProblems);
        for (int i = 0; i < rules.Count; i++)
        {
            IRenderPipelineValidationRule rule = rules[i];
            try
            {
                rule.Evaluate(context, problems);
            }
            catch (Exception ex)
            {
                // One faulty rule must not hide the result of the other rules.
                problems.Add(new RenderPipelineProblem(
                    rule.Id,
                    ERenderPipelineProblemSeverity.Error,
                    string.Concat("The rule failed: ", ex.GetType().Name, ": ", ex.Message)));
            }
        }

        return new RenderPipelineValidationReport(problems);
    }

    /// <summary>Builds a context from a live pipeline and runs the default rules.</summary>
    public static RenderPipelineValidationReport Validate(RenderPipeline pipeline, XRRenderPipelineInstance? instance, RendererBackendId backend)
        => Validate(RenderPipelineValidationContext.FromPipeline(pipeline, instance, backend));
}
