namespace XREngine.Data;

/// <summary>
/// Calculates the CRC-32 of an Ogg page: polynomial 0x04C11DB7, not reflected, start value zero, no final XOR.
/// The checksum field of the page must be zero when the CRC is calculated.
/// </summary>
internal static class OggCrc
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>Adds bytes to a running CRC.</summary>
    /// <param name="crc">The CRC of the bytes before <paramref name="data"/>, or zero at the start of a page.</param>
    /// <param name="data">The next bytes of the page.</param>
    /// <returns>The CRC after <paramref name="data"/>.</returns>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Table;
        for (int i = 0; i < data.Length; i++)
            crc = (crc << 8) ^ table[(int)((crc >> 24) ^ data[i])];
        return crc;
    }

    /// <summary>Calculates the CRC of a full page and writes it to the checksum field at bytes 22 to 25.</summary>
    /// <param name="page">The full page: header, lacing values, and body.</param>
    public static void Write(Span<byte> page)
    {
        page.Slice(22, 4).Clear();
        uint crc = Update(0u, page);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(22, 4), crc);
    }

    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 0x80000000u) != 0 ? (value << 1) ^ 0x04C11DB7u : value << 1;
            table[i] = value;
        }
        return table;
    }
}
