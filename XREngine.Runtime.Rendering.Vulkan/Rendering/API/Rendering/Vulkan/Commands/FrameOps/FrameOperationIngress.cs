namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Short-lived producer ingress. This is the one permitted location for an
/// authoring <see cref="FrameOp"/> array; it is consumed and discarded by
/// <see cref="FrameOperationStream.Lower"/> before planning begins.
/// </summary>
internal sealed class FrameOperationIngress
{
    private FrameOp[] _source = [];
    private int _count;
    private bool _borrowed;

    internal int Count => _count;

    /// <summary>
    /// True when the operations stay owned by the caller: lowering copies them
    /// without consuming their authoring snapshots or input leases, and stores
    /// their contexts without native output-framebuffer fields. OpenXR logical
    /// eye plans use this so the prepared eye operations, which are recorded
    /// later against the acquired images, need not be cloned per submit.
    /// </summary>
    internal bool IsBorrowedLogicalCohort => _borrowed;

    internal void Populate(FrameOp[] source)
        => Populate(source, source?.Length ?? 0);

    internal void Populate(FrameOp[] source, int count)
        => Populate(source, count, borrowedLogicalCohort: false);

    internal void Populate(FrameOp[] source, int count, bool borrowedLogicalCohort)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > source.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _source = source;
        _count = count;
        _borrowed = borrowedLogicalCohort;
    }

    internal FrameOp GetAuthoringOperation(int index) => _source[index];

    /// <summary>Releases producer object references once lowering has copied them.</summary>
    internal void Clear()
    {
        _source = [];
        _count = 0;
        _borrowed = false;
    }
}
