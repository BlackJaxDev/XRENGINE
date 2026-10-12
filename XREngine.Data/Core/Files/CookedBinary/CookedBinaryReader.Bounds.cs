namespace XREngine.Core.Files;

public sealed unsafe partial class CookedBinaryReader
{
    /// <summary>Validates a collection's minimum wire footprint before its count controls allocation or iteration.</summary>
    public int ReadCollectionCount(int minimumBytesPerElement = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumBytesPerElement);
        int count = ReadInt32();
        CookedBinaryReadBudget.ValidateCount(count);
        if (count > Remaining / minimumBytesPerElement)
            throw new InvalidDataException("CookedBinary.TruncatedCollection: collection count exceeds the remaining encoded elements.");
        return count;
    }

    internal int ReadBitArrayByteCount(out int bitLength)
    {
        bitLength = ReadInt32();
        if (bitLength < 0)
            throw new InvalidDataException("CookedBinary.InvalidLength: negative bit-array length.");
        // Divide before adding rather than overflowing an attacker-controlled length + 7.
        int byteCount = bitLength / 8 + (bitLength % 8 == 0 ? 0 : 1);
        EnsureAvailable(byteCount);
        CookedBinaryReadBudget.Reserve(byteCount);
        return byteCount;
    }
}
