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

    internal int Count => _count;

    internal void Populate(FrameOp[] source)
        => Populate(source, source?.Length ?? 0);

    internal void Populate(FrameOp[] source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > source.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _source = source;
        _count = count;
    }

    internal FrameOp GetAuthoringOperation(int index) => _source[index];

    /// <summary>Releases producer object references once lowering has copied them.</summary>
    internal void Clear()
    {
        _source = [];
        _count = 0;
    }
}
