namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Short-lived producer ingress. This is the one permitted location for an
/// authoring <see cref="FrameOp"/> array; it is consumed and discarded by
/// <see cref="FrameOperationStream.Lower"/> before planning begins.
/// </summary>
internal sealed class FrameOperationIngress
{
    private FrameOp[] _source = [];

    internal int Count => _source.Length;

    internal void Populate(FrameOp[] source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    internal FrameOp GetAuthoringOperation(int index) => _source[index];

    /// <summary>Releases producer object references once lowering has copied them.</summary>
    internal void Clear() => _source = [];
}
