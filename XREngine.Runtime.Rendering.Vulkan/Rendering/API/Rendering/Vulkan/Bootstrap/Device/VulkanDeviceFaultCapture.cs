using System.Text;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable cold-path device-fault payload produced by the device authority.
/// Persistence is deliberately a handoff: the authority owns bounded native
/// retrieval and formatting, while the engine logging layer chooses storage.
/// </summary>
internal sealed class VulkanDeviceFaultCapture
{
    private readonly VulkanDeviceFaultArtifact[] _artifacts;

    internal VulkanDeviceFaultCapture(string summary, params VulkanDeviceFaultArtifact[] artifacts)
    {
        Summary = summary;
        _artifacts = artifacts;
    }

    internal string Summary { get; }
    internal ReadOnlySpan<VulkanDeviceFaultArtifact> Artifacts => _artifacts;

    internal void AppendSummary(StringBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(Summary))
            return;

        builder.AppendLine().AppendLine(Summary);
    }
}
