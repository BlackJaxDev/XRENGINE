namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks the backend state of each dense texture row. The Vulkan runtime writes
/// <see cref="AdvancedTextureRecord.EncodedReferenceIndex"/> into the resident texture
/// table, and, for a source that only changed residency after publication, the width, height
/// and mip count of the bound image. This is backend state: a texture can become
/// descriptor-ready, leave its loading placeholder, or get another resident mip chain without
/// a change to the canonical table generations. The generation from this tracker lets the
/// resident table see such a change.
/// </summary>
internal sealed class VulkanAdvancedTextureReadinessTracker
{
    private RowState[] _rows = [];
    private int _rowCount;

    /// <summary>
    /// Gets the readiness generation. It advances each time <see cref="Update"/> sees a row
    /// whose encoded reference, width, height or mip count differs from the previous update.
    /// Zero means no update yet.
    /// </summary>
    internal ulong Generation { get; private set; }

    /// <summary>Allocates storage for <paramref name="capacity"/> rows. Call outside the frame path.</summary>
    internal void Allocate(int capacity)
    {
        _rows = new RowState[capacity];
        _rowCount = 0;
        Generation = 0u;
    }

    /// <summary>
    /// Compares the backend-prepared rows with the previous update and advances
    /// <see cref="Generation"/> when a row's backend state or the row count changed.
    /// </summary>
    /// <returns><see langword="true"/> when the generation advanced.</returns>
    internal bool Update(ReadOnlySpan<AdvancedTextureRecord> rows)
    {
        if (rows.Length > _rows.Length)
            throw new InvalidOperationException(
                $"Texture readiness rows {rows.Length} exceed the tracker capacity {_rows.Length}.");

        bool changed = Generation == 0u || rows.Length != _rowCount;
        for (int index = 0; index < rows.Length; ++index)
        {
            ref readonly AdvancedTextureRecord row = ref rows[index];
            RowState state = new(row.EncodedReferenceIndex, row.Width, row.Height, row.MipCount);
            if (_rows[index] == state)
                continue;

            _rows[index] = state;
            changed = true;
        }

        _rowCount = rows.Length;
        if (changed)
            Generation = Generation == ulong.MaxValue ? 1u : Generation + 1u;
        return changed;
    }

    private readonly record struct RowState(uint EncodedReferenceIndex, uint Width, uint Height, uint MipCount);
}
