using System.Buffers.Binary;

namespace XREngine.Data;

/// <summary>
/// Reads the fields of the Vorbis header packets that give the length of each audio packet: the two block sizes from
/// the identification header and the block flag of each mode from the setup header.
/// </summary>
/// <remarks>
/// The modes are the last fields of the setup header, before the framing bit. Each mode has 41 bits: a block flag,
/// a window type and a transform type of 16 bits that must be zero, and a mapping number of 8 bits. The fields before
/// the modes have variable lengths, so this reader finds the modes from the end of the packet. This is the method that
/// common Ogg tools use. A false match is possible in theory, so the page index checks the packet lengths that it
/// calculates against the granule position of each page and is not used if one does not agree.
/// </remarks>
internal static class VorbisSetupHeader
{
    /// <summary>The largest number of modes in a Vorbis stream.</summary>
    public const int MaxModes = 64;

    private const int ModeBits = 41;

    /// <summary>Reads the identification header.</summary>
    /// <param name="packet">The first header packet.</param>
    /// <param name="channels">The channel count.</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="shortBlock">The size of a short block in samples.</param>
    /// <param name="longBlock">The size of a long block in samples.</param>
    /// <returns>True if the packet is a valid identification header.</returns>
    public static bool TryReadIdentification(ReadOnlySpan<byte> packet, out int channels, out int sampleRate, out int shortBlock, out int longBlock)
    {
        channels = 0;
        sampleRate = 0;
        shortBlock = 0;
        longBlock = 0;
        if (packet.Length < 30 || packet[0] != 1 || !IsVorbisSignature(packet))
            return false;

        channels = packet[11];
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4));
        int shortExponent = packet[28] & 0x0F;
        int longExponent = packet[28] >> 4;
        if (channels <= 0 || sampleRate <= 0 || shortExponent < 6 || longExponent > 13 || shortExponent > longExponent)
            return false;

        shortBlock = 1 << shortExponent;
        longBlock = 1 << longExponent;
        return true;
    }

    /// <summary>True if the packet starts with a packet type byte and the "vorbis" signature.</summary>
    public static bool IsVorbisSignature(ReadOnlySpan<byte> packet)
        => packet.Length >= 7 && packet[1] == (byte)'v' && packet[2] == (byte)'o' && packet[3] == (byte)'r'
            && packet[4] == (byte)'b' && packet[5] == (byte)'i' && packet[6] == (byte)'s';

    /// <summary>Reads the block flag of each mode from the end of the setup header.</summary>
    /// <param name="packet">The third header packet.</param>
    /// <param name="blockFlags">Receives the block flag of each mode. It must hold <see cref="MaxModes"/> values.</param>
    /// <param name="modeCount">The number of modes.</param>
    /// <returns>True if a valid mode list was found.</returns>
    public static bool TryReadModeBlockFlags(ReadOnlySpan<byte> packet, Span<bool> blockFlags, out int modeCount)
    {
        modeCount = 0;
        if (packet.Length < 8 || packet[0] != 5 || !IsVorbisSignature(packet) || blockFlags.Length < MaxModes)
            return false;

        long framingBit = FindLastSetBit(packet);
        if (framingBit < 0)
            return false;

        // Count the valid modes back from the framing bit. A count is a candidate if the 6-bit field before its first
        // mode holds that count minus one. The largest candidate wins, as in other Ogg tools.
        for (int count = 1; count <= MaxModes; count++)
        {
            long start = framingBit - (long)ModeBits * count;
            if (start < 6 + 7 * 8)
                break;
            if (ReadBits(packet, start + 1, 16) != 0 || ReadBits(packet, start + 17, 16) != 0 || ReadBits(packet, start + 33, 8) >= MaxModes)
                break;
            if (ReadBits(packet, start - 6, 6) == count - 1)
                modeCount = count;
        }

        if (modeCount == 0)
            return false;

        for (int mode = 0; mode < modeCount; mode++)
            blockFlags[mode] = ReadBits(packet, framingBit - (long)ModeBits * (modeCount - mode), 1) != 0;
        return true;
    }

    /// <summary>The number of bits that hold a value from zero to <paramref name="value"/>.</summary>
    public static int BitCount(int value)
    {
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }

    private static long FindLastSetBit(ReadOnlySpan<byte> packet)
    {
        for (int i = packet.Length - 1; i >= 0; i--)
        {
            int value = packet[i];
            if (value == 0)
                continue;
            int bit = 7;
            while ((value & (1 << bit)) == 0)
                bit--;
            return (long)i * 8 + bit;
        }
        return -1;
    }

    // Vorbis packs the bits of a field from the low bit of each byte up.
    private static int ReadBits(ReadOnlySpan<byte> packet, long position, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            long bit = position + i;
            value |= ((packet[(int)(bit >> 3)] >> (int)(bit & 7)) & 1) << i;
        }
        return value;
    }
}
