using XREngine.Rendering.Pipelines.Validation;

namespace XREngine.UnitTests.Rendering;

/// <summary>A validation rule that always throws. Tests use it to check that the validator isolates rule failures.</summary>
internal sealed class ThrowingValidationRule : IRenderPipelineValidationRule
{
    public string Id => "test.throws";

    public string Description => "Throws.";

    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
        => throw new InvalidOperationException("Test failure.");
}
