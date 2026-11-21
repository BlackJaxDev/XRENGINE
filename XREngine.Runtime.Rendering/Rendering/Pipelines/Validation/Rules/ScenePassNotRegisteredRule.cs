using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds mesh pass commands whose scene render pass the pipeline does not register.</summary>
public sealed class ScenePassNotRegisteredRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "command.scene-pass-unregistered";

    /// <inheritdoc />
    public string Description => "A mesh pass renders a scene pass that the pipeline does not register.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        IReadOnlyCollection<int>? registered = context.RegisteredRenderPasses;
        if (registered is null)
            return;

        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            if (context.Structure.Commands[i].Command is not VPRC_RenderMeshesPassShared meshPass || Contains(registered, meshPass.RenderPass))
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Error,
                string.Concat("This command renders scene pass ", meshPass.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture), ", but the pipeline does not register it. No mesh is submitted to that pass. Add it to GetPassIndicesAndSorters."),
                meshPass));
        }
    }

    private static bool Contains(IReadOnlyCollection<int> values, int value)
    {
        foreach (int item in values)
            if (item == value)
                return true;

        return false;
    }
}
