using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds texture formats that the target backend does not support well. Vulkan and WebGPU make three-channel formats
/// optional, and most devices do not render to them. The rule reads <see cref="PortableRenderFormats"/>.
/// </summary>
public sealed class BackendUnsupportedFormatRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.backend-format";

    /// <inheritdoc />
    public string Description => "A texture format is optional or missing on the target backend.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Backend != RendererBackendId.Vulkan && context.Backend != RendererBackendId.WebGPU)
            return;

        RenderPipelineResourceLayout layout = context.Resources.Layout;
        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            if (layout.OrderedSpecs[i] is not TextureSpec texture || !IsThreeChannel(texture))
                continue;

            problems.Add(RenderPipelineProblem.ForResource(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat("Texture '", texture.Name, "' uses a three-channel format. ", context.Backend.Value, " makes these formats optional, and most devices cannot render to them. Use a four-channel format."),
                texture.Name));
        }
    }

    private static bool IsThreeChannel(TextureSpec texture)
    {
        if (texture.SizedInternalFormat is { } sized)
            return PortableRenderFormats.IsUnpackedThreeChannel(sized);

        // Some code textures give only the GL internal format. The sized enum uses the same names.
        return texture.InternalFormat is { } internalFormat &&
               Enum.TryParse(internalFormat.ToString(), out ESizedInternalFormat parsed) &&
               PortableRenderFormats.IsUnpackedThreeChannel(parsed);
    }
}
