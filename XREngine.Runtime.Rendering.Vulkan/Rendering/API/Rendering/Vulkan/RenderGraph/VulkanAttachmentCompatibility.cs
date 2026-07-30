using System.Text;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns graphics-attachment identity and compiled-pass batch compatibility.
/// </summary>
internal static class VulkanAttachmentCompatibility
{
    public static bool AreCompatible(
        VulkanCompiledPassBatch existingBatch,
        RenderPassMetadata candidate,
        string candidateSignature)
        => existingBatch.Stage == ERenderGraphPassStage.Graphics &&
           candidate.Stage == ERenderGraphPassStage.Graphics &&
           string.Equals(existingBatch.AttachmentSignature, candidateSignature, StringComparison.Ordinal);

    public static string BuildSignature(RenderPassMetadata pass)
    {
        var builder = new StringBuilder();
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages
            .Where(static usage => usage.IsAttachment)
            .OrderBy(static usage => usage.ResourceName, StringComparer.Ordinal)
            .ThenBy(static usage => usage.ResourceType))
        {
            if (builder.Length > 0)
                builder.Append('|');
            builder.Append(usage.ResourceType).Append(':').Append(usage.ResourceName)
                .Append(':').Append(usage.LoadOp).Append(':').Append(usage.StoreOp)
                .Append(":resolve=").Append(usage.ResolveSourceColorIndex?.ToString() ?? "none");
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }
}
