namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    internal VulkanTextureStreamingDiagnosticSnapshot CaptureTextureStreamingDiagnostics()
        => _resourceRuntime.Uploads.CaptureDiagnosticSnapshot();

    internal bool TryQueueDiagnosticTextureStreamingUpload(
        XRTexture2D texture,
        Mipmap2D[] residentMips,
        TextureUploadPriorityClass priority,
        CancellationToken cancellationToken,
        out VulkanTextureStreamingUploadTicket ticket)
    {
        ticket = default;
        if (residentMips is null || residentMips.Length == 0)
            return false;

        Mipmap2D first = residentMips[0];
        TextureStreamingResidentData residentData = new(
            residentMips,
            Math.Max(first.Width, 1u),
            Math.Max(first.Height, 1u),
            Math.Max(first.Width, first.Height));
        if (!ImportedTextureStreamingManager.Instance.TryScheduleRawResidentDataForVulkan(
                texture, residentData, includeMipChain: true, priority, cancellationToken, out long generation) ||
            !_resourceRuntime.Uploads.TryGetLatestTicketForGeneration(texture, generation, out VulkanTextureUploadTicket internalTicket))
        {
            return false;
        }

        ticket = new VulkanTextureStreamingUploadTicket(internalTicket.Sequence, internalTicket.StreamingGeneration);
        return true;
    }

    internal VulkanTextureStreamingTicketSnapshot CaptureTextureStreamingTicket(
        XRTexture2D texture,
        in VulkanTextureStreamingUploadTicket ticket)
        => _resourceRuntime.Uploads.CaptureTicketSnapshot(texture, in ticket);
}
