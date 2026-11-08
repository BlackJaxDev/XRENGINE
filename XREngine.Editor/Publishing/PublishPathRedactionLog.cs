using System.Text;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Collects the machine paths and secrets that a publish removed from its output, so the publish can write one warning and
/// one report file. A publish opens one log for its run (<see cref="Begin"/>); the cook steps record into the current log.
/// </summary>
/// <remarks>
/// The current log is process wide, not bound to an execution context: a publish runs its steps as job iterations on
/// changing threads, and only one publish runs at a time.
/// </remarks>
public sealed class PublishPathRedactionLog : IDisposable
{
    private static readonly object CurrentSync = new();
    private static PublishPathRedactionLog? _current;

    private readonly object _sync = new();
    private readonly List<(string File, string Item)> _entries = [];
    private readonly HashSet<(string, string)> _entrySet = [];
    private readonly PublishPathRedactionLog? _parent;
    private bool _disposed;

    private PublishPathRedactionLog(PublishPathRedactionLog? parent)
        => _parent = parent;


    /// <summary>The removed items, each with the file that held it, in the order of the first record.</summary>
    public IReadOnlyList<(string File, string Item)> Entries
    {
        get
        {
            lock (_sync)
                return [.. _entries];
        }
    }

    /// <summary>Opens a log for one publish.</summary>
    /// <returns>The log. Dispose it to close it.</returns>
    public static PublishPathRedactionLog Begin()
    {
        lock (CurrentSync)
        {
            PublishPathRedactionLog log = new(_current);
            _current = log;
            return log;
        }
    }

    /// <summary>Records a removed item in the current log. Without an open log, the record goes nowhere.</summary>
    /// <param name="file">The file that held the item, for example its path relative to the asset folder.</param>
    /// <param name="item">The removed machine path, or a description of a removed secret member.</param>
    public static void Record(string file, string item)
    {
        PublishPathRedactionLog? log;
        lock (CurrentSync)
            log = _current;
        if (log is null)
            return;

        lock (log._sync)
        {
            if (log._entrySet.Add((file, item)))
                log._entries.Add((file, item));
        }
    }

    /// <summary>The number of items in the current log, or zero when no log is open.</summary>
    public static int CurrentCount
    {
        get
        {
            PublishPathRedactionLog? log;
            lock (CurrentSync)
                log = _current;
            if (log is null)
                return 0;
            lock (log._sync)
                return log._entries.Count;
        }
    }

    /// <summary>Gets the items of the current log from an index on.</summary>
    /// <param name="start">The first index, for example a <see cref="CurrentCount"/> read earlier.</param>
    /// <returns>The items, or an empty list when no log is open.</returns>
    public static List<(string File, string Item)> CurrentEntriesFrom(int start)
    {
        PublishPathRedactionLog? log;
        lock (CurrentSync)
            log = _current;
        if (log is null)
            return [];
        lock (log._sync)
            return log._entries.GetRange(Math.Min(start, log._entries.Count), Math.Max(0, log._entries.Count - start));
    }

    /// <summary>Deletes the report of an earlier publish, so a report never describes another publish.</summary>
    /// <param name="reportPath">The report path.</param>
    public static void DeleteReport(string reportPath)
    {
        if (File.Exists(reportPath))
            File.Delete(reportPath);
    }

    /// <summary>Formats the warning text, one line for each file and removed item.</summary>
    /// <returns>The text, or an empty string when nothing was removed.</returns>
    public string FormatReport()
    {
        IReadOnlyList<(string File, string Item)> entries = Entries;
        if (entries.Count == 0)
            return string.Empty;

        StringBuilder text = new();
        text.Append("The publish removed ").Append(entries.Count)
            .AppendLine(" machine path(s) or secret(s) from its output. The source files did not change:");
        foreach ((string file, string item) in entries)
            text.Append("  ").Append(file).Append(": ").AppendLine(item);
        return text.ToString();
    }

    /// <summary>
    /// Writes the report file, and writes the warning to every log that a Release editor shows (the engine log, standard
    /// error, and trace listeners) when the publish removed something. When nothing was removed, the report says so. Write
    /// the report in a folder that is never part of a publish output tree, because it lists the removed machine paths.
    /// </summary>
    /// <param name="reportPath">The report path.</param>
    /// <returns>The report path.</returns>
    public string WriteReport(string reportPath)
    {
        string report = FormatReport();
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, report.Length == 0 ? "The publish removed no machine path and no secret from its output." + Environment.NewLine : report, Encoding.UTF8);
        if (report.Length == 0)
            return reportPath;

        string warning = report + "Report: " + reportPath;
        Debug.LogWarning(warning);
        Console.Error.WriteLine("[WARN] " + warning);
        System.Diagnostics.Trace.TraceWarning(warning);
        return reportPath;
    }

    /// <summary>Closes the log and makes its parent the current log again.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (CurrentSync)
        {
            if (ReferenceEquals(_current, this))
                _current = _parent;
        }
    }
}
