using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Checks published files for absolute machine paths: the asset roots and the user profile folder of the cook process,
/// drive-letter paths, and UNC paths, as UTF-8 and as UTF-16 text. A publish must not ship a machine path, so a hit fails
/// the publish with <see cref="CookedSourcePathException"/>, and the error names the file. This check is the last guard
/// behind the write-time path projection (<see cref="XREngine.Core.Files.CookedSourcePathProjection"/>). It covers cooked
/// asset files and the files that a publish copies as they are (text, JSON, media, and other files).
/// </summary>
/// <remarks>
/// <para>
/// Binary data can look like <c>C:\</c> by chance. The drive-letter and UNC patterns therefore need at least three path
/// parts, which random data almost never gives. A real machine path, for example a path in a user profile, always has them.
/// The asset roots and the user profile folder are matched exactly, without case.
/// </para>
/// <para>
/// A copied file can be large binary media: compressed or raw images, audio, video, and models. Raw pixel and sample data
/// often holds long runs of printable bytes with many backslashes, so the general drive and UNC patterns give false hits
/// in it. A copied file that is not text therefore uses only exact matches: the asset roots, the user profile folder of the
/// cook process, and any path in a Windows user profile folder (<c>X:\Users\name</c> or <c>X:\Documents and Settings\name</c>,
/// which holds a user name). A drive path outside a user profile folder in binary media is not found. A copied file is text
/// when it starts with a byte order mark, or when its first bytes hold no zero byte and are valid UTF-8; text uses the
/// general patterns.
/// </para>
/// <para>
/// A separator can be a slash, a backslash, or an escaped backslash (two backslashes), as JSON text and double-quoted YAML
/// scalars write it.
/// </para>
/// <para>
/// A path part can hold non-ASCII characters, for example a Cyrillic or a CJK user profile name. In the UTF-8 view, a
/// non-ASCII character must be one complete UTF-8 sequence, so random high bytes do not extend a match. In the UTF-16 view,
/// any character that is not a control character, a reserved path character, a lone surrogate, or the replacement
/// character is a path character; a complete surrogate pair is one path character.
/// </para>
/// </remarks>
public static class CookedOutputPathGuard
{
    private const string Utf8PathChar =
        @"(?:[\x20-\x7E-[\\/:*?""<>|]]|[\xC2-\xDF][\x80-\xBF]|[\xE0-\xEF][\x80-\xBF]{2}|[\xF0-\xF4][\x80-\xBF]{3})";
    private const string Utf16PathChar = @"(?:[^\x00-\x1F\x7F\\/:*?""<>|\uD800-\uDFFF\uFFFD\uFFFE\uFFFF]|[\uD800-\uDBFF][\uDC00-\uDFFF])";
    private const string Separator = @"(?:\\{1,2}|/)";
    private const string UncSeparator = @"\\{1,2}";
    private const int MaxReportedLength = 260;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int ChunkOverlap = 4096;
    private const int TextProbeLength = 8192;
    private const int MinimumRootLength = 8;

    private static readonly Regex Utf8DrivePath = CreateDrivePattern(Utf8PathChar);
    private static readonly Regex Utf16DrivePath = CreateDrivePattern(Utf16PathChar);
    private static readonly Regex Utf8UncPath = CreateUncPattern(Utf8PathChar);
    private static readonly Regex Utf16UncPath = CreateUncPattern(Utf16PathChar);
    private static readonly Regex Utf8UserFolderPath = CreateUserFolderPattern(Utf8PathChar);
    private static readonly Regex Utf16UserFolderPath = CreateUserFolderPattern(Utf16PathChar);

    /// <summary>
    /// Fails the cook if a cooked asset file holds a machine path.
    /// </summary>
    /// <param name="cookedFile">The cooked file.</param>
    /// <param name="assetLabel">The name of the asset in the error, for example its path relative to the asset folder.</param>
    /// <exception cref="CookedSourcePathException">The file holds a machine path.</exception>
    public static void ThrowIfMachinePath(string cookedFile, string assetLabel)
    {
        if (TryFindMachinePathInFile(cookedFile, detectBinaryMedia: false, out string? path))
            throw new CookedSourcePathException(
                $"Cooked asset '{assetLabel}' holds the machine path '{path}'. A cook must not ship a machine path. " +
                "Move the source file into the game or engine asset folder, or remove the path from the asset.");
    }

    /// <summary>
    /// Fails the publish if a file that the publish copies as it is (not a cooked asset) holds a machine path. A file that
    /// is not text uses the exact rule for binary media.
    /// </summary>
    /// <param name="copiedFile">The copied file, or the source file that the publish packs as it is.</param>
    /// <param name="fileLabel">The name of the file in the error, for example its path relative to the asset folder.</param>
    /// <exception cref="CookedSourcePathException">The file holds a machine path.</exception>
    public static void ThrowIfMachinePathInCopiedFile(string copiedFile, string fileLabel)
    {
        if (TryFindMachinePathInFile(copiedFile, detectBinaryMedia: true, out string? path))
            throw new CookedSourcePathException(
                $"Published file '{fileLabel}' holds the machine path '{path}'. A publish must not ship a machine path. " +
                "Remove the path from the file, or remove the file from the published folder.");
    }

    /// <summary>Finds the first machine path in cooked bytes.</summary>
    /// <param name="bytes">The cooked bytes.</param>
    /// <param name="assetRoots">The exact roots to find, for example the asset roots of the cook process.</param>
    /// <param name="path">The path that was found.</param>
    /// <returns>True if the bytes hold a machine path.</returns>
    internal static bool TryFindMachinePath(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, out string? path)
        => TryFindMachinePath(bytes, assetRoots, binaryMedia: false, out path);

    /// <summary>Finds the first machine path in bytes, with the exact rule for binary media or the general rule.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="assetRoots">The exact roots to find, for example the asset roots of the cook process.</param>
    /// <param name="binaryMedia">True to use only the exact matches for a copied file that is not text.</param>
    /// <param name="path">The path that was found.</param>
    /// <returns>True if the bytes hold a machine path.</returns>
    internal static bool TryFindMachinePath(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, bool binaryMedia, out string? path)
    {
        // UTF-8 text keeps its ASCII bytes, and Latin-1 maps each byte to one char. UTF-16 text can start at an even or an
        // odd offset.
        string utf8View = Encoding.Latin1.GetString(bytes);
        if (TryFind(utf8View, assetRoots, isUtf8View: true, binaryMedia, out path))
            return true;

        for (int offset = 0; offset < 2 && offset < bytes.Length; offset++)
        {
            int length = (bytes.Length - offset) & ~1;
            string utf16View = Encoding.Unicode.GetString(bytes.Slice(offset, length));
            if (TryFind(utf16View, assetRoots, isUtf8View: false, binaryMedia, out path))
                return true;
        }

        path = null;
        return false;
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

    /// <summary>
    /// Reads a file in chunks and finds the first machine path. Each chunk starts with the last bytes of the chunk before
    /// it, so a path that crosses a chunk edge is in one chunk.
    /// </summary>
    private static bool TryFindMachinePathInFile(string file, bool detectBinaryMedia, out string? path)
    {
        List<string> roots = GetSearchRoots();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + ChunkOverlap);
        try
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            int carried = 0;
            bool binaryMedia = false;
            bool first = true;
            while (true)
            {
                int read = stream.ReadAtLeast(buffer.AsSpan(carried, ChunkSize), ChunkSize, throwOnEndOfStream: false);
                int length = carried + read;
                if (read == 0 && carried == 0)
                {
                    path = null;
                    return false;
                }

                if (first)
                {
                    binaryMedia = detectBinaryMedia && !LooksLikeText(buffer.AsSpan(0, length));
                    first = false;
                }

                if (TryFindMachinePath(buffer.AsSpan(0, length), roots, binaryMedia, out path))
                    return true;

                if (read < ChunkSize)
                    return false;

                carried = Math.Min(ChunkOverlap, length);
                buffer.AsSpan(length - carried, carried).CopyTo(buffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Regex CreateDrivePattern(string pathChar)
        => new("[A-Za-z]:" + Separator + "(?:" + pathChar + "+" + Separator + "){2,}" + pathChar + "*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex CreateUncPattern(string pathChar)
        => new(UncSeparator + UncSeparator + "[A-Za-z0-9._$-]{2,}" + UncSeparator + pathChar + "{2,}" + UncSeparator + pathChar + "{2,}" +
            "(?:" + Separator + pathChar + "+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A path in a Windows user profile folder: a drive, <c>Users</c> or <c>Documents and Settings</c>, and a user name.</summary>
    private static Regex CreateUserFolderPattern(string pathChar)
        => new("[A-Za-z]:" + Separator + "(?i:users|documents and settings)" + Separator + pathChar + "{2,}" +
            "(?:" + Separator + pathChar + "+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool TryFind(string text, IReadOnlyList<string> assetRoots, bool isUtf8View, bool binaryMedia, out string? path)
    {
        foreach (string root in assetRoots)
        {
            string needle = isUtf8View ? Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(root)) : root;
            int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                path = Report(text.Substring(index, Math.Min(MaxReportedLength, text.Length - index)), isUtf8View);
                return true;
            }
        }

        Match match;
        if (binaryMedia)
        {
            match = (isUtf8View ? Utf8UserFolderPath : Utf16UserFolderPath).Match(text);
        }
        else
        {
            match = (isUtf8View ? Utf8DrivePath : Utf16DrivePath).Match(text);
            if (!match.Success)
                match = (isUtf8View ? Utf8UncPath : Utf16UncPath).Match(text);
        }
        if (match.Success)
        {
            path = Report(match.Value, isUtf8View);
            return true;
        }

        path = null;
        return false;
    }

    /// <summary>Gets the text to report for a match. The UTF-8 view is decoded again, and the text stops at a control character.</summary>
    private static string Report(string value, bool isUtf8View)
    {
        string text = isUtf8View ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value)) : value;
        int end = 0;
        while (end < text.Length && !char.IsControl(text[end]) && text[end] != '\uFFFD')
            end++;
        return text[..Math.Max(end, Math.Min(text.Length, 3))];
    }

    /// <summary>
    /// Gets the exact roots to find: the game and engine asset roots of the cook process and the user profile folder, each
    /// with both separator forms.
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
    }
}
