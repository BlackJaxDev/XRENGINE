namespace XREngine.Rendering;

/// <summary>Optional bounded contact stream. Changing owner or generation cancels all previous captures.</summary>
public interface IRuntimePointerContactSource
{
    object? PointerContactOwner { get; }
    ulong PointerContactSequence { get; }
    ulong PointerContactGeneration { get; }
    int ConsumePointerContacts(Span<WindowPointerContact> destination, out ulong generation);
}
