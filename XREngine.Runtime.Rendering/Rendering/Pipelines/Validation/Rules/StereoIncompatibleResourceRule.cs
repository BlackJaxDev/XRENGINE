using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds framebuffer textures that do not expand to one layer for each eye in a stereo profile.</summary>
public sealed class StereoIncompatibleResourceRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.stereo";

    /// <inheritdoc />
    public string Description => "A framebuffer texture has one layer and is not stereo-compatible in a stereo profile.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (!context.Profile.Stereo)
            return;

        RenderPipelineResourceLayout layout = context.Resources.Layout;
        HashSet<string> attached = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
            if (layout.OrderedSpecs[i] is FrameBufferSpec fbo)
                for (int a = 0; a < fbo.Attachments.Count; a++)
                    attached.Add(fbo.Attachments[a].ResourceName);

        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            if (layout.OrderedSpecs[i] is not TextureSpec texture ||
                texture.StereoCompatible ||
                texture.Layers > 1 ||
                texture.SizePolicy.SizeClass == RenderResourceSizeClass.AbsolutePixels ||
                !attached.Contains(texture.Name))
            {
                continue;
            }

            problems.Add(RenderPipelineProblem.ForResource(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat("Texture '", texture.Name, "' is a view-sized attachment with one layer and is not stereo-compatible. In a stereo profile it holds only one eye. Mark it stereo-compatible, or give it a mono reason."),
                texture.Name));
        }
    }
}
