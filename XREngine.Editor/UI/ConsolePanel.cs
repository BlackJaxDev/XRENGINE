using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

/// <summary>Shows the engine console through recycled native rows.</summary>
public partial class ConsolePanel : NativeVirtualTextPanel
{
    private ELogCategory? _category;
    private DateTime _lastRefresh;
    private int _pendingRefresh;
    private readonly List<(ulong Id, string Text)> _visible = [];
    private readonly Dictionary<LogEntry, ulong> _entryIds = [];
    private readonly HashSet<LogEntry> _seenEntries = [];
    private readonly List<LogEntry> _retiredEntries = [];
    private ulong _nextEntryId;
    private string _filter = string.Empty;
    private UITextInputComponent? _filterInput;
    public event Action<string>? FilterChanged;

    public ELogCategory? Category
    {
        get => _category;
        set { _category = value; RefreshRows(); }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? string.Empty;
            if (_filterInput is not null)
                _filterInput.Text = _filter;
            RefreshRows();
            FilterChanged?.Invoke(_filter);
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        Debug.ConsoleEntryAdded += EntryAdded;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
        Debug.ConsoleEntryAdded -= EntryAdded;
        base.OnComponentDeactivated();
    }

    protected override void CreateCommands(SceneNode commands)
    {
        AddCommand("All", () => Category = null);
        AddCommand("General", () => Category = ELogCategory.General);
        AddCommand("UI", () => Category = ELogCategory.UI);
        AddCommand("Clear", () => { Debug.ClearConsoleEntries(); RefreshRows(); });
        SceneNode filterNode = commands.NewChild();
        filterNode.SetTransform<UIBoundableTransform>().Width = 144;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(filterNode);
        _filterInput = input;
        input.SingleLineMode = true;
        input.Text = _filter;
        input.Submitted += field => Filter = field.Text.Trim();
        input.Cancelled += field => field.Text = _filter;
    }

    private void RefreshTick()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastRefresh < TimeSpan.FromMilliseconds(250))
            return;
        if (Interlocked.Exchange(ref _pendingRefresh, 0) == 0 && now - _lastRefresh < TimeSpan.FromSeconds(2))
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    private void EntryAdded(LogEntry _) => Interlocked.Exchange(ref _pendingRefresh, 1);

    protected override void RefreshRows()
    {
        _visible.Clear();
        List<LogEntry> entries = Debug.GetConsoleEntries();
        _seenEntries.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            LogEntry entry = entries[i];
            _seenEntries.Add(entry);
            if (_category is { } category && entry.Category != category)
                continue;
            if (_filter.Length != 0 && !entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            string line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Category}] {entry.Message}";
            if (entry.RepeatCount > 1)
                line += $" (x{entry.RepeatCount})";
            if (!_entryIds.TryGetValue(entry, out ulong id))
            {
                id = ++_nextEntryId;
                _entryIds.Add(entry, id);
            }
            _visible.Add((id, line));
        }
        _retiredEntries.Clear();
        foreach (LogEntry entry in _entryIds.Keys)
            if (!_seenEntries.Contains(entry))
                _retiredEntries.Add(entry);
        foreach (LogEntry entry in _retiredEntries)
            _entryIds.Remove(entry);
        SetRows(_visible);
    }
}
