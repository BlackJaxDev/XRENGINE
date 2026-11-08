using System.Globalization;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Records which strip rules wrote an incremental archive. An incremental build packs an archive again in full when the
/// record is missing, when the strip version changed, or when the archive is not the file that the record describes, so
/// that every entry passes the current strip once.
/// </summary>
internal static class StripLedger
{
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
        return lines.Length == 3
            && lines[0] == CookedOutputPathGuard.StripVersion.ToString(CultureInfo.InvariantCulture)
            && lines[1] == new FileInfo(archivePath).Length.ToString(CultureInfo.InvariantCulture)
            && lines[2] == File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the record for an archive that the current strip rules wrote.</summary>
    /// <param name="ledgerPath">The record path.</param>
    /// <param name="archivePath">The archive path.</param>
    public static void Save(string ledgerPath, string archivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        File.WriteAllLines(ledgerPath,
        [
            CookedOutputPathGuard.StripVersion.ToString(CultureInfo.InvariantCulture),
            new FileInfo(archivePath).Length.ToString(CultureInfo.InvariantCulture),
            File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(CultureInfo.InvariantCulture),
        ]);
    }
}
