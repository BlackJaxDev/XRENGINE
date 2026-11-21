namespace XREngine.Rendering;

/// <summary>
/// Names one pooled editor readback. A copied handle can cancel only the same
/// slot generation, so an old callback cannot cancel a later hover request.
/// </summary>
public readonly struct EditorPickingRequest : IDisposable
{
    private readonly XRRenderPipelineInstance? _owner;

    internal EditorPickingRequest(XRRenderPipelineInstance owner, int slotIndex,
        ulong generation)
    {
        _owner = owner;
        SlotIndex = slotIndex;
        Generation = generation;
    }

    internal int SlotIndex { get; }
    public ulong Generation { get; }
    public bool IsPending => _owner?.IsEditorPickingRequestPending(SlotIndex,
        Generation) == true;
    public bool IsActive => _owner?.IsEditorPickingRequestActive(SlotIndex,
        Generation) == true;

    public void Dispose() => _owner?.CancelEditorPickingRequest(SlotIndex,
        Generation);
}
