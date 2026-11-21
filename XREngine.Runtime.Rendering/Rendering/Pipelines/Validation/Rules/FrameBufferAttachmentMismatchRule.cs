using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds framebuffers whose attachments point to missing textures, put a depth format on a color slot, or mix
/// attachment sizes.
/// </summary>
public sealed class FrameBufferAttachmentMismatchRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "resource.fbo-attachment-mismatch";

    /// <inheritdoc />
    public string Description => "A framebuffer attachment is missing, has the wrong format kind, or has a different size.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        RenderPipelineResourceLayout layout = context.Resources.Layout;
        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            if (layout.OrderedSpecs[i] is not FrameBufferSpec fbo)
                continue;

            RenderResourceSizePolicy? firstSize = null;
            for (int a = 0; a < fbo.Attachments.Count; a++)
            {
                FrameBufferAttachmentDescriptor attachment = fbo.Attachments[a];
                if (!layout.TryGet(attachment.ResourceName, out RenderPipelineResourceSpec? target))
                {
                    problems.Add(RenderPipelineProblem.ForResource(
                        Id,
                        ERenderPipelineProblemSeverity.Warning,
                        string.Concat("Framebuffer '", fbo.Name, "' attaches '", attachment.ResourceName, "' at ", attachment.Attachment.ToString(), ", but the layout does not declare it."),
                        fbo.Name));
                    continue;
                }

                if (target is TextureSpec texture && IsColorSlot(attachment.Attachment) && IsDepthFormat(texture))
                {
                    problems.Add(RenderPipelineProblem.ForResource(
                        Id,
                        ERenderPipelineProblemSeverity.Error,
                        string.Concat("Framebuffer '", fbo.Name, "' attaches depth texture '", texture.Name, "' to color slot ", attachment.Attachment.ToString(), "."),
                        fbo.Name));
                }

                // Only base-level texture attachments are compared. Views and mip attachments have a derived size.
                if (target is not TextureSpec || attachment.MipLevel != 0)
                    continue;

                if (firstSize is null)
                {
                    firstSize = target.SizePolicy;
                }
                else if (target.SizePolicy != firstSize.Value)
                {
                    problems.Add(RenderPipelineProblem.ForResource(
                        Id,
                        ERenderPipelineProblemSeverity.Warning,
                        string.Concat("Framebuffer '", fbo.Name, "' attaches '", target.Name, "' with a different size policy than its first attachment. The render area uses the smallest attachment."),
                        fbo.Name));
                }
            }
        }
    }

    private static bool IsColorSlot(EFrameBufferAttachment attachment)
        => attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;

    private static bool IsDepthFormat(TextureSpec texture)
    {
        string name = texture.SizedInternalFormat?.ToString() ?? texture.InternalFormat?.ToString() ?? string.Empty;
        return name.StartsWith("Depth", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Stencil", StringComparison.OrdinalIgnoreCase);
    }
}
