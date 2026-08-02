using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen inheritance identity required to execute a recorded secondary.
/// </summary>
internal readonly record struct VulkanRecordedCommandInheritance(
    bool DynamicRendering,
    RenderPass RenderPass,
    Framebuffer Framebuffer,
    DynamicRenderingFormatSignature DynamicRenderingFormats,
    bool DepthStencilReadOnly,
    SampleCountFlags Samples,
    DynamicRenderingLocalReadSignature LocalReadSignature = default,
    RenderingFlags RenderingFlags = 0)
{
    internal ulong ComputeIdentity()
    {
        FrameOpSignatureHasher identity = new();
        identity.Add(DynamicRendering);
        identity.Add(RenderPass.Handle);
        identity.Add(Framebuffer.Handle);
        identity.Add(DynamicRenderingFormats.ColorAttachmentCount);
        for (uint i = 0; i < DynamicRenderingFormats.ColorAttachmentCount; i++)
            identity.Add((int)DynamicRenderingFormats.GetColorAttachmentFormat(i));
        identity.Add((int)DynamicRenderingFormats.DepthAttachmentFormat);
        identity.Add((int)DynamicRenderingFormats.StencilAttachmentFormat);
        identity.Add(DynamicRenderingFormats.ViewMask);
        identity.Add(DynamicRenderingFormats.LayerCount);
        identity.Add(DepthStencilReadOnly);
        identity.Add((uint)Samples);
        identity.Add(LocalReadSignature.ColorAttachmentLocationCount);
        for (int i = 0;
             i < LocalReadSignature.ColorAttachmentLocationCount;
             i++)
        {
            identity.Add(LocalReadSignature.GetColorAttachmentLocation(i));
        }

        identity.Add(LocalReadSignature.ColorInputAttachmentIndexCount);
        for (int i = 0;
             i < LocalReadSignature.ColorInputAttachmentIndexCount;
             i++)
        {
            identity.Add(LocalReadSignature.GetColorInputAttachmentIndex(i));
        }

        identity.Add(LocalReadSignature.DepthInputAttachmentIndex.HasValue);
        identity.Add(
            LocalReadSignature.DepthInputAttachmentIndex.GetValueOrDefault());
        identity.Add(LocalReadSignature.StencilInputAttachmentIndex.HasValue);
        identity.Add(
            LocalReadSignature.StencilInputAttachmentIndex.GetValueOrDefault());
        identity.Add((uint)RenderingFlags);
        return identity.ToHash();
    }
}
