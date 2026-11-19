using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Carries one GPU auto exposure value from a publishing pipeline instance to consuming
/// pipeline instances that render in the same frame after it. The OpenXR quad view uses it:
/// the wide eye pair meters the scene, and the inset pair copies the wide value, so the
/// inset has no brightness step at its border.
/// </summary>
/// <remarks>
/// The value stays on the GPU. The publisher copies its 1x1 exposure texture into
/// <see cref="Buffer"/> after its exposure update, and each consumer copies the buffer into
/// its own exposure texture. A storage buffer has no image layout, so a consumer needs only
/// a memory barrier, not a layout transition of an image that another pipeline instance owns.
/// </remarks>
public sealed class SharedAutoExposureChannel
{
    private ulong _publishedRenderFrameId;

    /// <summary>Creates the one-float storage buffer that holds the published exposure.</summary>
    public SharedAutoExposureChannel(string name)
    {
        Buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, 1, EComponentType.Float, 1, false, false)
        {
            Usage = EBufferUsage.DynamicCopy,
            DisposeOnPush = false,
            Resizable = false,
            PadEndingToVec4 = false,
        };
        Buffer.SetDataRawAtIndex(0, 0.0f);
    }

    /// <summary>Storage buffer with one float: the exposure that the publisher wrote last.</summary>
    public XRDataBuffer Buffer { get; }

    /// <summary>
    /// Render frame ID of the last successful publish. A consumer copies the value only when
    /// the publisher wrote it in the same render frame.
    /// </summary>
    public ulong PublishedRenderFrameId => Volatile.Read(ref _publishedRenderFrameId);

    /// <summary>Records that the publisher enqueued its copy for this render frame.</summary>
    public void MarkPublished(ulong renderFrameId)
        => Volatile.Write(ref _publishedRenderFrameId, renderFrameId);
}
