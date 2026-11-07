using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds command patches that target a structural path instead of an anchor id. A structural path breaks when the base
/// adds or moves commands before the target.
/// </summary>
public sealed class DerivedStructuralPathRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "derived.structural-path";

    /// <inheritdoc />
    public string Description => "A derived pipeline patch targets a structural path, which breaks easily.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Pipeline is not { IsDerived: true } pipeline)
            return;

        IReadOnlyList<RenderPipelineOverlay> overlays = pipeline.Overlays;
        for (int o = 0; o < overlays.Count; o++)
        {
            List<RenderCommandPatch> patches = overlays[o].CommandPatches;
            for (int p = 0; p < patches.Count; p++)
            {
                if (!patches[p].UsesStructuralPath)
                    continue;

                problems.Add(new RenderPipelineProblem(
                    Id,
                    ERenderPipelineProblemSeverity.Warning,
                    string.Concat("Overlay ", (o + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ": ", patches[p].Describe(),
                        " uses a structural path. Give the base command an anchor id, then target the anchor.")));
            }
        }
    }
}
