using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Finds absolute machine paths in published files and strips them from the cooked copy. A publish must not ship a machine
/// path: the asset roots and the user profile folder of the cook process, drive-letter paths, user profile paths, and UNC
/// paths, as UTF-8 and as UTF-16 text. The strip overwrites each path with filler of the same length, so the file layout and
/// any length prefix stay valid, and it records each stripped path in the current <see cref="PublishPathRedactionLog"/>. It
/// changes only files in the build output, never a source file. This step is the last guard behind the write-time path
/// projection (<see cref="XREngine.Core.Files.CookedSourcePathProjection"/>).
/// </summary>
/// <remarks>
/// <para>
/// The rules depend on the file (<see cref="EMachinePathScanProfile"/>). Text uses the general drive and UNC patterns. A
/// cooked asset needs three path parts for a general drive path, so a short random byte run does not match. Binary media
/// (compressed or raw images, audio, video, models) holds long printable runs in raw pixel and sample data, so it uses the
/// exact roots, user profile paths, and a strict file-path pattern: two or more folder parts, one separator style, plain name
/// characters, and a file name with an extension.
/// </para>
/// <para>
/// The filler is the underscore character. In text, the stripped range stops before a quote, a comma, a semicolon, an equals
/// sign, or a closing brace, so the syntax stays valid; an escaped backslash is always stripped as a pair. In binary data,
/// the range stops at the end of a length-prefixed string when the four bytes before the path give its length; otherwise it
/// stops at the first byte that is not a plain name character. A PNG file gets new chunk checksums.
/// </para>
/// <para>
/// The strip fails, and names the file, when a path cannot be stripped safely: in a compressed or checksummed container (ZIP,
/// GZIP, 7z, RAR, XZ, Zstandard, BZip2), in a critical PNG chunk, or when a path is still found after the strip.
/// </para>
/// </remarks>
public static class CookedOutputPathGuard
{
    /// <summary>
    /// The version of the find and strip rules. An incremental archive build checks every entry again when the version
    /// that it recorded differs.
    /// </summary>
    public const int StripVersion = 2;

    private const string Utf8PathChar =
        @"(?:[\x20-\x7E-[\\/:*?""<>|]]|[\xC2-\xDF][\x80-\xBF]|[\xE0-\xEF][\x80-\xBF]{2}|[\xF0-\xF4][\x80-\xBF]{3})";
    private const string Utf16PathChar = @"(?:[^\x00-\x1F\x7F\\/:*?""<>|\uD800-\uDFFF�￾￿]|[\uD800-\uDBFF][\uDC00-\uDFFF])";
    private const string Utf8NameChar =
        @"(?:[A-Za-z0-9 _.()&+,'~!#$%@-]|[\xC2-\xDF][\x80-\xBF]|[\xE0-\xEF][\x80-\xBF]{2}|[\xF0-\xF4][\x80-\xBF]{3})";
    private const string Utf16NameChar = @"[\p{L}\p{Nd} _.()&+,'~!#$%@-]";
    private const string Utf8NameEnd = @"(?:[A-Za-z0-9_)-]|[\x80-\xBF])";
    private const string Utf16NameEnd = @"[\p{L}\p{Nd}_)-]";
    private const string Separator = @"(?:\\{1,2}|/)";
    private const string UncStart = @"(?<![\\\w.$-])(?:\\{4}|\\{2})(?=[A-Za-z0-9._$-])";
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int ChunkOverlap = 4096;
    private const int TextProbeLength = 8192;
    private const int MinimumRootLength = 8;
    private const int MaxReportedLength = 260;
    private const byte Filler = (byte)'_';

    private static readonly Regex Utf8CookedDrive = CreateDrivePattern(Utf8PathChar, minimumParts: 2, anchored: false);
    private static readonly Regex Utf16CookedDrive = CreateDrivePattern(Utf16PathChar, minimumParts: 2, anchored: false);
    private static readonly Regex Utf8TextDrive = CreateDrivePattern(Utf8PathChar, minimumParts: 1, anchored: true);
    private static readonly Regex Utf16TextDrive = CreateDrivePattern(Utf16PathChar, minimumParts: 1, anchored: true);
    private static readonly Regex Utf8Unc = CreateUncPattern(Utf8PathChar);
    private static readonly Regex Utf16Unc = CreateUncPattern(Utf16PathChar);
    private static readonly Regex Utf8UserFolder = CreateUserFolderPattern(Utf8PathChar);
    private static readonly Regex Utf16UserFolder = CreateUserFolderPattern(Utf16PathChar);
    private static readonly Regex Utf8BinaryFile = CreateBinaryFilePattern(Utf8NameChar, Utf8NameEnd);
    private static readonly Regex Utf16BinaryFile = CreateBinaryFilePattern(Utf16NameChar, Utf16NameEnd);
    private static readonly Regex UserFolderToken = new(@"(?i:users|documents and settings)(?:\\{1,2}|/)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly byte[][] UnsafeContainerSignatures =
    [
        [(byte)'P', (byte)'K', 3, 4],
        [(byte)'P', (byte)'K', 5, 6],
        [0x1F, 0x8B],
        [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C],
        [(byte)'R', (byte)'a', (byte)'r', (byte)'!'],
        [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0],
        [0x28, 0xB5, 0x2F, 0xFD],
        [(byte)'B', (byte)'Z', (byte)'h'],
    ];

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Strips every machine path from a cooked asset file in the build output.</summary>
    /// <param name="cookedFile">The cooked file. It is changed in place.</param>
    /// <param name="label">The name of the file in the warning and in an error.</param>
    /// <returns>The stripped paths.</returns>
    /// <exception cref="CookedSourcePathException">A path cannot be stripped safely.</exception>
    public static IReadOnlyList<string> StripCookedFile(string cookedFile, string label)
        => Strip(cookedFile, label, EMachinePathScanProfile.CookedAsset);

    /// <summary>
    /// Strips every machine path from a file that a publish copies as it is (text, JSON, media, and other files). Call it
    /// on the copy in the build output, never on a source file.
    /// </summary>
    /// <param name="copiedFile">The copied file. It is changed in place.</param>
    /// <param name="label">The name of the file in the warning and in an error.</param>
    /// <returns>The stripped paths.</returns>
    /// <exception cref="CookedSourcePathException">A path cannot be stripped safely.</exception>
    public static IReadOnlyList<string> StripCopiedFile(string copiedFile, string label)
        => Strip(copiedFile, label, DetectCopiedFileProfile(copiedFile));

    /// <summary>True if a copied or source file holds a machine path. The file does not change.</summary>
    /// <param name="file">The file.</param>
    /// <returns>True if the strip would change the file.</returns>
    public static bool ContainsMachinePathInCopiedFile(string file)
        => FindMachinePathsInFile(file, DetectCopiedFileProfile(file)).Count > 0;

    /// <summary>Finds the first machine path in cooked bytes.</summary>
    /// <param name="bytes">The cooked bytes.</param>
    /// <param name="assetRoots">The exact roots to find, for example the asset roots of the cook process.</param>
    /// <param name="path">The path that was found.</param>
    /// <returns>True if the bytes hold a machine path.</returns>
    internal static bool TryFindMachinePath(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, out string? path)
        => TryFindMachinePath(bytes, assetRoots, EMachinePathScanProfile.CookedAsset, out path);

    /// <summary>Finds the first machine path in bytes with the rules of a profile.</summary>
    internal static bool TryFindMachinePath(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, EMachinePathScanProfile profile, out string? path)
    {
        List<MachinePathHit> hits = FindMachinePaths(bytes, assetRoots, profile);
        if (hits.Count == 0)
        {
            path = null;
            return false;
        }

        MachinePathHit first = hits[0];
        foreach (MachinePathHit hit in hits)
            if (hit.Offset < first.Offset)
                first = hit;
        path = first.Text;
        return true;
    }

    /// <summary>Finds every machine path in bytes with the rules of a profile. The offsets are relative to the bytes.</summary>
    internal static List<MachinePathHit> FindMachinePaths(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, EMachinePathScanProfile profile)
    {
        List<MachinePathHit> hits = [];
        AddHits(bytes, assetRoots, profile, 0, hits);
        return hits;
    }

    /// <summary>True if the first bytes of a copied file look like text: a byte order mark, or valid UTF-8 with no zero byte.</summary>
    /// <param name="head">The first bytes of the file.</param>
    /// <returns>True for text.</returns>
    internal static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ||
            head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ||
            head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return true;

        ReadOnlySpan<byte> probe = head[..Math.Min(head.Length, TextProbeLength)];
        if (probe.IndexOf((byte)0) >= 0)
            return false;

        // The probe can end inside a UTF-8 sequence. Remove an incomplete sequence at the end before the check.
        int end = probe.Length;
        for (int back = 1; back <= 3 && back <= probe.Length; back++)
        {
            byte value = probe[^back];
            if ((value & 0xC0) != 0x80)
            {
                int sequenceLength = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;
                if (sequenceLength > back)
                    end = probe.Length - back;
                break;
            }
        }

        return System.Text.Unicode.Utf8.IsValid(probe[..end]);
    }

    private static EMachinePathScanProfile DetectCopiedFileProfile(string file)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[TextProbeLength];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return LooksLikeText(head[..read]) ? EMachinePathScanProfile.Text : EMachinePathScanProfile.BinaryMedia;
    }

    private static IReadOnlyList<string> Strip(string file, string label, EMachinePathScanProfile profile)
    {
        List<MachinePathHit> hits = FindMachinePathsInFile(file, profile);
        if (hits.Count == 0)
            return [];

        DateTime lastWrite = File.GetLastWriteTimeUtc(file);
        List<string> stripped = [];
        using (FileStream stream = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            byte[] head = new byte[16];
            int headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            ThrowIfUnsafeContainer(head.AsSpan(0, headLength), label, hits);
            bool png = head.AsSpan(0, headLength).StartsWith(PngSignature);

            List<(long Start, int Length, bool Utf16)> ranges = new(hits.Count);
            foreach (MachinePathHit hit in hits)
            {
                (long start, int length) = BoundRange(stream, hit, profile);
                if (length <= 0)
                    continue;
                ranges.Add((start, length, hit.Utf16));
                stripped.Add(ReadText(stream, start, length, hit.Utf16));
            }

            List<(long DataStart, int DataLength)>? pngChunks = png ? FindModifiedPngChunks(stream, ranges, label) : null;
            foreach ((long start, int length, bool utf16) in ranges)
                WriteFiller(stream, start, length, utf16);
            if (pngChunks is not null)
                foreach ((long dataStart, int dataLength) in pngChunks)
                    RewritePngChunkCrc(stream, dataStart, dataLength);
        }

        File.SetLastWriteTimeUtc(file, lastWrite);

        List<MachinePathHit> remaining = FindMachinePathsInFile(file, profile);
        if (remaining.Count > 0)
            throw new CookedSourcePathException(
                $"Published file '{label}' still holds the machine path '{remaining[0].Text}' after the strip, so it cannot be " +
                "stripped safely. Remove the path from the source file.");

        foreach (string path in stripped)
            PublishPathRedactionLog.Record(label, path);
        return stripped;
    }

    /// <summary>
    /// Reads a file in chunks and finds every machine path. Each chunk starts with the last bytes of the chunk before it,
    /// so a path that crosses a chunk edge is in one chunk.
    /// </summary>
    private static List<MachinePathHit> FindMachinePathsInFile(string file, EMachinePathScanProfile profile)
    {
        List<string> roots = GetSearchRoots();
        List<MachinePathHit> hits = [];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + ChunkOverlap);
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            int carried = 0;
            long chunkStart = 0;
            while (true)
            {
                int read = stream.ReadAtLeast(buffer.AsSpan(carried, ChunkSize), ChunkSize, throwOnEndOfStream: false);
                int length = carried + read;
                if (read == 0 && carried == 0)
                    break;

                AddHits(buffer.AsSpan(0, length), roots, profile, chunkStart, hits);
                if (read < ChunkSize)
                    break;

                carried = Math.Min(ChunkOverlap, length);
                chunkStart += length - carried;
                buffer.AsSpan(length - carried, carried).CopyTo(buffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return RemoveOverlaps(hits);
    }

    private static void AddHits(ReadOnlySpan<byte> bytes, IReadOnlyList<string> roots, EMachinePathScanProfile profile, long baseOffset, List<MachinePathHit> hits)
    {
        // UTF-8 text keeps its ASCII bytes, and Latin-1 maps each byte to one char. UTF-16 text can start at an even or an
        // odd offset.
        string utf8View = Encoding.Latin1.GetString(bytes);
        AddViewHits(utf8View, roots, profile, isUtf8View: true, baseOffset, hits);

        for (int offset = 0; offset < 2 && offset < bytes.Length; offset++)
        {
            int length = (bytes.Length - offset) & ~1;
            string utf16View = Encoding.Unicode.GetString(bytes.Slice(offset, length));
            AddViewHits(utf16View, roots, profile, isUtf8View: false, baseOffset + offset, hits);
        }
    }

    private static void AddViewHits(string text, IReadOnlyList<string> roots, EMachinePathScanProfile profile, bool isUtf8View, long viewOffset, List<MachinePathHit> hits)
    {
        int unit = isUtf8View ? 1 : 2;
        foreach (string root in roots)
        {
            string needle = isUtf8View ? Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(root)) : root;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = index + needle.Length;
                while (end < text.Length && IsPathCharOrSeparator(text[end], isUtf8View))
                    end++;
                hits.Add(CreateHit(text, index, end - index, isUtf8View, viewOffset, unit));
                index = end;
            }
        }

        foreach (Regex pattern in GetPatterns(profile, isUtf8View))
            foreach (Match match in pattern.Matches(text))
                hits.Add(CreateHit(text, match.Index, match.Length, isUtf8View, viewOffset, unit));
    }

    private static MachinePathHit CreateHit(string text, int index, int length, bool isUtf8View, long viewOffset, int unit)
        => new(viewOffset + (long)index * unit, length * unit, !isUtf8View, Report(text.Substring(index, Math.Min(length, MaxReportedLength)), isUtf8View));

    private static Regex[] GetPatterns(EMachinePathScanProfile profile, bool isUtf8View)
        => profile switch
        {
            EMachinePathScanProfile.Text => isUtf8View ? [Utf8TextDrive, Utf8Unc] : [Utf16TextDrive, Utf16Unc],
            EMachinePathScanProfile.BinaryMedia => isUtf8View ? [Utf8UserFolder, Utf8BinaryFile] : [Utf16UserFolder, Utf16BinaryFile],
            _ => isUtf8View ? [Utf8CookedDrive, Utf8Unc, Utf8UserFolder] : [Utf16CookedDrive, Utf16Unc, Utf16UserFolder],
        };

    /// <summary>Keeps the longest hit where hits overlap, for example a root hit and a pattern hit of the same path.</summary>
    private static List<MachinePathHit> RemoveOverlaps(List<MachinePathHit> hits)
    {
        if (hits.Count < 2)
            return hits;

        hits.Sort(static (a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : b.ByteLength.CompareTo(a.ByteLength));
        List<MachinePathHit> result = [];
        long coveredEnd = -1;
        foreach (MachinePathHit hit in hits)
        {
            long end = hit.Offset + hit.ByteLength;
            if (hit.Offset < coveredEnd)
            {
                // A hit inside an earlier one adds nothing. A hit that goes further than an earlier one makes it longer.
                if (end > coveredEnd && result[^1].Utf16 == hit.Utf16)
                {
                    MachinePathHit last = result[^1];
                    result[^1] = last with { ByteLength = (int)(end - last.Offset) };
                    coveredEnd = end;
                }
                continue;
            }

            result.Add(hit);
            coveredEnd = end;
        }

        return result;
    }

    /// <summary>Gets the byte range to overwrite for a hit. See the remarks of the class for the rules.</summary>
    private static (long Start, int Length) BoundRange(FileStream stream, MachinePathHit hit, EMachinePathScanProfile profile)
    {
        int unit = hit.Utf16 ? 2 : 1;
        int prefixLength = (int)Math.Min(hit.Offset, 4);
        byte[] bytes = new byte[prefixLength + hit.ByteLength];
        stream.Position = hit.Offset - prefixLength;
        stream.ReadExactly(bytes);
        string text = hit.Utf16
            ? Encoding.Unicode.GetString(bytes, prefixLength, hit.ByteLength)
            : Encoding.Latin1.GetString(bytes, prefixLength, hit.ByteLength);

        int units;
        if (profile == EMachinePathScanProfile.Text)
        {
            units = text.Length;
            int cut = text.IndexOfAny(['\'', '`', ',', ';', '=', '}']);
            if (cut >= 0)
                units = cut;
            while (units > 0 && text[units - 1] is ' ' or '\t')
                units--;
            return (hit.Offset, units * unit);
        }

        int minimum = GetMinimumUnits(text);
        units = TryReadLengthPrefix(bytes.AsSpan(0, prefixLength), out int prefixed) && prefixed >= minimum
            ? Math.Min(prefixed, text.Length)
            : Math.Max(TrimToNameCharacters(text, hit.Utf16), minimum);
        return (hit.Offset, units * unit);
    }

    /// <summary>
    /// Gets the smallest number of characters to strip: through the last separator, and through the user name of a user
    /// profile path, so a user name never stays.
    /// </summary>
    private static int GetMinimumUnits(string text)
    {
        int minimum = text.LastIndexOfAny(['\\', '/']) + 1;
        Match user = UserFolderToken.Match(text);
        if (user.Success)
        {
            int nameStart = user.Index + user.Length;
            int nameEnd = text.IndexOfAny(['\\', '/'], nameStart);
            minimum = Math.Max(minimum, nameEnd < 0 ? text.Length : nameEnd);
        }

        return Math.Min(minimum, text.Length);
    }

    /// <summary>
    /// Reads a 32-bit little-endian length before a string, as FBX string properties, EXR attributes, and MemoryPack strings
    /// write it. A one-byte length is not read: a quote or an equals sign before a path in XML text would look like one.
    /// </summary>
    private static bool TryReadLengthPrefix(ReadOnlySpan<byte> prefix, out int length)
    {
        length = 0;
        if (prefix.Length == 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            // For UTF-16 text the value can be a character or a byte count. The caller limits the range to the match, so a
            // byte count read as a character count stays inside the path.
            if (value is >= 3 and <= 1 << 20)
            {
                length = (int)value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the number of leading characters that are separators or plain name characters.</summary>
    private static int TrimToNameCharacters(string text, bool utf16)
    {
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            bool name = c is '\\' or '/' or ' ' or '_' or '.' or '(' or ')' or '&' or '+' or ',' or '\'' or '~' or '!' or '#' or '$' or '%' or '@' or '-' or ':'
                || char.IsAsciiLetterOrDigit(c)
                || (utf16 ? char.IsLetterOrDigit(c) : c >= '\u0080');
            if (!name)
                break;
            index++;
        }

        return index;
    }

    private static string ReadText(FileStream stream, long start, int length, bool utf16)
    {
        byte[] bytes = new byte[length];
        stream.Position = start;
        stream.ReadExactly(bytes);
        return utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    private static void WriteFiller(FileStream stream, long start, int length, bool utf16)
    {
        byte[] filler = new byte[length];
        if (utf16)
        {
            for (int i = 0; i + 1 < length; i += 2)
                filler[i] = Filler;
        }
        else
        {
            filler.AsSpan().Fill(Filler);
        }

        stream.Position = start;
        stream.Write(filler);
    }

    private static void ThrowIfUnsafeContainer(ReadOnlySpan<byte> head, string label, List<MachinePathHit> hits)
    {
        foreach (byte[] signature in UnsafeContainerSignatures)
            if (head.StartsWith(signature))
                throw new CookedSourcePathException(
                    $"Published file '{label}' holds the machine path '{hits[0].Text}' in a compressed or checksummed container, " +
                    "so it cannot be stripped safely. Remove the path from the source file.");
    }

    /// <summary>
    /// Finds the PNG chunks that the ranges change. A range must be inside the data of one ancillary chunk; a range in a
    /// critical chunk (for example image data) cannot be stripped safely.
    /// </summary>
    private static List<(long DataStart, int DataLength)> FindModifiedPngChunks(FileStream stream, List<(long Start, int Length, bool Utf16)> ranges, string label)
    {
        List<(long DataStart, int DataLength)> chunks = [];
        byte[] header = new byte[8];
        foreach ((long start, int length, _) in ranges)
        {
            long position = PngSignature.Length;
            bool found = false;
            while (position + 12 <= stream.Length)
            {
                stream.Position = position;
                stream.ReadExactly(header);
                int dataLength = BinaryPrimitives.ReadInt32BigEndian(header);
                long dataStart = position + 8;
                if (dataLength < 0 || dataStart + dataLength + 4 > stream.Length)
                    break;

                if (start >= dataStart && start + length <= dataStart + dataLength)
                {
                    bool critical = header[4] is >= (byte)'A' and <= (byte)'Z';
                    if (critical)
                        break;
                    if (!chunks.Contains((dataStart, dataLength)))
                        chunks.Add((dataStart, dataLength));
                    found = true;
                    break;
                }

                position = dataStart + dataLength + 4;
            }

            if (!found)
                throw new CookedSourcePathException(
                    $"Published file '{label}' holds a machine path in a critical PNG chunk, so it cannot be stripped safely. " +
                    "Remove the path from the source file.");
        }

        return chunks;
    }

    private static void RewritePngChunkCrc(FileStream stream, long dataStart, int dataLength)
    {
        byte[] typeAndData = new byte[4 + dataLength];
        stream.Position = dataStart - 4;
        stream.ReadExactly(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(typeAndData));
        stream.Position = dataStart + dataLength;
        stream.Write(crc);
    }

    private static Regex CreateDrivePattern(string pathChar, int minimumParts, bool anchored)
        => new((anchored ? "(?<![A-Za-z0-9])" : string.Empty) +
            "[A-Za-z]:" + Separator + "(?:" + pathChar + "+" + Separator + "){" + minimumParts + ",}" + pathChar + "*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A UNC path. The start must not follow a path character or a backslash, so an escaped separator inside a relative
    /// path (for example <c>Textures\\Wall\\a.png</c> in JSON) is not a server name.
    /// </summary>
    private static Regex CreateUncPattern(string pathChar)
        => new(UncStart + "[A-Za-z0-9._$-]{2,}" + @"\\{1,2}" + pathChar + "{2,}" + @"\\{1,2}" + pathChar + "{2,}" +
            "(?:" + Separator + pathChar + "+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A path in a Windows user profile folder: a drive, <c>Users</c> or <c>Documents and Settings</c>, and a user name.</summary>
    private static Regex CreateUserFolderPattern(string pathChar)
        => new("[A-Za-z]:" + Separator + "(?i:users|documents and settings)" + Separator + pathChar + "{2,}" +
            "(?:" + Separator + pathChar + "+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A file path in binary data: two or more folder parts of two or more plain name characters, one separator style, and
    /// a file name with an extension of two to five characters that starts with a letter. Raw pixel and sample data almost
    /// never forms it. The extension does not need a boundary after it, because packed binary data (for example the next
    /// FBX property code) can follow it directly.
    /// </summary>
    private static Regex CreateBinaryFilePattern(string nameChar, string nameEnd)
        => new(@"(?<![A-Za-z0-9])[A-Za-z]:(?<s>\\\\|\\|/)(?:" + nameChar + @"{2,}\k<s>){2,}" + nameChar + "*?" + nameEnd +
            @"\.[A-Za-z][A-Za-z0-9]{1,4}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsPathCharOrSeparator(char c, bool isUtf8View)
    {
        if (c is '\\' or '/')
            return true;
        if (c < ' ' || c == '\x7F' || c is '"' or '*' or ':' or '<' or '>' or '?' or '|')
            return false;
        return isUtf8View || (c != '�' && !char.IsSurrogate(c));
    }

    /// <summary>Gets the text to report for a match. The UTF-8 view is decoded again, and the text stops at a control character.</summary>
    private static string Report(string value, bool isUtf8View)
    {
        string text = isUtf8View ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value)) : value;
        int end = 0;
        while (end < text.Length && !char.IsControl(text[end]) && text[end] != '�')
            end++;
        return text[..Math.Max(end, Math.Min(text.Length, 3))];
    }

    /// <summary>
    /// Gets the exact roots to find: the game and engine asset roots of the cook process and the user profile folder, each
    /// with both separator forms and the escaped form.
    /// </summary>
    private static List<string> GetSearchRoots()
    {
        List<string> roots = [];
        IAssetSerializationServices services = AssetSerializationServices.Current;
        AddRoot(roots, services.GameAssetsPath);
        AddRoot(roots, services.EngineAssetsPath);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
        return roots;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A very short root, for example a drive root, would match too much. The patterns cover such paths.
        if (full.Length < MinimumRootLength)
            return;

        roots.Add(full);
        string alternate = full.Replace('\\', '/');
        if (!string.Equals(alternate, full, StringComparison.Ordinal))
            roots.Add(alternate);
        string escaped = full.Replace("\\", "\\\\");
        if (!string.Equals(escaped, full, StringComparison.Ordinal))
            roots.Add(escaped);
    }
}
