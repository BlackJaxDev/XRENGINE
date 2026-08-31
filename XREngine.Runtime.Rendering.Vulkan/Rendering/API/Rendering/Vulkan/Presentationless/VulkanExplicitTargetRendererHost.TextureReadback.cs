namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>Observes an already-published RGBA8 texture without creating or uploading it.</summary>
    public bool TryDescribeCurrentNativeTexture(
        XRTexture2D texture,
        out VulkanNativeTextureDiagnosticDescription description)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryDescribeExplicitNativeTexture(texture, out description);
    }

    /// <summary>
    /// Reads a bounded row band of the exact current texture generation. This
    /// synchronous diagnostic copy must stay outside production/performance
    /// intervals; it neither uploads a missing texture nor feeds rendering.
    /// </summary>
    public bool TryReadbackTextureMipRows(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        XRTexture2D texture,
        in VulkanNativeTextureDiagnosticDescription expected,
        uint mipLevel,
        int firstRow,
        int rowCount,
        out byte[] rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryReadbackExplicitTextureMipRows(
            in receipt, texture, in expected, mipLevel, firstRow, rowCount, out rgba);
    }
}
