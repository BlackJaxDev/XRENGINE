namespace XREngine;

/// <summary>
/// Stable runtime output/attachment contract used to decide whether views can
/// share command construction. Per-frame image indices and camera matrices are
/// deliberately excluded.
/// </summary>
public readonly record struct RenderFrameViewTargetDescriptor(
    ERenderLibrary Backend,
    ulong OutputIdentity,
    ulong SwapchainIdentity,
    ulong AttachmentSignature,
    ulong FormatIdentity,
    uint SampleCount,
    uint LayerCount,
    bool SupportsColorAttachmentLayout,
    bool SupportsTransferDestinationLayout,
    ulong ResourceGeneration,
    ulong TemporalGeneration)
{
    public bool IsSpecified => OutputIdentity != 0UL;
}
