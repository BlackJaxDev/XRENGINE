namespace XREngine.Animation.Importers;

/// <summary>Reusable allocation-free event occurrence buffer for playback hot paths.</summary>
public sealed class ImportedAnimationEventBuffer
{
    private ImportedAnimationEventOccurrence[] _items = [];

    public int Count { get; private set; }
    public ReadOnlySpan<ImportedAnimationEventOccurrence> Items => _items.AsSpan(0, Count);

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;
        Array.Resize(ref _items, capacity);
    }

    public void Clear() => Count = 0;

    public void Add(in ImportedAnimationEventOccurrence occurrence)
    {
        if (Count == _items.Length)
            Array.Resize(ref _items, Math.Max(4, _items.Length * 2));
        _items[Count++] = occurrence;
    }
}
