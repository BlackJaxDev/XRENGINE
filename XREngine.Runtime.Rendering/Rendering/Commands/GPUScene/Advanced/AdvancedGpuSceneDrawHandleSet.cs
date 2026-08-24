namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable exact primitive-to-draw-handle mapping for one mesh command.
/// Slot <c>i</c> is primitive <c>i</c>; invalid slots represent unsupported or
/// absent primitives without shifting identity ownership.
/// </summary>
public sealed class AdvancedGpuSceneDrawHandleSet
{
    private readonly AdvancedGpuHandle[] _handles;

    internal AdvancedGpuSceneDrawHandleSet(ReadOnlySpan<AdvancedGpuHandle> handles)
    {
        _handles = handles.ToArray();
    }

    public int Count => _handles.Length;

    public AdvancedGpuHandle Primary => _handles.Length > 0 ? _handles[0] : AdvancedGpuHandle.Invalid;

    public ReadOnlySpan<AdvancedGpuHandle> Handles => _handles;

    public bool Matches(ReadOnlySpan<AdvancedGpuHandle> handles)
        => _handles.AsSpan().SequenceEqual(handles);
}
