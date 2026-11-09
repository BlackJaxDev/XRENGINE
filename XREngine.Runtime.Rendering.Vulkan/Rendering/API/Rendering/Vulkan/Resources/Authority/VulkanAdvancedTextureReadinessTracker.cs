namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks the backend readiness of each dense texture row. The Vulkan runtime writes
/// <see cref="AdvancedTextureRecord.EncodedReferenceIndex"/> into the resident texture
/// table, but readiness is backend state: a texture can become descriptor-ready, or leave its
/// loading placeholder, without a change to the canonical table generations. The generation
/// from this tracker lets the resident table see such a change.
/// </summary>
internal sealed class VulkanAdvancedTextureReadinessTracker
{
    private uint[] _encodedReferenceIndices = [];
    private int _rowCount;

    /// <summary>
    /// Gets the readiness generation. It advances each time <see cref="Update"/> sees a row
    /// whose encoded reference differs from the previous update. Zero means no update yet.
    /// </summary>
    internal ulong Generation { get; private set; }

    /// <summary>Allocates storage for <paramref name="capacity"/> rows. Call outside the frame path.</summary>
    internal void Allocate(int capacity)
    {
        _encodedReferenceIndices = new uint[capacity];
        _rowCount = 0;
        Generation = 0u;
    }

    /// <summary>
    /// Compares the backend-prepared rows with the previous update and advances
    /// <see cref="Generation"/> when a row's encoded reference or the row count changed.
    /// </summary>
    /// <returns><see langword="true"/> when the generation advanced.</returns>
    internal bool Update(ReadOnlySpan<AdvancedTextureRecord> rows)
    {
        if (rows.Length > _encodedReferenceIndices.Length)
            throw new InvalidOperationException(
                $"Texture readiness rows {rows.Length} exceed the tracker capacity {_encodedReferenceIndices.Length}.");

        bool changed = Generation == 0u || rows.Length != _rowCount;
        for (int index = 0; index < rows.Length; ++index)
        {
            uint encoded = rows[index].EncodedReferenceIndex;
            if (_encodedReferenceIndices[index] == encoded)
                continue;

            _encodedReferenceIndices[index] = encoded;
            changed = true;
        }

        _rowCount = rows.Length;
        if (changed)
            Generation = Generation == ulong.MaxValue ? 1u : Generation + 1u;
        return changed;
    }
}
