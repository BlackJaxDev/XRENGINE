namespace XREngine.Rendering;

/// <summary>Tracks ownership of one reusable editor pixel readback slot.</summary>
internal enum EEditorPickingRequestState : byte
{
    Free,
    Pending,
    Delivering,
}
