using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Checks cooked files for absolute machine paths after the cook: the asset roots of the cook process, drive-letter paths,
/// and UNC paths, as UTF-8 and as UTF-16 text. A cook must not ship a machine path, so a hit fails the cook with
/// <see cref="CookedSourcePathException"/>. This check is the last guard behind the write-time path projection
/// (<see cref="XREngine.Core.Files.CookedSourcePathProjection"/>).
/// </summary>
/// <remarks>
/// Cooked files hold binary data, so a short random byte run can look like <c>C:\</c>. The drive-letter and UNC patterns
/// therefore need at least three path parts, which random data almost never gives. A real machine path, for example a
/// path in a user profile, always has them. The asset roots are matched exactly, without case.
/// </remarks>
public static class CookedOutputPathGuard
{
    private const string PathChar = @"[\x20-\x7E-[\\/:*?""<>|]]";
    private const int MaxReportedLength = 260;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int ChunkOverlap = 4096;

    private static readonly Regex DrivePath = new(
        @"[A-Za-z]:[\\/](?:" + PathChar + @"+[\\/]){2,}" + PathChar + "*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UncPath = new(
        @"\\\\[A-Za-z0-9._$-]{2,}\\[A-Za-z0-9._$ -]{2,}\\" + PathChar + "{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Fails the cook if a cooked file holds a machine path.
    /// </summary>
    /// <param name="cookedFile">The cooked file.</param>
    /// <param name="assetLabel">The name of the asset in the error, for example its path relative to the asset folder.</param>
    /// <exception cref="CookedSourcePathException">The file holds a machine path.</exception>
    public static void ThrowIfMachinePath(string cookedFile, string assetLabel)
    {
        // Read the file in chunks. Each chunk starts with the last bytes of the chunk before it, so a path that crosses a
        // chunk edge is in one chunk.
        List<string> roots = GetAssetRoots();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + ChunkOverlap);
        try
        {
            using FileStream stream = new(cookedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            int carried = 0;
            while (true)
            {
                int read = stream.ReadAtLeast(buffer.AsSpan(carried, ChunkSize), ChunkSize, throwOnEndOfStream: false);
                int length = carried + read;
                if (read == 0 && carried == 0)
                    return;

                if (TryFindMachinePath(buffer.AsSpan(0, length), roots, out string? path))
                    throw new CookedSourcePathException(
                        $"Cooked asset '{assetLabel}' holds the machine path '{path}'. A cook must not ship a machine path. " +
                        "Move the source file into the game or engine asset folder, or remove the path from the asset.");

                if (read < ChunkSize)
                    return;

                carried = Math.Min(ChunkOverlap, length);
                buffer.AsSpan(length - carried, carried).CopyTo(buffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Finds the first machine path in cooked bytes.</summary>
    /// <param name="bytes">The cooked bytes.</param>
    /// <param name="assetRoots">The asset roots of the cook process.</param>
    /// <param name="path">The path that was found.</param>
    /// <returns>True if the bytes hold a machine path.</returns>
    internal static bool TryFindMachinePath(ReadOnlySpan<byte> bytes, IReadOnlyList<string> assetRoots, out string? path)
    {
        // UTF-8 text keeps its ASCII bytes, and Latin-1 maps each byte to one char. UTF-16 text can start at an even or an
        // odd offset.
        string utf8View = Encoding.Latin1.GetString(bytes);
        if (TryFind(utf8View, assetRoots, encodeRootsAsUtf8: true, out path))
            return true;

        for (int offset = 0; offset < 2 && offset < bytes.Length; offset++)
        {
            int length = (bytes.Length - offset) & ~1;
            string utf16View = Encoding.Unicode.GetString(bytes.Slice(offset, length));
            if (TryFind(utf16View, assetRoots, encodeRootsAsUtf8: false, out path))
                return true;
        }

        path = null;
        return false;
    }

    private static bool TryFind(string text, IReadOnlyList<string> assetRoots, bool encodeRootsAsUtf8, out string? path)
    {
        foreach (string root in assetRoots)
        {
            string needle = encodeRootsAsUtf8 ? Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(root)) : root;
            int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                path = Trim(text.Substring(index, Math.Min(MaxReportedLength, text.Length - index)));
                return true;
            }
        }

        Match match = DrivePath.Match(text);
        if (!match.Success)
            match = UncPath.Match(text);
        if (match.Success)
        {
            path = Trim(match.Value);
            return true;
        }

        path = null;
        return false;
    }

    /// <summary>Gets the game and engine asset roots of the cook process, with both separator forms.</summary>
    private static List<string> GetAssetRoots()
    {
        List<string> roots = [];
        IAssetSerializationServices services = AssetSerializationServices.Current;
        AddRoot(roots, services.GameAssetsPath);
        AddRoot(roots, services.EngineAssetsPath);
        return roots;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        roots.Add(full);
        string alternate = full.Replace('\\', '/');
        if (!string.Equals(alternate, full, StringComparison.Ordinal))
            roots.Add(alternate);
    }

    private static string Trim(string value)
    {
        int end = 0;
        while (end < value.Length && value[end] >= ' ' && value[end] <= '~')
            end++;
        return value[..Math.Max(end, Math.Min(value.Length, 3))];
    }
}
