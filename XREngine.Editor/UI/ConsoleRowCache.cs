namespace XREngine.Editor.UI.Components;

/// <summary>Keeps stable entry IDs and reuses formatted text while entries stay unchanged.</summary>
public sealed class ConsoleRowCache
{
    private readonly Dictionary<LogEntry, ConsoleCachedEntry> _entries =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, LogEntry> _entriesById = [];
    private readonly HashSet<LogEntry> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ulong> _visibleIds = [];
    private readonly List<LogEntry> _retired = [];
    private readonly List<(ulong Id, string Text)> _visible = [];
    private readonly List<(ulong Id, string Text)> _nextVisible = [];
    private ulong _nextId;

    public IReadOnlyList<(ulong Id, string Text)> VisibleRows => _visible;
    public int TotalCount { get; private set; }
    public int FilteredCount => _visible.Count;

    public bool TryGetVisibleEntry(ulong id, out LogEntry? entry)
    {
        if (_visibleIds.Contains(id) && _entriesById.TryGetValue(id, out entry))
            return true;
        entry = null;
        return false;
    }

    /// <summary>Gets the text from the same snapshot used by the visible row.</summary>
    public bool TryGetVisibleText(ulong id, out string? text)
    {
        if (TryGetVisibleEntry(id, out LogEntry? entry) && entry is not null &&
            _entries.TryGetValue(entry, out ConsoleCachedEntry? cached))
        {
            text = cached.Text;
            return true;
        }
        text = null;
        return false;
    }

    /// <summary>Returns true only when a visible row ID or displayed text changes.</summary>
    public bool Update(IReadOnlyList<ConsoleEntrySnapshot> source, ELogCategory? category, string filter)
    {
        TotalCount = source.Count;
        _nextVisible.Clear();
        _seen.Clear();
        for (int index = 0; index < source.Count; index++)
        {
            ConsoleEntrySnapshot snapshot = source[index];
            LogEntry entry = snapshot.Entry;
            _seen.Add(entry);
            if (!_entries.TryGetValue(entry, out ConsoleCachedEntry? cached))
            {
                if (_nextId == ulong.MaxValue)
                    throw new InvalidOperationException("Console row ID space is exhausted.");
                ulong id = ++_nextId;
                cached = new ConsoleCachedEntry(id, snapshot.RepeatCount, Format(snapshot));
                _entries.Add(entry, cached);
                _entriesById.Add(id, entry);
            }
            else if (cached.RepeatCount != snapshot.RepeatCount)
            {
                cached.RepeatCount = snapshot.RepeatCount;
                cached.Text = Format(snapshot);
            }
            if (category is not null && entry.Category != category ||
                filter.Length != 0 && !entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _nextVisible.Add((cached.Id, cached.Text));
        }
        _retired.Clear();
        foreach (LogEntry entry in _entries.Keys)
            if (!_seen.Contains(entry))
                _retired.Add(entry);
        foreach (LogEntry entry in _retired)
        {
            ulong id = _entries[entry].Id;
            _entries.Remove(entry);
            _entriesById.Remove(id);
        }
        if (!VisibleRowsChanged())
            return false;
        _visible.Clear();
        _visibleIds.Clear();
        for (int index = 0; index < _nextVisible.Count; index++)
        {
            (ulong id, string text) = _nextVisible[index];
            _visible.Add((id, text));
            _visibleIds.Add(id);
        }
        return true;
    }

    private bool VisibleRowsChanged()
    {
        if (_visible.Count != _nextVisible.Count)
            return true;
        for (int index = 0; index < _visible.Count; index++)
            if (_visible[index].Id != _nextVisible[index].Id ||
                !ReferenceEquals(_visible[index].Text, _nextVisible[index].Text))
                return true;
        return false;
    }

    private static string Format(ConsoleEntrySnapshot snapshot)
    {
        LogEntry entry = snapshot.Entry;
        string text = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Category}] {entry.Message}";
        return snapshot.RepeatCount > 1 ? text + $" (x{snapshot.RepeatCount})" : text;
    }
}
