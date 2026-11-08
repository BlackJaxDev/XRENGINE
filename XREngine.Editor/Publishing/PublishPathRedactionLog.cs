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

    /// <summary>The name of the report file that a publish writes next to its output folder.</summary>
    public const string ReportSuffix = ".publish-report.txt";

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
    /// Writes the warning to every log that a Release editor shows (the engine log, standard error, and trace listeners),
    /// and writes the report file. The report file goes next to the output folder, not into it, so it does not ship.
    /// </summary>
    /// <param name="outputRoot">The output folder of the publish.</param>
    /// <returns>The report file path, or null when nothing was removed.</returns>
    public string? WriteWarningAndReport(string outputRoot)
    {
        string report = FormatReport();
        if (report.Length == 0)
            return null;

        string full = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string reportPath = full + ReportSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, report, Encoding.UTF8);

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
