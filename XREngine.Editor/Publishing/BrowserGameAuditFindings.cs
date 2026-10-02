namespace XREngine.Editor.Publishing;

/// <summary>Retains a deterministic, size-bounded sample of browser admission findings.</summary>
internal sealed class BrowserGameAuditFindings
{
    private const int MaxFindings = 32;
    private const int MaxFindingLength = 320;
    private readonly SortedSet<string> _lines = new(StringComparer.Ordinal);
    private bool _omitted;

    internal bool Any => _lines.Count != 0;

    internal void Add(string line)
    {
        if (line.Length > MaxFindingLength)
            line = line[..(MaxFindingLength - 11)] + "… [clipped]";
        if (_lines.Contains(line))
            return;
        if (_lines.Count == MaxFindings)
        {
            _omitted = true;
            string last = _lines.Max!;
            if (string.CompareOrdinal(line, last) >= 0)
                return;
            _lines.Remove(last);
        }
        _lines.Add(line);
    }

    internal string Render() => string.Join("; ", _lines)
        + (_omitted ? "; additional findings omitted" : string.Empty);
}
