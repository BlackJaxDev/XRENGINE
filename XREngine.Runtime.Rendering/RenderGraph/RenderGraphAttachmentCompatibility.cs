using System.Text;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Builds attachment signatures and decides whether a pass can join a batch. The rules depend only on pass
/// metadata, so every backend and every tool uses the same batches.
/// </summary>
public static class RenderGraphAttachmentCompatibility
{
    /// <summary>
    /// Returns true when <paramref name="candidate"/> can extend <paramref name="existingBatch"/>: both are graphics
    /// passes and their attachment signatures are equal.
    /// </summary>
    public static bool AreCompatible(
        RenderGraphPassBatch existingBatch,
        RenderPassMetadata candidate,
        string candidateSignature)
        => existingBatch.Stage == ERenderGraphPassStage.Graphics &&
           candidate.Stage == ERenderGraphPassStage.Graphics &&
           string.Equals(existingBatch.AttachmentSignature, candidateSignature, StringComparison.Ordinal);

    /// <summary>
    /// Builds the attachment signature of <paramref name="pass"/>: each attachment usage with its type, name, load and
    /// store operations, and resolve source, sorted by name and type. A pass with no attachments returns <c>none</c>.
    /// Compilation calls it only when the metadata changes.
    /// </summary>
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
