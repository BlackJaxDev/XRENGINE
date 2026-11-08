using System.Globalization;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Records which strip rules wrote an incremental archive, and which paths the strip removed from its entries. An
/// incremental build packs an archive again in full when the record is missing, when the strip version changed, or when
/// the archive is not the file that the record describes, so that every entry passes the current strip once. The list of
/// removed paths lets a build that changes nothing still report what the archive holds.
/// </summary>
internal static class StripLedger
{
    private const int HeaderLines = 3;

    /// <summary>Gets the record path for an archive build in the intermediate directory.</summary>
    /// <param name="intermediateDirectory">The intermediate directory of the project.</param>
    /// <param name="deltaFolderName">The delta folder name of the archive build.</param>
    /// <returns>The record path.</returns>
    public static string GetPath(string intermediateDirectory, string deltaFolderName)
        => Path.Combine(intermediateDirectory, "Build", deltaFolderName + ".strip-ledger");

    /// <summary>True if the record exists, has the current strip version, and describes the archive file.</summary>
    /// <param name="ledgerPath">The record path.</param>
    /// <param name="archivePath">The archive path.</param>
    /// <returns>True if the archive passed the current strip.</returns>
    public static bool IsCurrent(string ledgerPath, string archivePath)
    {
        if (!File.Exists(ledgerPath) || !File.Exists(archivePath))
            return false;

        string[] lines = File.ReadAllLines(ledgerPath);
        return lines.Length >= HeaderLines
            && lines[0] == CookedOutputPathGuard.StripVersion.ToString(CultureInfo.InvariantCulture)
            && lines[1] == new FileInfo(archivePath).Length.ToString(CultureInfo.InvariantCulture)
            && lines[2] == File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the removed paths of the archive entries: the entry path and the removed item.</summary>
    /// <param name="ledgerPath">The record path.</param>
    /// <returns>The removed paths, or an empty list when there is no record.</returns>
    public static List<(string Entry, string Item)> ReadEntries(string ledgerPath)
    {
        List<(string Entry, string Item)> entries = [];
        if (!File.Exists(ledgerPath))
            return entries;

        string[] lines = File.ReadAllLines(ledgerPath);
        for (int i = HeaderLines; i < lines.Length; i++)
        {
            int tab = lines[i].IndexOf('\t');
            if (tab > 0)
                entries.Add((lines[i][..tab], lines[i][(tab + 1)..]));
        }

        return entries;
    }

    /// <summary>Writes the record for an archive that the current strip rules wrote.</summary>
    /// <param name="ledgerPath">The record path.</param>
    /// <param name="archivePath">The archive path.</param>
    /// <param name="entries">The removed paths of the archive entries.</param>
    public static void Save(string ledgerPath, string archivePath, IEnumerable<(string Entry, string Item)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        List<string> lines =
        [
            CookedOutputPathGuard.StripVersion.ToString(CultureInfo.InvariantCulture),
            new FileInfo(archivePath).Length.ToString(CultureInfo.InvariantCulture),
            File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(CultureInfo.InvariantCulture),
        ];
        foreach ((string entry, string item) in entries)
            lines.Add(entry + "\t" + item.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
        File.WriteAllLines(ledgerPath, lines);
    }
}
