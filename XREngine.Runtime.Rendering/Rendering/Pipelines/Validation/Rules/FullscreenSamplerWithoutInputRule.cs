using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds samplers of an authored quad material shader that have no declared input texture. The sampler then reads
/// an unbound texture, and the pass metadata does not order the pass after the producer.
/// </summary>
/// <param name="samplerProvider">
/// Returns the sampler names of a fragment shader path, or null when the shader cannot be read. Null uses
/// <see cref="QuadMaterialSamplerReflection"/>.
/// </param>
public sealed class FullscreenSamplerWithoutInputRule(Func<string, IReadOnlyList<string>?>? samplerProvider = null) : IRenderPipelineValidationRule
{
    private readonly Func<string, IReadOnlyList<string>?> _samplerProvider = samplerProvider ?? ReflectSamplers;

    /// <inheritdoc />
    public string Id => "resource.sampler-without-input";

    /// <inheritdoc />
    public string Description => "A quad material shader samples a texture that the material does not declare as an input.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Pipeline is not CustomRenderPipeline custom)
            return;

        List<AuthoredRenderResource> resources = custom.AuthoredResources;
        for (int i = 0; i < resources.Count; i++)
        {
            AuthoredRenderResource resource = resources[i];
            if (resource.Kind != EAuthoredRenderResourceKind.QuadMaterial || string.IsNullOrWhiteSpace(resource.FragmentShader))
                continue;

            IReadOnlyList<string>? samplers = _samplerProvider(resource.FragmentShader);
            if (samplers is null)
            {
                problems.Add(RenderPipelineProblem.ForResource(
                    Id,
                    ERenderPipelineProblemSeverity.Error,
                    string.Concat("Quad material '", resource.Name, "' cannot read fragment shader '", resource.FragmentShader, "'."),
                    resource.Name));
                continue;
            }

            for (int s = 0; s < samplers.Count; s++)
            {
                if (ContainsIgnoreCase(resource.InputTextures, samplers[s]))
                    continue;

                problems.Add(RenderPipelineProblem.ForResource(
                    Id,
                    ERenderPipelineProblemSeverity.Warning,
                    string.Concat("Quad material '", resource.Name, "' samples '", samplers[s], "', but declares no input with that name. Add the texture to its inputs."),
                    resource.Name));
            }
        }
    }

    private static IReadOnlyList<string>? ReflectSamplers(string path)
        => QuadMaterialSamplerReflection.TryGetSamplers(path, out IReadOnlyList<string> samplers, out _) ? samplers : null;

    private static bool ContainsIgnoreCase(List<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
