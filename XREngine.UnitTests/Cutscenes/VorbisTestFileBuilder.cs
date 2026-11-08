using System.Buffers.Binary;
using XREngine.Data;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// Builds Ogg Vorbis test files from the packets of an existing file. It can repeat a run of the audio packets to make a
/// longer file, and it can write small pages, so that packets cross page edges. No encoder is needed.
/// </summary>
/// <remarks>
/// The repeated run starts and ends at packets with the same block flags as the packets around the original edges, so
/// the window flags in each packet stay true. The granule positions come from the block size of each packet.
/// </remarks>
internal static class VorbisTestFileBuilder
{
    /// <summary>One page of a built file.</summary>
    /// <param name="Offset">The byte offset of the page.</param>
    /// <param name="Granule">The granule position, or -1 if no packet ends on the page.</param>
    /// <param name="IsAudio">True for a page after the header pages.</param>
    public readonly record struct Page(long Offset, long Granule, bool IsAudio);

    /// <summary>Builds a file from the packets of a source file.</summary>
    /// <param name="source">The source Ogg Vorbis file.</param>
    /// <param name="repeat">The number of extra copies of the repeated run of audio packets.</param>
    /// <param name="maxBodyBytes">The largest body of an audio page in bytes.</param>
    /// <param name="pages">Receives each page of the new file.</param>
    public static byte[] Build(byte[] source, int repeat, int maxBodyBytes, out List<Page> pages)
    {
        List<byte[]> packets = ReadPackets(source, out long sourceFinalGranule, out uint serial);
        byte[] id = packets[0];
        byte[] comment = packets[1];
        byte[] setup = packets[2];
        List<byte[]> audio = packets.GetRange(3, packets.Count - 3);

        if (!VorbisSetupHeader.TryReadIdentification(id, out _, out _, out int shortBlock, out int longBlock))
            throw new InvalidDataException("The source identification header is not valid.");
        bool[] modeFlags = new bool[VorbisSetupHeader.MaxModes];
        if (!VorbisSetupHeader.TryReadModeBlockFlags(setup, modeFlags, out int modeCount))
            throw new InvalidDataException("The source setup header has no modes.");
        int modeMask = (1 << VorbisSetupHeader.BitCount(modeCount - 1)) - 1;
        int BlockOf(byte[] packet) => modeFlags[(packet[0] >> 1) & modeMask] ? longBlock : shortBlock;

        // The run from packet 1 to packet end - 1 repeats. Find an end whose block flags match the edges of the run.
        int runStart = 1;
        int runEnd = audio.Count - 1;
        while (runEnd > runStart + 1 && (BlockOf(audio[runEnd - 1]) != BlockOf(audio[runStart - 1]) || BlockOf(audio[runEnd]) != BlockOf(audio[runStart])))
            runEnd--;

        List<byte[]> sequence = [.. audio.GetRange(0, runEnd)];
        for (int r = 0; r < repeat; r++)
            sequence.AddRange(audio.GetRange(runStart, runEnd - runStart));
        sequence.AddRange(audio.GetRange(runEnd, audio.Count - runEnd));

        long sourceEnd = EndPositions(audio, BlockOf)[^1];
        long[] ends = EndPositions(sequence, BlockOf);
        long finalGranule = ends[^1] - (sourceEnd - sourceFinalGranule);

        using MemoryStream output = new();
        pages = [];
        uint pageSequence = 0;
        WritePackets(output, serial, ref pageSequence, [id], 0x02, 255 * 255, null, -1, pages, false);
        WritePackets(output, serial, ref pageSequence, [comment, setup], 0, 255 * 255, null, -1, pages, false);
        WritePackets(output, serial, ref pageSequence, sequence, 0, maxBodyBytes, ends, finalGranule, pages, true);
        return output.ToArray();
    }

    /// <summary>Splits an Ogg file into its packets.</summary>
    public static List<byte[]> ReadPackets(byte[] ogg, out long finalGranule, out uint serial)
    {
        List<byte[]> packets = [];
        List<byte> current = [];
        int offset = 0;
        finalGranule = -1;
        serial = 0;
        while (offset < ogg.Length)
        {
            int segments = ogg[offset + 26];
            long granule = BinaryPrimitives.ReadInt64LittleEndian(ogg.AsSpan(offset + 6, 8));
            serial = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(offset + 14, 4));
            if (granule != -1)
                finalGranule = granule;
            int body = offset + 27 + segments;
            for (int s = 0; s < segments; s++)
            {
                int lacing = ogg[offset + 27 + s];
                current.AddRange(ogg.AsSpan(body, lacing).ToArray());
                body += lacing;
                if (lacing < 255)
                {
                    packets.Add([.. current]);
                    current.Clear();
                }
            }
            offset = body;
        }
        return packets;
    }

    private static long[] EndPositions(List<byte[]> audio, Func<byte[], int> blockOf)
    {
        long[] ends = new long[audio.Count];
        long position = 0;
        for (int i = 1; i < audio.Count; i++)
        {
            position += blockOf(audio[i - 1]) / 4 + blockOf(audio[i]) / 4;
            ends[i] = position;
        }
        return ends;
    }

    // Writes packets on pages. A page ends when its body would pass the limit or it has 255 lacing values. The last page of
    // the audio gets the end-of-stream flag and the final granule position.
    private static void WritePackets(
        MemoryStream output,
        uint serial,
        ref uint pageSequence,
        List<byte[]> packets,
        byte firstFlags,
        int maxBodyBytes,
        long[]? ends,
        long finalGranule,
        List<Page> pages,
        bool isAudio)
    {
        List<byte> lacing = [];
        List<byte> body = [];
        long granule = -1;
        bool continued = false;
        byte flags = firstFlags;

        for (int p = 0; p < packets.Count; p++)
        {
            byte[] packet = packets[p];
            int done = 0;
            while (true)
            {
                int segment = Math.Min(255, packet.Length - done);
                if (lacing.Count == 255 || (lacing.Count > 0 && body.Count + segment > maxBodyBytes))
                {
                    bool nextContinued = done > 0;
                    Flush(output, serial, ref pageSequence, (byte)(flags | (continued ? 0x01 : 0)), granule, lacing, body, pages, isAudio);
                    flags = 0;
                    granule = -1;
                    continued = nextContinued;
                }

                lacing.Add((byte)segment);
                body.AddRange(packet.AsSpan(done, segment).ToArray());
                done += segment;
                if (segment < 255)
                    break;
            }

            granule = ends is null ? 0 : (p == packets.Count - 1 ? finalGranule : ends[p]);
        }

        byte last = (byte)(flags | (continued ? 0x01 : 0) | (isAudio ? 0x04 : 0));
        Flush(output, serial, ref pageSequence, last, granule, lacing, body, pages, isAudio);
    }

    private static void Flush(MemoryStream output, uint serial, ref uint pageSequence, byte flags, long granule, List<byte> lacing, List<byte> body, List<Page> pages, bool isAudio)
    {
        byte[] page = new byte[27 + lacing.Count + body.Count];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18, 4), pageSequence++);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + lacing.Count);
        OggCrc.Write(page);
        pages.Add(new Page(output.Position, granule, isAudio));
        output.Write(page);
        lacing.Clear();
        body.Clear();
    }
}
