using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds overlays that were made for a different base type. This occurs when a host, such as the OpenXR eye pipeline
/// factory, applies the overlays of a selected asset to the pipeline type that it creates.
/// </summary>
public sealed class DerivedBaseTypeMismatchRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "derived.base-type-mismatch";

    /// <inheritdoc />
    public string Description => "An overlay was made for a different base pipeline type.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Pipeline is not { IsDerived: true } pipeline)
            return;

        string typeName = pipeline.GetType().FullName ?? pipeline.GetType().Name;
        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        for (int o = 0; o < overlays.Count; o++)
        {
            string? expected = overlays[o].BaseTypeName;
            if (string.IsNullOrEmpty(expected) || string.Equals(expected, typeName, StringComparison.Ordinal))
                continue;

            problems.Add(new RenderPipelineProblem(
                Id,
                ERenderPipelineProblemSeverity.Error,
                string.Concat("Overlay '", overlays[o].Name, "' was made for ", expected, ", but the pipeline is ", typeName,
                    ". Select a pipeline asset that derives from ", typeName, ", or change the base of the asset.")));
        }
    }
}
