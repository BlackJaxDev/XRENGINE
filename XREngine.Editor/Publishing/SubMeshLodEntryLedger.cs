using System.Globalization;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Records which separate LOD entries (<c>lod/&lt;key&gt;</c>) each cooked file of an archive refers to.
/// An incremental build cooks only the changed files, so it cannot see the entries of the other files.
/// The ledger keeps them, so the build can remove the entries that no cooked file refers to any more.
/// </summary>
/// <remarks>
/// The ledger file holds the write time of the archive that it describes. If the archive changed in another way,
/// the ledger does not load, and the build keeps all LOD entries. A full build writes a new ledger.
/// </remarks>
internal sealed class SubMeshLodEntryLedger
{
    private const string Header = "xre-lod-entry-ledger 1";

    private readonly Dictionary<string, string[]> _entriesBySource = new(StringComparer.Ordinal);

    /// <summary>Gets the path of the ledger file of an archive.</summary>
    /// <param name="intermediateDirectory">The intermediate folder of the build.</param>
    /// <param name="deltaFolderName">The name of the delta folder of the archive.</param>
    public static string GetPath(string intermediateDirectory, string deltaFolderName)
        => Path.Combine(intermediateDirectory, "Build", deltaFolderName + ".lod-entries.txt");

    /// <summary>
    /// Loads the ledger of an archive. It returns null if the file is missing or not valid, or if it describes
    /// another version of the archive. A ledger that does not match is deleted.
    /// </summary>
    /// <param name="ledgerPath">The ledger file.</param>
    /// <param name="archivePath">The archive that the ledger describes.</param>
    public static SubMeshLodEntryLedger? TryLoad(string ledgerPath, string archivePath)
    {
        if (!File.Exists(ledgerPath) || !File.Exists(archivePath))
            return null;

        try
        {
            string[] lines = File.ReadAllLines(ledgerPath);
            if (lines.Length < 2
                || !string.Equals(lines[0], Header, StringComparison.Ordinal)
                || !long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long archiveTicks)
                || archiveTicks != File.GetLastWriteTimeUtc(archivePath).Ticks)
            {
                Delete(ledgerPath);
                return null;
            }

            SubMeshLodEntryLedger ledger = new();
            for (int i = 2; i < lines.Length; i++)
            {
                string line = lines[i];
                int tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    Delete(ledgerPath);
                    return null;
                }

                string[] entries = line[(tab + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries);
                ledger.Set(line[..tab], entries);
            }
            return ledger;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Sets the entries of one cooked file. An empty list removes the file from the ledger.</summary>
    /// <param name="sourcePath">The relative path of the source file.</param>
    /// <param name="entries">The LOD entries that the cooked file refers to.</param>
    public void Set(string sourcePath, string[] entries)
    {
        if (entries.Length == 0)
            _entriesBySource.Remove(sourcePath);
        else
            _entriesBySource[sourcePath] = entries;
    }

    /// <summary>Removes a source file from the ledger.</summary>
    /// <param name="sourcePath">The relative path of the source file.</param>
    public void Remove(string sourcePath)
        => _entriesBySource.Remove(sourcePath);

    /// <summary>Gets the entries that at least one cooked file refers to.</summary>
    public HashSet<string> CollectLiveEntries()
    {
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (string[] entries in _entriesBySource.Values)
            live.UnionWith(entries);
        return live;
    }

    /// <summary>Writes the ledger with the current write time of the archive. If the write fails, the file is deleted.</summary>
    /// <param name="ledgerPath">The ledger file.</param>
    /// <param name="archivePath">The archive that the ledger describes.</param>
    public void Save(string ledgerPath, string archivePath)
    {
        try
        {
            List<string> lines = new(_entriesBySource.Count + 2)
            {
                Header,
                File.GetLastWriteTimeUtc(archivePath).Ticks.ToString(CultureInfo.InvariantCulture),
            };
            foreach ((string sourcePath, string[] entries) in _entriesBySource)
                lines.Add(sourcePath + '\t' + string.Join(';', entries));

            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            File.WriteAllLines(ledgerPath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.LogWarning($"[Build] The LOD entry ledger '{ledgerPath}' could not be written, so the next incremental build keeps all LOD entries: {ex.Message}");
            Delete(ledgerPath);
        }
    }

    /// <summary>Deletes a ledger file if it exists.</summary>
    /// <param name="ledgerPath">The ledger file.</param>
    public static void Delete(string ledgerPath)
    {
        try
        {
            if (File.Exists(ledgerPath))
                File.Delete(ledgerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.LogWarning($"[Build] The stale LOD entry ledger '{ledgerPath}' could not be deleted: {ex.Message}");
        }
    }
}
