namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>Captures the real renderer-scoped imported-upload counters.</summary>
    public VulkanTextureStreamingDiagnosticSnapshot GetTextureStreamingDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.CaptureTextureStreamingDiagnostics();
    }

    /// <summary>
    /// Queues retained immutable mip payload through the normal imported-texture
    /// streaming manager. The returned ticket identifies real service work.
    /// </summary>
    public bool TryQueueTextureStreamingDiagnosticUpload(
        XRTexture2D texture,
        Mipmap2D[] residentMips,
        TextureUploadPriorityClass priority,
        CancellationToken cancellationToken,
        out VulkanTextureStreamingUploadTicket ticket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var rendererScope = AbstractRenderer.PushThreadCurrent(_renderer);
        using var creationOwner = GenericRenderObject.PushApiWrapperCreationOwner(_renderer);
        return _renderer.TryQueueDiagnosticTextureStreamingUpload(
            texture, residentMips, priority, cancellationToken, out ticket);
    }

    public VulkanTextureStreamingTicketSnapshot GetTextureStreamingTicketStatus(
        XRTexture2D texture,
        in VulkanTextureStreamingUploadTicket ticket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texture);
        return _renderer.CaptureTextureStreamingTicket(texture, in ticket);
    }
}
