namespace XREngine.Data.Runtime.Collections;

/// <summary>
/// A high-performance bit set implementation that uses an array of 64-bit words to store bits efficiently.
/// </summary>
/// <param name="bitCapacity">The initial bit capacity of the bit set.</param>
public sealed class HotBitSet(int bitCapacity = 0)
{
    /// <summary>
    /// Gets the underlying array of 64-bit words used to store the bits.
    /// </summary>
    private ulong[] _words = bitCapacity > 0 ? new ulong[WordCount(bitCapacity)] : [];

    /// <summary>
    /// Gets the capacity of the bit set in terms of 64-bit words.
    /// </summary>
    public int WordCapacity => _words.Length;

    /// <summary>
    /// Ensures that the bit set has at least the specified bit capacity, resizing if necessary.
    /// </summary>
    /// <param name="bitCapacity">The minimum bit capacity to ensure.</param>
    public void EnsureBitCapacity(int bitCapacity)
    {
        int wordCount = WordCount(bitCapacity);
        if (wordCount > _words.Length)
            Array.Resize(ref _words, wordCount);
    }

    /// <summary>
    /// Gets the value of the bit at the specified index.
    /// </summary>
    /// <param name="bitIndex">The index of the bit to retrieve.</param>
    /// <returns>True if the bit is set; otherwise, false.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the bit index is negative.</exception>
    public bool Get(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        int wordIndex = bitIndex >> 6;
        if (wordIndex >= _words.Length)
            return false;

        ulong mask = 1UL << (bitIndex & 63);
        return (_words[wordIndex] & mask) != 0;
    }

    /// <summary>
    /// Sets the bit at the specified index to true.
    /// </summary>
    /// <param name="bitIndex">The index of the bit to set.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the bit index is negative.</exception>
    public void Set(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        EnsureBitCapacity(bitIndex + 1);
        _words[bitIndex >> 6] |= 1UL << (bitIndex & 63);
    }

    /// <summary>
    /// Clears the bit at the specified index (sets it to false).
    /// </summary>
    /// <param name="bitIndex">The index of the bit to clear.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the bit index is negative.</exception>
    public void Clear(int bitIndex)
    {
        if (bitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(bitIndex));

        int wordIndex = bitIndex >> 6;
        if (wordIndex >= _words.Length)
            return;

        _words[wordIndex] &= ~(1UL << (bitIndex & 63));
    }

    /// <summary>
    /// Clears all bits in the bit set (sets them to false).
    /// </summary>
    public void ClearAll()
        => Array.Clear(_words);

    /// <summary>
    /// Gets a read-only span representing the underlying 64-bit words of the bit set.
    /// </summary>
    public ReadOnlySpan<ulong> Words => _words;

    /// <summary>
    /// Calculates the number of 64-bit words required to store the specified number of bits.
    /// </summary>
    /// <param name="bitCount">The number of bits.</param>
    /// <returns>The number of 64-bit words required.</returns>
    private static int WordCount(int bitCount)
        => bitCount <= 0 ? 0 : ((bitCount + 63) >> 6);
}
