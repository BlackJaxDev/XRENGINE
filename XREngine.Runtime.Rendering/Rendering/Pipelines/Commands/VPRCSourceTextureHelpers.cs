using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRCSourceTextureHelpers
{
    public static bool TryResolveColorTexture(
        XRRenderPipelineInstance instance,
        string? sourceTextureName,
        string? sourceFBOName,
        out XRTexture? texture,
        out string failure)
    {
        texture = null;

        if (!string.IsNullOrWhiteSpace(sourceTextureName))
        {
            if (instance.TryGetTexture(sourceTextureName!, out texture) && texture is not null)
            {
                failure = string.Empty;
                return true;
            }

            failure = $"Texture '{sourceTextureName}' was not found.";
            return false;
        }

        XRFrameBuffer? sourceFbo = null;
        if (!string.IsNullOrWhiteSpace(sourceFBOName))
        {
            sourceFbo = instance.GetFBO<XRFrameBuffer>(sourceFBOName!);
            if (sourceFbo is null)
            {
                failure = $"Framebuffer '{sourceFBOName}' was not found.";
                return false;
            }
        }
        else
        {
            sourceFbo = instance.RenderState.OutputFBO;
            if (sourceFbo is null)
            {
                failure = "No source texture was specified and the current pipeline output is the window backbuffer.";
                return false;
            }
        }

        if (TryResolveFirstColorTexture(sourceFbo, out texture))
        {
            failure = string.Empty;
            return true;
        }

        string sourceName = sourceFbo.Name ?? sourceFBOName ?? "<unnamed FBO>";
        failure = $"Framebuffer '{sourceName}' does not expose a color texture attachment that can be sampled or read back.";
        return false;
    }

    private static bool TryResolveFirstColorTexture(XRFrameBuffer frameBuffer, out XRTexture? texture)
    {
        texture = null;
        var targets = frameBuffer.Targets;
        if (targets is null)
            return false;

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, _, _) = targets[i];
            if (attachment is < EFrameBufferAttachment.ColorAttachment0 or > EFrameBufferAttachment.ColorAttachment7)
                continue;

            if (target is XRTexture tex)
            {
                texture = tex;
                return true;
            }
        }

        return false;
    }
}