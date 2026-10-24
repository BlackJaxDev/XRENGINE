namespace XREngine.Rendering.UI;

/// <summary>Owns one immutable contiguous painter-order span of a composition layer.
/// Keep the parent publication retained while reading its draw resources.</summary>
public sealed class UICompositionSegment
{
    private readonly UIRenderRecord[] _records;

    internal UICompositionSegment(ulong identity, ulong revision, UICompositionOwner owner,
        ReadOnlySpan<UIRenderRecord> records)
    {
        Identity = identity;
        Revision = revision;
        Owner = owner;
        _records = records.ToArray();
    }

    public ulong Identity { get; }
    public ulong Revision { get; }
    public UICompositionOwner Owner { get; }
    public ReadOnlySpan<UIRenderRecord> Records => _records;
}
