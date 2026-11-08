using System.Buffers.Binary;

namespace XREngine.Data;

/// <summary>
/// An index of the Ogg pages of one Vorbis stream, so a seek can start the decode near its target and not at the start of the file.
/// </summary>
/// <remarks>
/// <para>
/// The build reads every page once. It checks the CRC, the stream serial, and the sequence numbers, and it finds where each
/// audio packet starts and ends. It reads the mode of each packet from its first byte and gets the packet length from the
/// block sizes: a packet gives a quarter of the previous block plus a quarter of its own block, and the first packet gives
/// no samples. The build checks this sum against the granule position of each page, which is the end frame of the last
/// packet that ends on the page. A stream that does not agree, a stream with a start offset, a chained stream, or a damaged
/// page gives no index, and the decoder then decodes from the start of the file.
/// </para>
/// <para>
/// An entry is a page on which a packet starts. A decode that starts at that packet loses only the samples of that packet,
/// because the first packet of a decode has nothing to overlap with. That first packet is the full block of pre-roll that
/// the overlap of the next packet needs, so the decode is exact from <see cref="VorbisPageIndexEntry.RestartFrame"/> on.
/// </para>
/// <para>
/// Memory bound: the index keeps at most <see cref="MaxEntries"/> entries of 32 bytes (32 KB). When the entry array is
/// full, the build keeps every second entry and doubles <see cref="PageStride"/>, so entries are between
/// <see cref="PageStride"/> and about two times <see cref="PageStride"/> pages apart. The index also keeps one buffer with
/// new header pages (the identification header, an empty comment header, and the setup header) and space for the largest
/// audio page of the file (at most <see cref="MaxPageBytes"/> bytes; about 4 to 8 KB for libvorbis files). The original
/// comment header is not kept, so embedded cover art does not add to the size.
/// </para>
/// <para>
/// A page whose sequence number is smaller than the number of new header pages gets no entry, because the header pages
/// take the sequence numbers just before the restart page. This loses at most the first audio pages and the pages just
/// after a wrap of the 32-bit sequence number.
/// </para>
/// </remarks>
internal sealed class VorbisPageIndex
{
    /// <summary>The largest number of entries.</summary>
    public const int MaxEntries = 1024;

    /// <summary>The largest size of an Ogg page: the header, 255 lacing values, and 255 segments of 255 bytes.</summary>
    public const int MaxPageBytes = PageHeaderBytes + 255 + 255 * 255;

    private const int PageHeaderBytes = 27;
    private const int MaxSetupBytes = 1 << 20;
    private const byte FlagContinued = 0x01;
    private const byte FlagBeginOfStream = 0x02;
    private const byte FlagEndOfStream = 0x04;

    private static readonly byte[] EmptyCommentPacket = [3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0, 1];

    private readonly VorbisPageIndexEntry[] _entries;
    private readonly byte[] _prefix;
    private readonly int _headerLength;
    private readonly int[] _headerPageStarts;
    private readonly int[] _headerPageLengths;

    private VorbisPageIndex(
        VorbisPageIndexEntry[] entries,
        byte[] prefix,
        int headerLength,
        int[] headerPageStarts,
        int[] headerPageLengths,
        uint serial,
        long finalGranule,
        int pageCount,
        int pageStride)
    {
        _entries = entries;
        _prefix = prefix;
        _headerLength = headerLength;
        _headerPageStarts = headerPageStarts;
        _headerPageLengths = headerPageLengths;
        Serial = serial;
        FinalGranule = finalGranule;
        PageCount = pageCount;
        PageStride = pageStride;
    }

    /// <summary>The serial number of the logical stream.</summary>
    public uint Serial { get; }

    /// <summary>The granule position of the last page, which is the frame count of the stream.</summary>
    public long FinalGranule { get; }

    /// <summary>The number of pages in the file.</summary>
    public int PageCount { get; }

    /// <summary>The smallest number of pages between two entries.</summary>
    public int PageStride { get; }

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Length;

    /// <summary>The entries in the order of their frames. A test can change an entry to check the restart checks.</summary>
    internal VorbisPageIndexEntry[] Entries => _entries;

    /// <summary>Finds the entry with the largest restart frame that is not after the target.</summary>
    /// <param name="frame">The target frame.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="index">The index of the entry, or -1.</param>
    /// <returns>False if the target is before the first entry.</returns>
    public bool TryFind(long frame, out VorbisPageIndexEntry entry, out int index)
    {
        int low = 0;
        int high = _entries.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (_entries[middle].RestartFrame <= frame)
            {
                found = middle;
                low = middle + 1;
            }
            else
                high = middle - 1;
        }

        entry = found >= 0 ? _entries[found] : default;
        index = found;
        return found >= 0;
    }

    /// <summary>
    /// Reads the page of an entry, checks that it is the page that the build saw, and sets the splice stream to give the
    /// header pages, the restart page without the end of the earlier packet, and then the file from the next page on.
    /// </summary>
    /// <param name="stream">The file stream.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="splice">The stream to set.</param>
    /// <param name="failure">The reason if the page does not agree with the entry.</param>
    /// <returns>True if the splice stream is ready.</returns>
    public bool TryPrepareRestart(Stream stream, in VorbisPageIndexEntry entry, OggSpliceStream splice, out string? failure)
    {
        failure = null;
        Span<byte> page = _prefix.AsSpan(_headerLength);
        if (entry.ByteOffset < 0 || entry.ByteOffset >= stream.Length)
            return FailRestart(out failure, $"The index entry points to byte {entry.ByteOffset}, which is outside the file.");

        stream.Position = entry.ByteOffset;
        if (ReadFully(stream, page[..PageHeaderBytes]) < PageHeaderBytes || !IsPageStart(page))
            return FailRestart(out failure, $"No Ogg page starts at byte {entry.ByteOffset}.");

        int segments = page[26];
        if (PageHeaderBytes + segments > page.Length || ReadFully(stream, page.Slice(PageHeaderBytes, segments)) < segments)
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} ends inside its lacing values.");
        int bodyLength = SumLacing(page.Slice(PageHeaderBytes, segments));
        int pageLength = PageHeaderBytes + segments + bodyLength;
        if (pageLength > page.Length)
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} is larger than every page that the build saw.");
        if (ReadFully(stream, page.Slice(PageHeaderBytes + segments, bodyLength)) < bodyLength)
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} ends inside its body.");
        if (!HasValidCrc(page[..pageLength]))
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} has a bad CRC.");

        byte flags = page[5];
        long granule = BinaryPrimitives.ReadInt64LittleEndian(page.Slice(6, 8));
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(14, 4));
        int sequence = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(18, 4));
        if (serial != Serial || sequence != entry.Sequence || granule != entry.Granule)
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} is not the page of the index entry (sequence {sequence}, granule {granule}).");

        int skip = entry.SkipSegments;
        bool continued = (flags & FlagContinued) != 0;
        if (skip < 0 || skip >= segments || continued != skip > 0)
            return FailRestart(out failure, $"The page at byte {entry.ByteOffset} does not start the way that the index entry tells.");
        for (int i = 0; i < skip; i++)
        {
            bool last = i == skip - 1;
            if (last != page[PageHeaderBytes + i] < 255)
                return FailRestart(out failure, $"The packet end at the start of the page at byte {entry.ByteOffset} does not agree with the index entry.");
        }

        int newLength = pageLength;
        if (skip > 0)
        {
            int skipBytes = SumLacing(page.Slice(PageHeaderBytes, skip));
            int newSegments = segments - skip;
            int newBody = bodyLength - skipBytes;
            page.Slice(PageHeaderBytes + skip, newSegments).CopyTo(page.Slice(PageHeaderBytes, newSegments));
            page.Slice(PageHeaderBytes + segments + skipBytes, newBody).CopyTo(page.Slice(PageHeaderBytes + newSegments, newBody));
            page[26] = (byte)newSegments;
            page[5] = (byte)(flags & ~FlagContinued);

            // The granule position belongs to the last packet that ends on the page. If only the removed packet ended here,
            // no packet ends on the new page.
            if (!AnyPacketEnds(page.Slice(PageHeaderBytes, newSegments)))
                BinaryPrimitives.WriteInt64LittleEndian(page.Slice(6, 8), -1L);
            newLength = PageHeaderBytes + newSegments + newBody;
            OggCrc.Write(page[..newLength]);
        }

        // The header pages get the sequence numbers just before the restart page, so the decoder sees no lost page.
        int headerPages = _headerPageStarts.Length;
        for (int i = 0; i < headerPages; i++)
        {
            Span<byte> header = _prefix.AsSpan(_headerPageStarts[i], _headerPageLengths[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), (uint)(entry.Sequence - headerPages + i));
            OggCrc.Write(header);
        }

        splice.Reset(_prefix, _headerLength + newLength, stream, entry.ByteOffset + pageLength);
        return true;
    }

    /// <summary>Reads all pages of a stream and builds the index.</summary>
    /// <param name="stream">A readable and seekable stream that holds an Ogg Vorbis file.</param>
    /// <param name="cancellationToken">Stops the build between two pages.</param>
    /// <param name="failure">The reason if the stream cannot be indexed.</param>
    /// <returns>The index, or null if the stream cannot be indexed.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static VorbisPageIndex? TryBuild(Stream stream, CancellationToken cancellationToken, out string? failure)
    {
        failure = null;
        stream.Position = 0;
        byte[] page = new byte[MaxPageBytes];
        byte[] idPacket = new byte[64];
        int idLength = 0;
        byte[] setup = new byte[4096];
        int setupLength = 0;
        int commentLength = 0;
        byte commentType = 0;
        Span<bool> modeFlags = stackalloc bool[VorbisSetupHeader.MaxModes];
        int modeCount = 0;
        int modeMask = 0;
        int shortBlock = 0;
        int longBlock = 0;
        int headerPackets = 0;

        byte[]? prefix = null;
        int headerLength = 0;
        int[] headerPageStarts = [];
        int[] headerPageLengths = [];

        VorbisPageIndexEntry[] entries = new VorbisPageIndexEntry[MaxEntries];
        int count = 0;
        int stride = 1;
        int pagesSinceEntry = 0;

        // A candidate entry waits until the packet after its first packet starts, because its restart frame depends on
        // the block size of that packet.
        bool pending = false;
        VorbisPageIndexEntry pendingEntry = default;
        bool pendingFirstLong = false;
        bool awaitingNext = false;
        bool nextKnown = false;
        bool nextLong = false;
        bool freshLong = false;

        long offset = 0;
        int pageNumber = 0;
        uint serial = 0;
        uint lastSequence = 0;
        bool inPacket = false;
        bool endOfStream = false;
        bool currentLong = false;
        bool previousLong = false;
        bool havePrevious = false;
        long position = 0;
        long lastGranule = -1;
        long finalGranule = -1;
        int largestPage = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = ReadFully(stream, page.AsSpan(0, PageHeaderBytes));
            if (read == 0)
                break;
            if (read < PageHeaderBytes)
                return FailBuild(out failure, $"The file ends inside the page header at byte {offset}.");
            if (endOfStream)
                return FailBuild(out failure, "Data follows the end-of-stream page. A chained or multiplexed stream has no index.");
            if (!IsPageStart(page))
                return FailBuild(out failure, $"No Ogg page starts at byte {offset}.");

            byte flags = page[5];
            long granule = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(6, 8));
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14, 4));
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(18, 4));
            int segments = page[26];
            if (ReadFully(stream, page.AsSpan(PageHeaderBytes, segments)) < segments)
                return FailBuild(out failure, $"The file ends inside the lacing values of the page at byte {offset}.");
            int bodyLength = SumLacing(page.AsSpan(PageHeaderBytes, segments));
            int pageLength = PageHeaderBytes + segments + bodyLength;
            if (ReadFully(stream, page.AsSpan(PageHeaderBytes + segments, bodyLength)) < bodyLength)
                return FailBuild(out failure, $"The file ends inside the body of the page at byte {offset}.");
            if (!HasValidCrc(page.AsSpan(0, pageLength)))
                return FailBuild(out failure, $"The page at byte {offset} has a bad CRC.");

            if (pageNumber == 0)
            {
                if ((flags & FlagBeginOfStream) == 0)
                    return FailBuild(out failure, "The first page does not start a stream.");
                serial = pageSerial;
            }
            else
            {
                if ((flags & FlagBeginOfStream) != 0 || pageSerial != serial)
                    return FailBuild(out failure, $"The page at byte {offset} belongs to another logical stream. A multiplexed stream has no index.");
                if (sequence != lastSequence + 1)
                    return FailBuild(out failure, $"The page at byte {offset} has sequence number {sequence} after {lastSequence}, so a page is missing.");
            }
            lastSequence = sequence;

            if (((flags & FlagContinued) != 0) != inPacket)
                return FailBuild(out failure, $"The continuation flag of the page at byte {offset} does not agree with the packet before it.");

            bool audioPage = prefix is not null;
            bool packetEnded = false;
            bool packetStarted = false;
            int freshSegment = -1;
            long restartFrame = 0;
            int bodyPosition = PageHeaderBytes + segments;
            for (int s = 0; s < segments; s++)
            {
                int lacing = page[PageHeaderBytes + s];
                ReadOnlySpan<byte> segment = page.AsSpan(bodyPosition, lacing);
                bodyPosition += lacing;

                if (!audioPage)
                {
                    if (prefix is not null)
                        return FailBuild(out failure, $"Audio data shares the last header page at byte {offset}. The setup header must end its page.");

                    switch (headerPackets)
                    {
                        case 0:
                            if (idLength + lacing > idPacket.Length)
                                return FailBuild(out failure, "The identification header is too long.");
                            segment.CopyTo(idPacket.AsSpan(idLength));
                            idLength += lacing;
                            break;
                        case 1:
                            if (commentLength == 0 && lacing > 0)
                                commentType = segment[0];
                            commentLength += lacing;
                            break;
                        default:
                            if (setupLength + lacing > MaxSetupBytes)
                                return FailBuild(out failure, "The setup header is too long.");
                            if (setupLength + lacing > setup.Length)
                                Array.Resize(ref setup, Math.Max(setup.Length * 2, setupLength + lacing));
                            segment.CopyTo(setup.AsSpan(setupLength));
                            setupLength += lacing;
                            break;
                    }

                    inPacket = lacing == 255;
                    if (inPacket)
                        continue;

                    headerPackets++;
                    if (headerPackets == 1 && !VorbisSetupHeader.TryReadIdentification(idPacket.AsSpan(0, idLength), out _, out _, out shortBlock, out longBlock))
                        return FailBuild(out failure, "The identification header is not valid.");
                    if (headerPackets == 2 && commentType != 3)
                        return FailBuild(out failure, "The second header packet is not a comment header.");
                    if (headerPackets < 3)
                        continue;

                    if (!VorbisSetupHeader.TryReadModeBlockFlags(setup.AsSpan(0, setupLength), modeFlags, out modeCount))
                        return FailBuild(out failure, "The modes of the setup header were not found.");
                    modeMask = (1 << VorbisSetupHeader.BitCount(modeCount - 1)) - 1;
                    prefix = BuildHeaderPages(idPacket.AsSpan(0, idLength), setup.AsSpan(0, setupLength), serial, out headerLength, out headerPageStarts, out headerPageLengths);
                    continue;
                }

                if (!inPacket)
                {
                    if (lacing == 0)
                        return FailBuild(out failure, $"The page at byte {offset} holds an empty audio packet.");
                    byte first = segment[0];
                    if ((first & 1) != 0)
                        return FailBuild(out failure, $"The page at byte {offset} holds a packet that is not audio.");
                    int mode = (first >> 1) & modeMask;
                    if (mode >= modeCount)
                        return FailBuild(out failure, $"A packet on the page at byte {offset} uses mode {mode}, but the setup header has {modeCount} modes.");

                    currentLong = modeFlags[mode];
                    if (awaitingNext)
                    {
                        awaitingNext = false;
                        nextKnown = true;
                        nextLong = currentLong;
                        if (pending)
                        {
                            AddEntry(entries, ref count, ref stride, Shifted(pendingEntry, pendingFirstLong, nextLong, shortBlock, longBlock));
                            pending = false;
                        }
                    }

                    if (!packetStarted)
                    {
                        packetStarted = true;
                        if (havePrevious)
                        {
                            freshSegment = s;
                            freshLong = currentLong;
                            restartFrame = position + Block(previousLong, shortBlock, longBlock) / 4 + Block(currentLong, shortBlock, longBlock) / 4;
                            awaitingNext = true;
                            nextKnown = false;
                        }
                    }
                    inPacket = true;
                }

                if (lacing < 255)
                {
                    position = havePrevious ? position + Block(previousLong, shortBlock, longBlock) / 4 + Block(currentLong, shortBlock, longBlock) / 4 : 0;
                    havePrevious = true;
                    previousLong = currentLong;
                    inPacket = false;
                    packetEnded = true;
                }
            }

            bool pageEndsStream = (flags & FlagEndOfStream) != 0;
            if (!audioPage)
            {
                if (pageEndsStream)
                    return FailBuild(out failure, "The stream ends in its header pages.");
                offset += pageLength;
                pageNumber++;
                continue;
            }

            if (granule == -1)
            {
                if (packetEnded)
                    return FailBuild(out failure, $"A packet ends on the page at byte {offset}, but the page has no granule position.");
            }
            else
            {
                if (!packetEnded)
                    return FailBuild(out failure, $"No packet ends on the page at byte {offset}, but the page has a granule position.");
                if (pageEndsStream ? granule > position || granule < lastGranule : granule != position)
                    return FailBuild(out failure, $"The page at byte {offset} has granule position {granule}, but its packets end at frame {position}.");
                lastGranule = granule;
            }

            if (pageEndsStream)
            {
                if (granule < 0)
                    return FailBuild(out failure, "The end-of-stream page has no granule position.");
                endOfStream = true;
                finalGranule = granule;
            }

            largestPage = Math.Max(largestPage, pageLength);
            pagesSinceEntry++;
            if (freshSegment >= 0 && sequence >= (uint)headerPageStarts.Length && pagesSinceEntry >= stride)
            {
                VorbisPageIndexEntry candidate = new(offset, granule, restartFrame, (int)sequence, freshSegment);
                if (nextKnown)
                    AddEntry(entries, ref count, ref stride, Shifted(candidate, freshLong, nextLong, shortBlock, longBlock));
                else
                {
                    pending = true;
                    pendingEntry = candidate;
                    pendingFirstLong = freshLong;
                }
                pagesSinceEntry = 0;
            }

            offset += pageLength;
            pageNumber++;
        }

        if (prefix is null)
            return FailBuild(out failure, "The file has no complete Vorbis headers.");
        if (!endOfStream)
            return FailBuild(out failure, "The file has no end-of-stream page.");

        // The restart area holds the largest audio page of the file, not the largest possible page.
        Array.Resize(ref entries, count);
        Array.Resize(ref prefix, headerLength + largestPage);
        return new VorbisPageIndex(entries, prefix, headerLength, headerPageStarts, headerPageLengths, serial, finalGranule, pageNumber, stride);
    }

    private static int Block(bool isLong, int shortBlock, int longBlock)
        => isLong ? longBlock : shortBlock;

    // NVorbis gives no samples for the first packet of a decode. When that packet has a long block and the next packet a
    // short block, NVorbis also drops the flat part of the right half of the long window, before the short overlap starts.
    // That part is a quarter of the long block minus a quarter of the short block, so the first sample comes that much later.
    private static VorbisPageIndexEntry Shifted(in VorbisPageIndexEntry entry, bool firstLong, bool nextLong, int shortBlock, int longBlock)
    {
        int shift = Math.Max(0, (Block(firstLong, shortBlock, longBlock) - Block(nextLong, shortBlock, longBlock)) / 4);
        return entry with { RestartFrame = entry.RestartFrame + shift };
    }

    // Adds an entry. When the array is full, it keeps every second entry and doubles the stride first.
    private static void AddEntry(VorbisPageIndexEntry[] entries, ref int count, ref int stride, in VorbisPageIndexEntry entry)
    {
        if (count == MaxEntries)
        {
            for (int i = 0; i < MaxEntries / 2; i++)
                entries[i] = entries[i * 2];
            count = MaxEntries / 2;
            stride *= 2;
        }
        entries[count++] = entry;
    }

    // Writes the identification header on the first page, and an empty comment header and the setup header on the pages
    // after it. The build adds space for the largest audio page later. The sequence numbers are set at each restart.
    private static byte[] BuildHeaderPages(ReadOnlySpan<byte> id, ReadOnlySpan<byte> setup, uint serial, out int length, out int[] pageStarts, out int[] pageLengths)
    {
        int commentAndSetup = EmptyCommentPacket.Length + setup.Length;
        int lacingCount = id.Length / 255 + 1 + EmptyCommentPacket.Length / 255 + 1 + setup.Length / 255 + 1;
        int pages = 1 + (lacingCount + 254) / 255;
        byte[] prefix = new byte[pages * (PageHeaderBytes + 255) + id.Length + commentAndSetup];

        byte[] lacing = new byte[lacingCount];
        byte[] body = new byte[commentAndSetup];
        EmptyCommentPacket.CopyTo(body, 0);
        setup.CopyTo(body.AsSpan(EmptyCommentPacket.Length));
        int restLacing = AddLacing(lacing, 0, EmptyCommentPacket.Length);
        restLacing = AddLacing(lacing, restLacing, setup.Length);

        int[] starts = new int[1 + (restLacing + 254) / 255];
        int[] lengths = new int[starts.Length];
        byte[] idLacing = new byte[id.Length / 255 + 1];
        AddLacing(idLacing, 0, id.Length);
        int position = 0;
        starts[0] = position;
        lengths[0] = WritePage(prefix.AsSpan(position), FlagBeginOfStream, serial, idLacing, id);
        position += lengths[0];

        int lacingDone = 0;
        int bodyDone = 0;
        for (int p = 1; p < starts.Length; p++)
        {
            int pageLacing = Math.Min(255, restLacing - lacingDone);
            ReadOnlySpan<byte> values = lacing.AsSpan(lacingDone, pageLacing);
            int pageBody = SumLacing(values);
            byte flags = lacingDone > 0 && lacing[lacingDone - 1] == 255 ? FlagContinued : (byte)0;
            starts[p] = position;
            lengths[p] = WritePage(prefix.AsSpan(position), flags, serial, values, body.AsSpan(bodyDone, pageBody));
            position += lengths[p];
            lacingDone += pageLacing;
            bodyDone += pageBody;
        }

        length = position;
        pageStarts = starts;
        pageLengths = lengths;
        return prefix;
    }

    // Adds the lacing values of one packet: a value of 255 for each full segment and one last value below 255.
    private static int AddLacing(byte[] lacing, int index, int packetLength)
    {
        for (int i = 0; i < packetLength / 255; i++)
            lacing[index++] = 255;
        lacing[index++] = (byte)(packetLength % 255);
        return index;
    }

    // Writes one header page with granule position zero. The sequence number and the CRC are set at each restart.
    private static int WritePage(Span<byte> destination, byte flags, uint serial, ReadOnlySpan<byte> lacing, ReadOnlySpan<byte> body)
    {
        "OggS"u8.CopyTo(destination);
        destination[4] = 0;
        destination[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(6, 8), 0L);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(14, 4), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(18, 4), 0u);
        destination[26] = (byte)lacing.Length;
        lacing.CopyTo(destination[PageHeaderBytes..]);
        body.CopyTo(destination[(PageHeaderBytes + lacing.Length)..]);
        int length = PageHeaderBytes + lacing.Length + body.Length;
        OggCrc.Write(destination[..length]);
        return length;
    }

    private static bool IsPageStart(ReadOnlySpan<byte> header)
        => header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S' && header[4] == 0;

    private static bool HasValidCrc(ReadOnlySpan<byte> page)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(22, 4));
        ReadOnlySpan<byte> zero = [0, 0, 0, 0];
        uint crc = OggCrc.Update(0u, page[..22]);
        crc = OggCrc.Update(crc, zero);
        crc = OggCrc.Update(crc, page[26..]);
        return crc == stored;
    }

    private static int SumLacing(ReadOnlySpan<byte> lacing)
    {
        int sum = 0;
        for (int i = 0; i < lacing.Length; i++)
            sum += lacing[i];
        return sum;
    }

    private static bool AnyPacketEnds(ReadOnlySpan<byte> lacing)
    {
        for (int i = 0; i < lacing.Length; i++)
        {
            if (lacing[i] < 255)
                return true;
        }
        return false;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private static VorbisPageIndex? FailBuild(out string? failure, string reason)
    {
        failure = reason;
        return null;
    }

    private static bool FailRestart(out string? failure, string reason)
    {
        failure = reason;
        return false;
    }
}
