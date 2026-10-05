namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A utility struct for computing a stable hash signature for frame operations
/// from values of various data types, including strings.
/// </summary>
internal struct FrameOpSignatureHasher
{
    /// <summary>
    /// The offset basis used for the FNV-1a hash algorithm, which is a large prime number.
    /// </summary>
    private const ulong OffsetBasis = 14695981039346656037UL;
    /// <summary>
    /// The prime number used for mixing in the FNV-1a hash algorithm, which helps to reduce collisions.
    /// </summary>
    private const ulong Prime = 1099511628211UL;
    /// <summary>
    /// The current hash value being computed, which is updated as new values are added to the hasher.
    /// </summary>
    private ulong _value;

    public FrameOpSignatureHasher()
    {
        // Initialize the hash value to the offset basis, which is a standard starting point for FNV-1a hashing.
        _value = OffsetBasis;
    }

    /// <summary>
    /// Adds a boolean value to the hash computation by converting it to an integer (1 for true, 0 for false) 
    /// and mixing it into the current hash value.
    /// </summary>
    /// <param name="value">The boolean value to add to the hash computation.</param>
    public void Add(bool value) => Add(value ? 1 : 0);
    /// <summary>
    /// Adds an integer value to the hash computation by mixing it into the current hash value. 
    /// The integer is treated as an unsigned integer for mixing purposes.
    /// </summary>
    /// <param name="value">The integer value to add to the hash computation.</param>
    public void Add(int value) => Mix(unchecked((uint)value));
    /// <summary>
    /// Adds an unsigned integer value to the hash computation by mixing it into the current hash value.
    /// </summary>
    /// <param name="value">The unsigned integer value to add to the hash computation.</param>
    public void Add(uint value) => Mix(value);
    /// <summary>
    /// Adds an unsigned long integer value to the hash computation by mixing it into the current hash value.
    /// </summary>
    /// <param name="value">The unsigned long integer value to add to the hash computation.</param>
    public void Add(ulong value) => Mix(value);
    /// <summary>
    /// Adds a floating-point value to the hash computation by converting it to its bit representation as an unsigned integer
    /// and mixing it into the current hash value. This ensures that the hash reflects the exact binary representation of the float, which is important for stability and uniqueness.
    /// </summary>
    /// <param name="value">The floating-point value to add to the hash computation.</param>
    public void Add(float value) => Add(BitConverter.SingleToUInt32Bits(value));
    /// <summary>
    /// Adds a string value to the hash computation by first checking if it is null. 
    /// If it is null, a sentinel value (-1) is added to the hash. 
    /// If it is not null, the length of the string is added to the hash, 
    /// followed by a stable signature of its characters, so equal strings always
    /// produce the same hash signature regardless of which instance holds them.
    /// </summary>
    /// <param name="value">The string value to add to the hash computation.</param>
    public void Add(string? value)
    {
        // If the string is null, add a sentinel value to the hash to represent null strings.
        if (value is null)
        {
            Add(-1);
            return;
        }

        // Add the length of the string to the hash to differentiate between strings of different lengths.
        Add(value.Length);

        // Computed on every call rather than cached: for the short identifiers hashed here it costs
        // about what a lookup did, and a shared cache keyed by string instance thrashed and contended
        // on its locks whenever a scene held many equal strings in separate instances, such as the
        // uniform names of materials restored from a play-mode snapshot.
        Add(ComputeStableStringSignature(value));
    }

    /// <summary>
    /// Returns the computed hash value as an unsigned long integer.
    /// </summary>
    /// <returns>The computed hash value as an unsigned long integer.</returns>
    public readonly ulong ToHash() => _value;

    /// <summary>
    /// Computes a stable signature for a given string value by iterating over its characters, 
    /// converting each character to its unsigned integer representation, 
    /// and mixing it into the hash computation.
    /// </summary>
    /// <param name="value">The string value for which to compute a stable signature.</param>
    /// <returns>The computed stable signature as an unsigned long integer.</returns>
    private static ulong ComputeStableStringSignature(string value)
    {
        // Create a new instance of the FrameOpSignatureHasher to compute the hash for the string.
        FrameOpSignatureHasher hash = new();

        // Add the length of the string to the hash to differentiate between strings of different lengths.
        hash.Add(value.Length);

        // Iterate over each character in the string, convert it to its unsigned integer representation, and mix it into the hash computation.
        for (int i = 0; i < value.Length; i++)
            hash.Add((uint)value[i]);

        // Return the computed hash value as an unsigned long integer.
        return hash.ToHash();
    }

    /// <summary>
    /// Mixes an unsigned long integer value into the current hash computation using the FNV-1a hash algorithm. 
    /// This involves XORing the current hash value with the input value, 
    /// multiplying by a prime number, 
    /// and then XORing with the input value shifted right by 32 bits.
    /// </summary>
    /// <param name="value"></param>
    private void Mix(ulong value)
    {
        unchecked
        {
            _value ^= value;
            _value *= Prime;
            _value ^= value >> 32;
            _value *= Prime;
        }
    }
}
